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
/// Reinicio diario de BDS (se configura desde el Panel). Avisa por el chat antes y, si se pide, espera a que no quede
/// nadie jugando (avisando cada 10 minutos). Libera la memoria que BDS va acumulando tras días encendido.
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

    /// <summary>Cada cuánto se recuerda por el chat que hay un reinicio pendiente.</summary>
    internal static readonly TimeSpan WaitingReminder = TimeSpan.FromMinutes(10);

    /// <summary>Como mucho se espera esto a que salgan todos; después se deja para el día siguiente.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromHours(20);

    /// <summary>Desde cuándo se espera a que no quede nadie para reiniciar (null si no hay reinicio pendiente).</summary>
    public DateTimeOffset? WaitingSince { get; private set; }

    public DateTimeOffset? NextRunAt { get; private set; }

    public RestartSettings Settings => state.Current.Restart;

    public RestartRecord? LastRun => state.Current.LastRestart;

    /// <summary>Guarda los ajustes. Devuelve un error en castellano o null si están bien.</summary>
    public string? SaveSettings(bool enabled, string time, string warnings, bool waitForEmpty)
    {
        if (DailySchedule.ParseTime(time) is null)
        {
            return "La hora no es válida: usa HH:mm, por ejemplo 05:30.";
        }
        var parsed = DailySchedule.ParseWarnings(warnings);
        if (!string.IsNullOrWhiteSpace(warnings) && parsed.Count == 0)
        {
            return "Los avisos no son válidos: minutos separados por comas, por ejemplo 30,15,5,1.";
        }
        if (parsed.Any(m => m > 60))
        {
            return "Los avisos pueden ser de 60 minutos como mucho.";
        }
        state.Update(s => s with { Restart = new RestartSettings(enabled, time.Trim(), string.Join(',', parsed), waitForEmpty) });
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
        await RunAsync(target, warnings, settings.WaitForEmpty, ct).ConfigureAwait(false);
    }

    private async Task RunAsync(DateTimeOffset target, IReadOnlyList<int> warnings, bool waitForEmpty, CancellationToken ct)
    {
        if (server.Snapshot.Status != ServerStatus.Running)
        {
            Record(false, "No se reinició: el servidor no estaba en marcha.");
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
            if (waitForEmpty && presence.Online.Count == 0)
            {
                continue; // Nadie a quien avisar.
            }
            var plural = minutes == 1 ? "" : "s";
            await server.SendCommandAsync(waitForEmpty
                ? $"say Reinicio de mantenimiento en {minutes} minuto{plural}. Si seguís jugando, esperará a que salgáis todos."
                : $"say El servidor se reiniciará en {minutes} minuto{plural} para mantenimiento. Vuelve a entrar en un momento.", ct).ConfigureAwait(false);
        }
        await DelayUntilAsync(target, ct).ConfigureAwait(false);

        DateTimeOffset? waitedFrom = null;
        if (waitForEmpty && presence.Online.Count > 0)
        {
            waitedFrom = time.GetUtcNow();
            if (!await WaitUntilEmptyAsync(ct).ConfigureAwait(false))
            {
                return;
            }
        }

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
            Record(true, waitedFrom is { } from
                ? $"Reiniciado tras esperar a que salieran todos (desde las {TimeZoneInfo.ConvertTime(from, time.LocalTimeZone):HH:mm})."
                : "Reiniciado.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falló el reinicio programado");
            Record(false, $"Falló el reinicio: {ex.Message}");
        }
    }

    /// <summary>
    /// Espera a que no quede nadie, recordándolo por el chat cada 10 minutos. Devuelve false (y lo anota) si el
    /// servidor se para mientras tanto o pasan <see cref="MaxWait"/>.
    /// </summary>
    private async Task<bool> WaitUntilEmptyAsync(CancellationToken ct)
    {
        var since = time.GetUtcNow();
        WaitingSince = since;
        console.Append(ConsoleLineKind.System,
            $"Reinicio programado: hay gente jugando ({Names()}); se reiniciará cuando no quede nadie.");
        try
        {
            var nextReminder = since;
            while (presence.Online.Count > 0)
            {
                if (server.Snapshot.Status != ServerStatus.Running)
                {
                    Record(false, "No se reinició: el servidor se paró mientras se esperaba a que salieran todos.");
                    return false;
                }
                var now = time.GetUtcNow();
                if (now - since >= MaxWait)
                {
                    Record(false, $"No se reinició: seguía habiendo gente jugando ({Names()}) tras {MaxWait.TotalHours:0} horas.");
                    return false;
                }
                if (now >= nextReminder)
                {
                    await server.SendCommandAsync(
                        "say Reinicio de mantenimiento pendiente: el servidor se reiniciará en cuanto salgáis todos.", ct).ConfigureAwait(false);
                    nextReminder = now + WaitingReminder;
                }
                await Task.Delay(Tick, time, ct).ConfigureAwait(false);
            }
            console.Append(ConsoleLineKind.System, "Reinicio programado: ya no queda nadie, se reinicia.");
            return true;
        }
        finally
        {
            WaitingSince = null;
        }
    }

    private string Names() => string.Join(", ", presence.Online.Select(p => p.Name));

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
