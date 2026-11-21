using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Scheduling;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.State;
using CubeKeeper.Infrastructure.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Scheduling;

/// <summary>
/// Reinicio diario de BDS (se configura desde el Panel). Avisa por el chat antes y, si se pide, no reinicia cuando hay
/// alguien jugando. Libera la memoria que BDS va acumulando tras días encendido.
/// </summary>
public sealed class RestartScheduler(
    IServerController server,
    IPlayerPresence presence,
    IConsoleLog console,
    AppStateStore state,
    TimeProvider time,
    ILogger<RestartScheduler> logger,
    BackupService? backups = null,
    UpdateService? updates = null) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    public DateTimeOffset? NextRunAt { get; private set; }

    public RestartSettings Settings => state.Current.Restart;

    public RestartRecord? LastRun => state.Current.LastRestart;

    /// <summary>Guarda los ajustes. Devuelve un error en castellano o null si están bien.</summary>
    public string? SaveSettings(bool enabled, string time, string warnings, bool skipIfPlayers)
    {
        if (DailySchedule.ParseTime(time) is null)
        {
            return "La hora no es válida: usa HH:mm, por ejemplo 05:30.";
        }
        var parsed = DailySchedule.ParseWarnings(warnings);
        if (!string.IsNullOrWhiteSpace(warnings) && parsed.Count == 0)
        {
            return "Los avisos no son válidos: minutos separados por comas, por ejemplo 5,1.";
        }
        if (parsed.Any(m => m > 60))
        {
            return "Los avisos pueden ser de 60 minutos como mucho.";
        }
        state.Update(s => s with { Restart = new RestartSettings(enabled, time.Trim(), string.Join(',', parsed), skipIfPlayers) });
        NextRunAt = null;
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, time);
        try
        {
            do
            {
                await RunIfDueAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task RunIfDueAsync(CancellationToken ct)
    {
        var settings = Settings;
        if (!settings.Enabled || DailySchedule.ParseTime(settings.Time) is not { } at)
        {
            NextRunAt = null;
            return;
        }
        var now = time.GetUtcNow();
        NextRunAt ??= DailySchedule.Next(now, at, time.LocalTimeZone);
        var warnings = DailySchedule.ParseWarnings(settings.WarningMinutes).OrderDescending().ToList();
        var target = NextRunAt.Value;
        var firstWarning = warnings.Count > 0 ? TimeSpan.FromMinutes(warnings[0]) : TimeSpan.Zero;
        if (now < target - firstWarning)
        {
            return;
        }
        NextRunAt = DailySchedule.Next(target.AddMinutes(1), at, time.LocalTimeZone);
        await RunAsync(target, warnings, settings.SkipIfPlayersOnline, ct).ConfigureAwait(false);
    }

    private async Task RunAsync(DateTimeOffset target, IReadOnlyList<int> warnings, bool skipIfPlayers, CancellationToken ct)
    {
        if (server.Snapshot.Status != ServerStatus.Running)
        {
            Record(false, "No se reinició: el servidor no estaba en marcha.");
            return;
        }
        if (skipIfPlayers && presence.Online.Count > 0)
        {
            Record(false, $"No se reinició: había gente jugando ({string.Join(", ", presence.Online.Select(p => p.Name))}).");
            return;
        }
        foreach (var minutes in warnings)
        {
            await DelayUntilAsync(target - TimeSpan.FromMinutes(minutes), ct).ConfigureAwait(false);
            if (server.Snapshot.Status != ServerStatus.Running)
            {
                Record(false, "No se reinició: el servidor se paró durante los avisos.");
                return;
            }
            await server.SendCommandAsync(
                $"say El servidor se reiniciará en {minutes} minuto{(minutes == 1 ? "" : "s")} para mantenimiento. Vuelve a entrar en un momento.", ct).ConfigureAwait(false);
        }
        await DelayUntilAsync(target, ct).ConfigureAwait(false);

        // No a la vez que una copia o una actualización: se espera hasta 10 minutos.
        var deadline = time.GetUtcNow().AddMinutes(10);
        while (Busy && time.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), time, ct).ConfigureAwait(false);
        }
        if (Busy)
        {
            Record(false, "No se reinició: había una copia o una actualización en curso.");
            return;
        }
        if (server.Snapshot.Status != ServerStatus.Running)
        {
            Record(false, "No se reinició: el servidor ya no estaba en marcha.");
            return;
        }
        try
        {
            console.Append(ConsoleLineKind.System, "Reinicio programado.");
            await server.RestartAsync(ct).ConfigureAwait(false);
            Record(true, "Reiniciado.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falló el reinicio programado");
            Record(false, $"Falló el reinicio: {ex.Message}");
        }
    }

    private bool Busy => backups?.IsBusy == true || updates?.IsBusy == true;

    private async Task DelayUntilAsync(DateTimeOffset when, CancellationToken ct)
    {
        var wait = when - time.GetUtcNow();
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, time, ct).ConfigureAwait(false);
        }
    }

    private void Record(bool restarted, string message)
    {
        logger.LogInformation("Reinicio programado: {Message}", message);
        if (!restarted)
        {
            console.Append(ConsoleLineKind.System, "Reinicio programado: " + message);
        }
        state.Update(s => s with { LastRestart = new RestartRecord(time.GetUtcNow(), restarted, message) });
    }
}
