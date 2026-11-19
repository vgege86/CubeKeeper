using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Backups;
using CubeKeeper.Core.Server;
using CubeKeeper.Core.Versions;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Updates;

/// <summary>
/// Actualiza BDS de forma segura: avisa a los jugadores, hace una copia del mundo, instala y comprueba que arranca.
/// Si la versión nueva no llega a "En marcha", vuelve a la anterior y restaura la copia.
/// </summary>
public sealed class UpdateService(
    IServerController server,
    IServerInstaller installer,
    IVersionSource versions,
    BackupService backups,
    IPlayerPresence presence,
    IConsoleLog console,
    DataPaths paths,
    AppStateStore state,
    IOptions<CubeKeeperOptions> options,
    TimeProvider time,
    ILogger<UpdateService> logger,
    Notifications.Notifier? notifier = null)
{
    private bool _scheduledRun;
    private readonly CubeKeeperOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsBusy => _gate.CurrentCount == 0;

    public bool IsPinned => !string.IsNullOrWhiteSpace(_options.Bds.PinnedVersion);

    /// <summary>Actualización automática activa (activada y sin versión fijada).</summary>
    public bool AutoUpdateActive => _options.Updates.AutoUpdate && !IsPinned;

    public UpdateRecord? LastUpdate => state.Current.LastUpdate;

    /// <summary>Versión anterior descargada en /data/versions a la que se puede volver, o null.</summary>
    public string? PreviousVersion
    {
        get
        {
            if (!Directory.Exists(paths.Versions) || installer.InstalledVersion is not { } current ||
                !BdsVersion.TryParse(current, out var installed))
            {
                return null;
            }
            return Directory.EnumerateDirectories(paths.Versions)
                .Select(Path.GetFileName)
                .Select(n => BdsVersion.TryParse(n, out var v) ? v : null)
                .Where(v => v is not null && !v.Equals(installed) && File.Exists(Path.Combine(paths.Versions, v.Text, "bedrock_server")))
                .OrderDescending()
                .FirstOrDefault()?.Text;
        }
    }

    /// <summary>Instala <paramref name="release"/>. Con avisos, si hay gente jugando, espera los minutos indicados avisando por el chat.</summary>
    public async Task<UpdateRecord> UpdateAsync(BdsRelease release, IReadOnlyList<int> warningMinutes, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await UpdateCoreAsync(release, warningMinutes, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Vuelve a la versión anterior descargada (sin tocar el mundo, del que antes se hace copia).</summary>
    public async Task<UpdateRecord> RollbackAsync(CancellationToken ct = default)
    {
        var previous = PreviousVersion ?? throw new InvalidOperationException("No hay ninguna versión anterior descargada.");
        return await UpdateAsync(LocalRelease(previous), [], ct).ConfigureAwait(false);
    }

    /// <summary>Tarea diaria: si hay una versión más nueva, actualiza con avisos.</summary>
    public async Task RunScheduledAsync(CancellationToken ct)
    {
        if (!AutoUpdateActive || !installer.IsInstalled)
        {
            return;
        }
        var latest = await versions.GetLatestAsync(_options.Bds.Channel, ct).ConfigureAwait(false);
        if (latest is null || !BdsVersion.TryParse(installer.InstalledVersion, out var installed) || installed is null || latest.Version <= installed)
        {
            logger.LogInformation("Comprobación programada: no hay versión nueva");
            return;
        }
        if (_options.Updates.SkipIfPlayersOnline && presence.Online.Count > 0)
        {
            console.Append(ConsoleLineKind.System, $"Hay una versión nueva ({latest.Version}), pero hay gente jugando: se deja para mañana.");
            return;
        }
        _scheduledRun = true;
        try
        {
            await UpdateAsync(latest, Core.Scheduling.DailySchedule.ParseWarnings(_options.Updates.WarningMinutes), ct).ConfigureAwait(false);
        }
        finally
        {
            _scheduledRun = false;
        }
    }

    private async Task<UpdateRecord> UpdateCoreAsync(BdsRelease release, IReadOnlyList<int> warningMinutes, CancellationToken ct)
    {
        var from = installer.InstalledVersion;
        if (!installer.IsInstalled)
        {
            await server.InstallAsync(release, null, ct).ConfigureAwait(false);
            return Record(from, release.Version.Text, true, $"Instalada la versión {release.Version}.");
        }

        await WarnPlayersAsync(release, warningMinutes, ct).ConfigureAwait(false);

        // Copia del mundo antes de tocar nada; si no se puede hacer, no se actualiza.
        BackupFile? safety = null;
        var world = await backups.GetWorldNameAsync(ct).ConfigureAwait(false);
        if (Directory.Exists(backups.WorldDirectory(world)))
        {
            try
            {
                safety = await backups.CreateAsync(BackupKind.PreUpdate, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var msg = $"No se actualiza: falló la copia de seguridad previa ({ex.Message}).";
                console.Append(ConsoleLineKind.System, msg);
                return Record(from, release.Version.Text, false, msg);
            }
        }

        var wantRunning = state.Current.DesiredRunning || server.Snapshot.Status.IsProcessAlive();
        string? failure = null;
        try
        {
            await server.InstallAsync(release, null, ct).ConfigureAwait(false);
            if (wantRunning && !await WaitForRunningAsync(ct).ConfigureAwait(false))
            {
                failure = $"la versión {release.Version} no llegó a arrancar en {_options.Updates.HealthTimeoutSeconds} s";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex.Message;
        }

        if (failure is null)
        {
            return Record(from, release.Version.Text, true, $"Actualizado de {from} a {release.Version}.");
        }

        console.Append(ConsoleLineKind.System, $"Fallo al actualizar: {failure}. Volviendo a la versión {from}…");
        logger.LogError("Fallo al actualizar a {Version}: {Failure}. Se vuelve a {From}", release.Version, failure, from);
        try
        {
            // Primero el mundo (con el servidor fallido parado) y después la versión anterior, que lo arranca:
            // así la versión antigua nunca abre un mundo que haya tocado la nueva.
            if (server.Snapshot.Status.IsProcessAlive())
            {
                await server.StopAsync(ct).ConfigureAwait(false);
                state.Update(s => s with { DesiredRunning = wantRunning });
            }
            if (safety is not null)
            {
                await backups.RestoreAsync(safety.FileName, ct).ConfigureAwait(false);
            }
            if (from is not null)
            {
                await server.InstallAsync(LocalRelease(from), null, ct).ConfigureAwait(false);
            }
            if (wantRunning && !server.Snapshot.Status.IsProcessAlive())
            {
                await server.StartAsync(ct).ConfigureAwait(false);
            }
            return Record(from, release.Version.Text, false, $"Falló la actualización a {release.Version} ({failure}); se ha vuelto a {from}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "También falló la vuelta atrás");
            return Record(from, release.Version.Text, false,
                $"Falló la actualización a {release.Version} ({failure}) y también la vuelta atrás ({ex.Message}). Revisa la consola.");
        }
    }

    private async Task WarnPlayersAsync(BdsRelease release, IReadOnlyList<int> warningMinutes, CancellationToken ct)
    {
        if (warningMinutes.Count == 0 || presence.Online.Count == 0 || server.Snapshot.Status != ServerStatus.Running)
        {
            return;
        }
        for (var i = 0; i < warningMinutes.Count; i++)
        {
            var m = warningMinutes[i];
            try
            {
                await server.SendCommandAsync(
                    $"say El servidor se reiniciará en {m} minuto{(m == 1 ? "" : "s")} para actualizarse a la versión {release.Version}.", ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return; // Se ha parado entretanto.
            }
            var next = i + 1 < warningMinutes.Count ? warningMinutes[i + 1] : 0;
            await Task.Delay(TimeSpan.FromMinutes(m - next), time, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForRunningAsync(CancellationToken ct)
    {
        var deadline = time.GetUtcNow().AddSeconds(_options.Updates.HealthTimeoutSeconds);
        while (time.GetUtcNow() < deadline)
        {
            switch (server.Snapshot.Status)
            {
                case ServerStatus.Running:
                    return true;
                case ServerStatus.Failed:
                    return false;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), time, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Versión ya descargada en /data/versions: el instalador no vuelve a descargarla.</summary>
    private BdsRelease LocalRelease(string version) =>
        new(BdsVersion.Parse(version), new Uri($"https://localhost/bedrock-server-{version}.zip"), _options.Bds.Channel, "copia local");

    private UpdateRecord Record(string? from, string to, bool ok, string message)
    {
        var record = new UpdateRecord(time.GetUtcNow(), from, to, ok, message);
        state.Update(s => s with { LastUpdate = record });
        console.Append(ConsoleLineKind.System, message);
        if (notifier is not null && (!ok || _scheduledRun))
        {
            _ = notifier.NotifyAsync(new Core.Notifications.Notification(
                Core.Notifications.NotificationKind.Update,
                ok ? $"Servidor actualizado a {to}" : $"Falló la actualización a {to}",
                message,
                $"{to}:{ok}",
                ok ? Core.Notifications.NotificationPriority.Default : Core.Notifications.NotificationPriority.High,
                "/"));
        }
        return record;
    }
}
