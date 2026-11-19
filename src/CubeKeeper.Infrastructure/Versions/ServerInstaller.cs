using System.IO.Compression;
using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Properties;
using CubeKeeper.Core.Versions;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Versions;

public sealed class EulaNotAcceptedException()
    : InvalidOperationException(
        "Para descargar el servidor hay que aceptar la EULA de Minecraft (https://www.minecraft.net/eula) " +
        "y la política de privacidad: pon CUBEKEEPER_ACCEPT_EULA=true.");

/// <summary>
/// Descarga el zip de BDS en /data/versions/&lt;versión&gt; y lo copia en /data/server sin tocar el estado del jugador.
/// </summary>
public sealed class ServerInstaller(
    HttpClient http,
    DataPaths paths,
    AppStateStore state,
    IOptions<CubeKeeperOptions> options,
    ILogger<ServerInstaller> logger) : IServerInstaller
{
    public const string HttpClientName = "bds-download";

    /// <summary>Archivos y carpetas de /data/server que nunca se sobrescriben al actualizar.</summary>
    public static readonly IReadOnlySet<string> PreservedPaths = new HashSet<string>(StringComparer.Ordinal)
    {
        "worlds",
        "server.properties",
        "allowlist.json",
        "permissions.json",
    };

    private readonly BdsOptions _options = options.Value.Bds;

    public string? InstalledVersion => IsInstalled ? state.Current.InstalledVersion : null;

    public bool IsInstalled => File.Exists(paths.ServerExecutable);

    public async Task<InstallResult> InstallAsync(BdsRelease release, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!_options.AcceptEula)
        {
            throw new EulaNotAcceptedException();
        }
        paths.EnsureCreated();

        var versionDir = Path.Combine(paths.Versions, release.Version.Text);
        if (!File.Exists(Path.Combine(versionDir, "bedrock_server")))
        {
            await DownloadAndExtractAsync(release, versionDir, progress, ct).ConfigureAwait(false);
        }
        else
        {
            progress?.Report($"La versión {release.Version} ya estaba descargada.");
        }

        var wasUpdate = IsInstalled;
        var hadProperties = File.Exists(paths.ServerProperties);
        progress?.Report("Copiando archivos al directorio del servidor…");
        CopyIntoServer(versionDir, paths.Server);

        IReadOnlyList<string> added = [];
        if (hadProperties)
        {
            added = await MergePropertiesAsync(versionDir, ct).ConfigureAwait(false);
        }
        else if (File.Exists(paths.ServerProperties))
        {
            // Instalación nueva: partir de la configuración probada (NetherNet, puertos, LAN…).
            var doc = await ServerPropertiesDocument.LoadAsync(paths.ServerProperties, ct).ConfigureAwait(false);
            var changed = RecommendedSettings.Apply(doc, includeInitialDefaults: true);
            await doc.SaveAsync(paths.ServerProperties, ct).ConfigureAwait(false);
            progress?.Report($"server.properties creado con la configuración recomendada ({string.Join(", ", changed)}).");
        }

        var exe = paths.ServerExecutable;
        if (!OperatingSystem.IsWindows() && File.Exists(exe))
        {
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        }

        state.Update(s => s with { InstalledVersion = release.Version.Text });
        PruneOldVersions(release.Version);
        logger.LogInformation("BDS {Version} instalado (actualización: {Update})", release.Version, wasUpdate);
        return new InstallResult(release.Version.Text, wasUpdate, added);
    }

    private async Task DownloadAndExtractAsync(BdsRelease release, string versionDir, IProgress<string>? progress, CancellationToken ct)
    {
        var zipPath = Path.Combine(paths.Downloads, $"bedrock-server-{release.Version}.zip");
        var partPath = zipPath + ".part";
        progress?.Report($"Descargando {release.DownloadUrl}…");

        using (var resp = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(partPath);
            var buffer = new byte[81920];
            long done = 0;
            var lastReported = -1;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total is > 0)
                {
                    var pct = (int)(done * 100 / total.Value);
                    if (pct / 10 != lastReported / 10)
                    {
                        lastReported = pct;
                        progress?.Report($"Descargado {pct}% ({done / 1024 / 1024} MB)");
                    }
                }
            }
        }
        File.Move(partPath, zipPath, overwrite: true);

        progress?.Report("Descomprimiendo…");
        var tmpDir = versionDir + ".tmp";
        if (Directory.Exists(tmpDir))
        {
            Directory.Delete(tmpDir, recursive: true);
        }
        // ExtractToDirectory rechaza entradas que se salgan del destino (zip slip).
        await ZipFile.ExtractToDirectoryAsync(zipPath, tmpDir, overwriteFiles: true, ct).ConfigureAwait(false);
        if (!File.Exists(Path.Combine(tmpDir, "bedrock_server")))
        {
            Directory.Delete(tmpDir, recursive: true);
            throw new InvalidDataException("El zip descargado no contiene bedrock_server: ¿es la versión de Linux?");
        }
        if (Directory.Exists(versionDir))
        {
            Directory.Delete(versionDir, recursive: true);
        }
        Directory.Move(tmpDir, versionDir);
        File.Delete(zipPath);
    }

    /// <summary>Copia el contenido de la versión a /data/server sin sobrescribir los archivos preservados que ya existan.</summary>
    internal static void CopyIntoServer(string sourceDir, string serverDir)
    {
        Directory.CreateDirectory(serverDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var top = rel.Split(Path.DirectorySeparatorChar, 2)[0];
            var dest = Path.Combine(serverDir, rel);
            if (PreservedPaths.Contains(top) && (File.Exists(dest) || Directory.Exists(Path.Combine(serverDir, top))))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary>Añade a server.properties los ajustes nuevos que traiga la versión, sin cambiar los existentes.</summary>
    private async Task<IReadOnlyList<string>> MergePropertiesAsync(string versionDir, CancellationToken ct)
    {
        var template = Path.Combine(versionDir, "server.properties");
        if (!File.Exists(template) || !File.Exists(paths.ServerProperties))
        {
            return [];
        }
        var current = await ServerPropertiesDocument.LoadAsync(paths.ServerProperties, ct).ConfigureAwait(false);
        var fresh = await ServerPropertiesDocument.LoadAsync(template, ct).ConfigureAwait(false);
        var added = current.AddMissingFrom(fresh);
        if (added.Count > 0)
        {
            await current.SaveAsync(paths.ServerProperties, ct).ConfigureAwait(false);
        }
        return added;
    }

    private void PruneOldVersions(BdsVersion current)
    {
        var keep = Math.Max(1, _options.VersionsToKeep);
        var dirs = Directory.EnumerateDirectories(paths.Versions)
            .Select(d => (Dir: d, Ok: BdsVersion.TryParse(Path.GetFileName(d), out var v), Version: v))
            .Where(x => x.Ok && x.Version is not null && !x.Version.Equals(current))
            .OrderByDescending(x => x.Version!)
            .Skip(keep - 1);
        foreach (var (dir, _, _) in dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                logger.LogInformation("Versión antigua eliminada: {Dir}", dir);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "No se pudo borrar {Dir}", dir);
            }
        }
    }
}
