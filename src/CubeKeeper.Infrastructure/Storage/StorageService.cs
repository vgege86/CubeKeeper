using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Storage;

/// <summary>Espacio de un disco (dataset) donde CubeKeeper guarda cosas.</summary>
public sealed record DiskUsage(string Label, string Path, long Total, long Free, bool Low)
{
    public double FreePercent => Total > 0 ? Free * 100.0 / Total : 0;
}

/// <summary>Cuánto ocupa cada parte de /data.</summary>
public sealed record FolderUsage(string Label, string Path, long Bytes);

public sealed record StorageReport(DateTimeOffset At, IReadOnlyList<DiskUsage> Disks, IReadOnlyList<FolderUsage> Folders)
{
    public IEnumerable<DiskUsage> LowDisks => Disks.Where(d => d.Low);
}

/// <summary>
/// Mide el espacio libre de /data y del segundo destino de copias, y lo que ocupa cada carpeta de /data. Se considera
/// poco espacio por debajo del porcentaje o de los GB configurados (lo que salte antes).
/// </summary>
public sealed class StorageService(DataPaths paths, BackupService backups, IOptions<CubeKeeperOptions> options, TimeProvider time, ILogger<StorageService> logger)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private readonly StorageOptions _options = options.Value.Storage;
    private readonly Lock _lock = new();
    private StorageReport? _cached;

    public StorageOptions Thresholds => _options;

    /// <summary>Informe reciente (se recalcula como mucho cada 10 minutos, salvo <paramref name="refresh"/>).</summary>
    public StorageReport GetReport(bool refresh = false)
    {
        lock (_lock)
        {
            if (!refresh && _cached is { } c && time.GetUtcNow() - c.At < CacheFor)
            {
                return c;
            }
        }
        var report = Measure();
        lock (_lock)
        {
            _cached = report;
        }
        return report;
    }

    /// <summary>Solo el espacio libre de los discos (rápido: no recorre carpetas).</summary>
    public IReadOnlyList<DiskUsage> MeasureDisks()
    {
        var disks = new List<DiskUsage>();
        AddDisk(disks, "Datos (/data)", paths.Root);
        if (backups.SecondaryAvailable)
        {
            AddDisk(disks, "Segundo destino de copias", backups.SecondaryPath!);
        }
        return disks;
    }

    public bool IsLow(long total, long free) =>
        total > 0 && (free * 100.0 / total < _options.WarnPercent || free < (long)(_options.WarnGigabytes * 1024 * 1024 * 1024));

    private StorageReport Measure()
    {
        var folders = new List<FolderUsage>
        {
            Folder("Copias del mundo", paths.Backups),
            Folder("Servidor y mundos", paths.Server),
            Folder("Versiones de BDS descargadas", paths.Versions),
            Folder("Mapa generado", Path.Combine(paths.Root, "map")),
            Folder("Motores de mapa", Path.Combine(paths.Root, "tools")),
            Folder("Temporales", paths.Downloads),
        };
        if (backups.SecondaryAvailable)
        {
            folders.Add(Folder("Copias en el segundo destino", backups.SecondaryPath!));
        }
        return new StorageReport(time.GetUtcNow(), MeasureDisks(), folders.Where(f => f.Bytes > 0).ToList());
    }

    private void AddDisk(List<DiskUsage> disks, string label, string path)
    {
        try
        {
            var drive = new DriveInfo(path);
            disks.Add(new DiskUsage(label, path, drive.TotalSize, drive.AvailableFreeSpace, IsLow(drive.TotalSize, drive.AvailableFreeSpace)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "No se pudo medir el espacio de {Path}", path);
        }
    }

    private FolderUsage Folder(string label, string path)
    {
        long bytes = 0;
        if (Directory.Exists(path))
        {
            try
            {
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    bytes += file.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "No se pudo medir {Path}", path);
            }
        }
        return new FolderUsage(label, path, bytes);
    }
}
