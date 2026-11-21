using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Notifications;
using CubeKeeper.Core.Resources;
using CubeKeeper.Infrastructure.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Resources;

/// <summary>Una medida: CPU y memoria de bedrock_server, y memoria del contenedor frente a su límite.</summary>
public sealed record ResourceSample(DateTimeOffset At, double? ServerCpuPercent, long? ServerMemory, long? ContainerMemory, long? ContainerLimit)
{
    public double? ContainerMemoryPercent => ContainerMemory is { } m && ContainerLimit is { } l && l > 0 ? m * 100.0 / l : null;
}

/// <summary>
/// Mide cada 30 s el uso de CPU y memoria (de /proc y de los cgroups, v1 o v2) y guarda las últimas 24 h en memoria.
/// Si la memoria del contenedor pasa del 90 % de su límite varias medidas seguidas, avisa una vez (hasta bajar del 80 %).
/// </summary>
public sealed class ResourceMonitor(IServerController server, TimeProvider time, ILogger<ResourceMonitor> logger, Notifier? notifier = null) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const int Keep = 24 * 60 * 2; // 24 h de medidas cada 30 s
    private const double TicksPerSecond = 100; // USER_HZ en Linux
    private const double AlertPercent = 90;
    private const double ClearPercent = 80;
    private const int AlertAfter = 3;

    private readonly Queue<ResourceSample> _history = new();
    private readonly Lock _lock = new();
    private (int Pid, long Ticks, DateTimeOffset At)? _lastCpu;
    private int _highCount;
    private bool _alerted;

    /// <summary>Raíces de /proc y de los cgroups (cambiables en las pruebas).</summary>
    public string ProcRoot { get; init; } = "/proc";

    public string CgroupRoot { get; init; } = "/sys/fs/cgroup";

    /// <summary>False fuera de Linux (en desarrollo en Windows): no hay nada que medir.</summary>
    public bool Supported => Directory.Exists(ProcRoot);

    public event Action? Sampled;

    public IReadOnlyList<ResourceSample> History(TimeSpan window)
    {
        var since = time.GetUtcNow() - window;
        lock (_lock)
        {
            return _history.Where(s => s.At >= since).ToList();
        }
    }

    public ResourceSample? Latest
    {
        get
        {
            lock (_lock)
            {
                return _history.Count > 0 ? _history.Last() : null;
            }
        }
    }

    /// <summary>CPU disponibles para el contenedor (su límite, o las del equipo).</summary>
    public double Cpus => ReadCpuLimit() ?? Environment.ProcessorCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Supported)
        {
            return;
        }
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                await SampleAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task<ResourceSample> SampleAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        double? cpu = null;
        long? rss = null;
        if (server.Snapshot.ProcessId is int pid)
        {
            rss = ProcFs.ParseRssBytes(Read(Path.Combine(ProcRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "status")) ?? "");
            var ticks = ProcFs.ParseCpuTicks(Read(Path.Combine(ProcRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "stat")) ?? "");
            if (ticks is long t)
            {
                if (_lastCpu is { } last && last.Pid == pid && now > last.At && t >= last.Ticks)
                {
                    var seconds = (now - last.At).TotalSeconds;
                    cpu = Math.Clamp((t - last.Ticks) / TicksPerSecond / seconds / Cpus * 100, 0, 100);
                }
                _lastCpu = (pid, t, now);
            }
        }
        else
        {
            _lastCpu = null;
        }
        var (memory, limit) = ReadContainerMemory();
        var sample = new ResourceSample(now, cpu, rss, memory, limit);
        lock (_lock)
        {
            _history.Enqueue(sample);
            while (_history.Count > Keep)
            {
                _history.Dequeue();
            }
        }
        Sampled?.Invoke();
        await CheckAlertAsync(sample, ct).ConfigureAwait(false);
        return sample;
    }

    private async Task CheckAlertAsync(ResourceSample sample, CancellationToken ct)
    {
        if (sample.ContainerMemoryPercent is not double percent)
        {
            return;
        }
        if (percent < ClearPercent)
        {
            _highCount = 0;
            _alerted = false;
            return;
        }
        _highCount = percent >= AlertPercent ? _highCount + 1 : 0;
        if (_highCount < AlertAfter || _alerted)
        {
            return;
        }
        logger.LogWarning("Memoria del contenedor al {Percent:0}% de su límite", percent);
        if (notifier is null)
        {
            return;
        }
        var result = await notifier.NotifyAsync(new Notification(
            NotificationKind.Maintenance,
            "El servidor se está quedando sin memoria",
            $"El contenedor usa el {percent:0}% de su límite de memoria. Reinicia el servidor (o activa el reinicio programado) " +
            "o sube el límite de memoria de la app en TrueNAS: si lo alcanza, el sistema lo cierra de golpe.",
            "memory",
            NotificationPriority.High,
            "/"), ct).ConfigureAwait(false);
        _alerted = result.Sent || !notifier.Configured;
    }

    private (long? Memory, long? Limit) ReadContainerMemory()
    {
        // cgroup v2 (TrueNAS SCALE) y, si no, v1. Como «docker stats», sin la caché de archivos que se puede liberar.
        if (Read(Path.Combine(CgroupRoot, "memory.current")) is { } current)
        {
            var used = ProcFs.ParseLimit(current);
            var inactive = ProcFs.ParseStat(Read(Path.Combine(CgroupRoot, "memory.stat")) ?? "", "inactive_file") ?? 0;
            return (used is long u ? Math.Max(0, u - inactive) : null, ProcFs.ParseLimit(Read(Path.Combine(CgroupRoot, "memory.max")) ?? "max"));
        }
        if (Read(Path.Combine(CgroupRoot, "memory", "memory.usage_in_bytes")) is { } usage)
        {
            var used = ProcFs.ParseLimit(usage);
            var inactive = ProcFs.ParseStat(Read(Path.Combine(CgroupRoot, "memory", "memory.stat")) ?? "", "total_inactive_file") ?? 0;
            return (used is long u ? Math.Max(0, u - inactive) : null, ProcFs.ParseLimit(Read(Path.Combine(CgroupRoot, "memory", "memory.limit_in_bytes")) ?? "max"));
        }
        return (null, null);
    }

    private double? ReadCpuLimit()
    {
        if (Read(Path.Combine(CgroupRoot, "cpu.max")) is { } max)
        {
            return ProcFs.ParseCpuMax(max);
        }
        if (Read(Path.Combine(CgroupRoot, "cpu", "cpu.cfs_quota_us")) is { } quota &&
            Read(Path.Combine(CgroupRoot, "cpu", "cpu.cfs_period_us")) is { } period)
        {
            return ProcFs.ParseCpuMax($"{quota.Trim().Replace("-1", "max", StringComparison.Ordinal)} {period.Trim()}");
        }
        return null;
    }

    private static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
