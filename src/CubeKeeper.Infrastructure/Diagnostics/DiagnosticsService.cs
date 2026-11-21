using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using CubeKeeper.Core.Abstractions;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Maps;
using CubeKeeper.Infrastructure.Security;
using CubeKeeper.Infrastructure.State;
using CubeKeeper.Infrastructure.Storage;
using CubeKeeper.Infrastructure.Versions;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Diagnostics;

/// <summary>
/// Prepara un .zip para pedir ayuda: estado, versiones, configuración sin secretos, espacio, copias, registro de
/// CubeKeeper, consola del servidor, server.properties y el último registro del mapa. Los secretos que se conocen
/// (contraseña inicial, secreto de las claves, token y tema de ntfy) se sustituyen por *** en todo el contenido.
/// </summary>
public sealed class DiagnosticsService(
    IServerController server,
    IServerInstaller installer,
    IConsoleLog console,
    RecentLogs logs,
    AppStateStore state,
    DataPaths paths,
    VersionPin pin,
    BackupService backups,
    StorageService storage,
    MapService maps,
    KeyEncryptionSecret keySecret,
    IOptions<CubeKeeperOptions> options,
    TimeProvider time)
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    public async Task WriteZipAsync(Stream output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var secrets = KnownSecrets();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await AddAsync(zip, "LEEME.txt", Readme(), secrets, ct).ConfigureAwait(false);
        await AddAsync(zip, "resumen.txt", Summary(), secrets, ct).ConfigureAwait(false);
        await AddAsync(zip, "disco.txt", Disk(), secrets, ct).ConfigureAwait(false);
        await AddAsync(zip, "copias.txt", Backups(), secrets, ct).ConfigureAwait(false);
        await AddAsync(zip, "registro-cubekeeper.txt", AppLog(), secrets, ct).ConfigureAwait(false);
        await AddAsync(zip, "consola-servidor.txt", Console(), secrets, ct).ConfigureAwait(false);
        await AddAsync(zip, "mapa.txt", Map(), secrets, ct).ConfigureAwait(false);
        if (File.Exists(paths.ServerProperties))
        {
            await AddAsync(zip, "server.properties", await File.ReadAllTextAsync(paths.ServerProperties, ct).ConfigureAwait(false), secrets, ct).ConfigureAwait(false);
        }
    }

    public string FileName() => $"cubekeeper-diagnostico-{TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone):yyyyMMdd-HHmm}.zip";

    private static async Task AddAsync(ZipArchive zip, string name, string content, IReadOnlyList<string> secrets, CancellationToken ct)
    {
        foreach (var secret in secrets)
        {
            content = content.Replace(secret, "***", StringComparison.Ordinal);
        }
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = await entry.OpenAsync(ct).ConfigureAwait(false);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
    }

    private List<string> KnownSecrets()
    {
        var o = options.Value;
        return new[] { o.Admin.Password, keySecret.Value, o.Notifications.NtfyToken, o.Notifications.NtfyTopic, state.Current.Ntfy?.Topic }
            .Where(s => !string.IsNullOrWhiteSpace(s) && s.Length >= 4)
            .Select(s => s!)
            .Distinct()
            .OrderByDescending(s => s.Length)
            .ToList();
    }

    private string When(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, time.LocalTimeZone).ToString("dd/MM/yyyy HH:mm:ss", Es);

    private string Readme() => $"""
        Diagnóstico de CubeKeeper generado el {When(time.GetUtcNow())}.

        Contiene: estado del servidor, versiones, configuración (sin contraseñas, tokens ni secretos), espacio en disco,
        lista de copias, el registro reciente de CubeKeeper, la consola reciente del servidor, server.properties y el
        último registro del mapa.

        No contiene: el mundo, las copias, la base de datos, las claves de /data/keys ni state.json.
        Sí contiene nombres de jugadores y XUID (aparecen en la consola) y las IP del panel que salgan en el registro.
        """;

    private string Summary()
    {
        var o = options.Value;
        var s = state.Current;
        var snap = server.Snapshot;
        var b = new StringBuilder();
        b.AppendLine("CUBEKEEPER");
        b.AppendLine($"  Versión: {Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?"}");
        b.AppendLine($"  .NET: {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        b.AppendLine($"  Zona horaria: {time.LocalTimeZone.Id}");
        b.AppendLine($"  Claves cifradas (CUBEKEEPER_KEY_SECRET): {(keySecret.IsSet ? "sí" : "no")}");
        b.AppendLine();
        b.AppendLine("SERVIDOR (BDS)");
        b.AppendLine($"  Estado: {snap.Status} · deseado en marcha: {(s.DesiredRunning ? "sí" : "no")}");
        b.AppendLine($"  Versión instalada: {installer.InstalledVersion ?? "—"} · en marcha: {snap.RunningVersion ?? "—"}");
        b.AppendLine($"  Versión fijada: {pin.Version ?? "ninguna"}{(pin.FromPanel ? " (desde el panel)" : pin.IsPinned ? " (CUBEKEEPER_BDS_VERSION)" : "")}");
        b.AppendLine($"  Variable CUBEKEEPER_BDS_VERSION: {pin.EnvironmentVersion ?? "—"} · canal: {o.Bds.Channel} · EULA aceptada: {(o.Bds.AcceptEula ? "sí" : "no")}");
        b.AppendLine($"  Último error: {snap.LastError ?? "—"}");
        if (s.LastUpdate is { } u)
        {
            b.AppendLine($"  Última actualización: {When(u.At)} {u.From ?? "—"} → {u.To} · {(u.Succeeded ? "bien" : "FALLÓ")} · {u.Message}");
        }
        b.AppendLine();
        b.AppendLine("CONFIGURACIÓN");
        b.AppendLine($"  Datos: {paths.Root} · nombre público: {o.PublicHost ?? "—"}");
        b.AppendLine($"  Redes permitidas: {o.Network.AllowedNetworks ?? "(por defecto)"} · proxies: {o.Network.TrustedProxies ?? "(por defecto)"}");
        b.AppendLine($"  Copias: cada {o.Backups.IntervalMinutes} min (solo con actividad: {(o.Backups.SkipIfIdle ? "sí" : "no")}) · se guardan {o.Backups.KeepHourly}/{o.Backups.KeepDaily}/{o.Backups.KeepWeekly}, {o.Backups.KeepManual} manuales");
        b.AppendLine($"  Segundo destino: {backups.SecondaryPath ?? "—"} · disponible: {(backups.SecondaryAvailable ? "sí" : "no")} · problema: {backups.SecondaryProblem ?? "—"}");
        b.AppendLine($"  Actualización automática: {(o.Updates.AutoUpdate ? "sí" : "no")} a las {o.Updates.Time} · avisos {o.Updates.WarningMinutes}");
        b.AppendLine($"  Avisos (ntfy): {(s.Ntfy is not null || !string.IsNullOrWhiteSpace(o.Notifications.NtfyTopic) ? "configurados" : "no configurados")} · tipos: {s.Notifications}");
        b.AppendLine($"  Acceso: {s.AccessMode}{(s.EnrollmentUntil is { } e ? $" · inscripción hasta {When(e)}" : "")}");
        b.AppendLine($"  Realms: {(s.Realms is not null ? "cuenta conectada" : "sin cuenta")}");
        b.AppendLine($"  Mapa: motor {s.Map.EngineId} · cada noche: {(s.Map.Nightly ? $"sí, {s.Map.Time}" : "no")}");
        return b.ToString();
    }

    private string Disk()
    {
        var report = storage.GetReport(refresh: true);
        var b = new StringBuilder();
        foreach (var d in report.Disks)
        {
            b.AppendLine($"{d.Label} ({d.Path}): {Size(d.Free)} libres de {Size(d.Total)} ({d.FreePercent:0}%){(d.Low ? " · POCO ESPACIO" : "")}");
        }
        b.AppendLine();
        foreach (var f in report.Folders)
        {
            b.AppendLine($"{f.Label} ({f.Path}): {Size(f.Bytes)}");
        }
        return b.ToString();
    }

    private string Backups()
    {
        var b = new StringBuilder();
        if (state.Current.BackupCheck is { } check)
        {
            b.AppendLine($"Última comprobación: {When(check.At)} · {check.File} · {(check.Ok ? "bien" : "DAÑADA")} · {check.Message}");
            b.AppendLine();
        }
        foreach (var e in backups.List())
        {
            var path = backups.ResolveExisting(e.File.FileName);
            var size = path is not null && File.Exists(path) ? Size(new FileInfo(path).Length) : "?";
            b.AppendLine($"{e.File.FileName} · {e.File.Kind} · {size}{(e.InSecondary ? " · también en el segundo destino" : "")}");
        }
        return b.Length == 0 ? "No hay copias." : b.ToString();
    }

    private string AppLog()
    {
        var b = new StringBuilder();
        foreach (var r in logs.Snapshot())
        {
            b.Append(When(r.At)).Append(' ').Append(r.Level.ToString().ToUpperInvariant()[..4]).Append(' ').Append(r.Category).Append(": ").AppendLine(r.Message);
            if (r.Exception is not null)
            {
                b.AppendLine(r.Exception);
            }
        }
        return b.Length == 0 ? "(vacío)" : b.ToString();
    }

    private string Console()
    {
        var b = new StringBuilder();
        foreach (var line in console.Snapshot())
        {
            b.Append(When(line.Timestamp)).Append(' ').Append(line.Kind switch
            {
                ConsoleLineKind.Command => "> ",
                ConsoleLineKind.System => "# ",
                ConsoleLineKind.Error => "! ",
                _ => "  ",
            }).AppendLine(line.Text);
        }
        return b.Length == 0 ? "(vacía)" : b.ToString();
    }

    private string Map()
    {
        var b = new StringBuilder();
        if (state.Current.Map.LastRender is { } r)
        {
            b.AppendLine($"Última generación: {When(r.At)} · {r.EngineId} · {(r.Succeeded ? "bien" : "FALLÓ")} · {r.Message} · {r.Duration:hh\\:mm\\:ss}");
        }
        if (state.Current.Map.Commands.Count > 0)
        {
            foreach (var (engine, command) in state.Current.Map.Commands)
            {
                b.AppendLine($"Comando {engine}: {command}");
            }
        }
        b.AppendLine();
        foreach (var line in maps.RecentLog)
        {
            b.AppendLine(line);
        }
        return b.ToString();
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024L * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
    };
}
