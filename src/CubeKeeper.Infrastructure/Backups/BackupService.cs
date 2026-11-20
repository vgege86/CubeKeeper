using System.IO.Compression;
using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Backups;
using CubeKeeper.Core.Properties;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Backups;

/// <summary>Una copia y si también está en el segundo destino.</summary>
public sealed record BackupEntry(BackupFile File, bool InSecondary);

/// <summary>
/// Copias del mundo en formato .mcworld (zip con level.dat en la raíz).
/// Con el servidor en marcha la copia es consistente: <c>save hold</c> → <c>save query</c> hasta que BDS da la lista de
/// archivos → copiar cada uno truncado a la longitud indicada → <c>save resume</c> (siempre, aunque falle la copia).
/// </summary>
public sealed class BackupService(
    IServerController server,
    ConsoleCommandRunner runner,
    DataPaths paths,
    IOptions<CubeKeeperOptions> options,
    TimeProvider time,
    ILogger<BackupService> logger)
{
    public const string DefaultLevelName = "Bedrock level";

    private readonly BackupOptions _options = options.Value.Backups;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Se dispara al crear, borrar o restaurar una copia.</summary>
    public event Action? Changed;

    public bool IsBusy => _gate.CurrentCount == 0;

    public RetentionPolicy Retention => new(_options.KeepHourly, _options.KeepDaily, _options.KeepWeekly, _options.KeepManual, _options.KeepSafety);

    /// <summary>Segundo destino configurado, o null.</summary>
    public string? SecondaryPath => string.IsNullOrWhiteSpace(_options.SecondaryPath) ? null : Path.GetFullPath(_options.SecondaryPath);

    /// <summary>Si el segundo destino está configurado y montado.</summary>
    public bool SecondaryAvailable => SecondaryPath is { } p && Directory.Exists(p);

    /// <summary>Por qué no llegó la última copia al segundo destino (null si llegó o no hay segundo destino).</summary>
    public string? SecondaryProblem { get; private set; }

    public async Task<string> GetWorldNameAsync(CancellationToken ct = default)
    {
        var doc = await ServerPropertiesDocument.LoadAsync(paths.ServerProperties, ct).ConfigureAwait(false);
        var name = doc.Get("level-name")?.Trim();
        return string.IsNullOrEmpty(name) ? DefaultLevelName : name;
    }

    public string WorldDirectory(string world)
    {
        if (world.Contains('/') || world.Contains('\\') || world is "." or "..")
        {
            throw new InvalidOperationException($"Nombre de mundo no válido: '{world}'.");
        }
        return Path.Combine(paths.Server, "worlds", world);
    }

    public IReadOnlyList<BackupEntry> List()
    {
        var secondary = SecondaryAvailable
            ? Directory.EnumerateFiles(SecondaryPath!, "*" + BackupName.Extension).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal)
            : [];
        return Enumerate(paths.Backups)
            .Select(b => new BackupEntry(b, secondary.Contains(b.FileName)))
            .OrderByDescending(e => e.File.CreatedAt)
            .ToList();
    }

    /// <summary>Copia del mundo activo o, con <paramref name="world"/>, de otro mundo (siempre en frío: no está cargado).</summary>
    public async Task<BackupFile> CreateAsync(BackupKind kind, CancellationToken ct = default, string? world = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CreateCoreAsync(kind, ct, world).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            RaiseChanged();
        }
    }

    /// <summary>
    /// Sustituye el mundo actual por el de la copia. Antes hace una copia de seguridad del actual, para la el servidor
    /// si estaba en marcha y lo vuelve a arrancar al terminar.
    /// </summary>
    public async Task RestoreAsync(string fileName, CancellationToken ct = default)
    {
        var source = ResolveExisting(fileName) ?? throw new FileNotFoundException("No existe esa copia.", fileName);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var world = await GetWorldNameAsync(ct).ConfigureAwait(false);
            var worldDir = WorldDirectory(world);
            var worldsDir = Path.GetDirectoryName(worldDir)!;
            Directory.CreateDirectory(worldsDir);

            // 1. Descomprimir y comprobar antes de tocar nada.
            var staging = Path.Combine(worldsDir, $".restore-{Guid.NewGuid():N}");
            await ZipFile.ExtractToDirectoryAsync(source, staging, overwriteFiles: true, ct).ConfigureAwait(false);
            var root = FindLevelRoot(staging);
            if (root is null)
            {
                Directory.Delete(staging, recursive: true);
                throw new InvalidDataException("La copia no contiene level.dat: no parece un mundo de Bedrock.");
            }

            // 2. Copia de seguridad del mundo actual.
            if (Directory.Exists(worldDir))
            {
                await CreateCoreAsync(BackupKind.PreRestore, ct).ConfigureAwait(false);
            }

            // 3. Parar, cambiar las carpetas y volver a arrancar.
            var wasRunning = server.Snapshot.Status.IsProcessAlive();
            if (wasRunning)
            {
                await server.StopAsync(ct).ConfigureAwait(false);
            }
            var old = Path.Combine(worldsDir, $".old-{Guid.NewGuid():N}");
            if (Directory.Exists(worldDir))
            {
                Directory.Move(worldDir, old);
            }
            Directory.Move(root, worldDir);
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            if (Directory.Exists(old))
            {
                Directory.Delete(old, recursive: true);
            }
            logger.LogInformation("Mundo {World} restaurado desde {File}", world, fileName);

            if (wasRunning)
            {
                await server.StartAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
            RaiseChanged();
        }
    }

    public void Delete(string fileName)
    {
        if (!BackupName.TryParse(fileName, 0, out _))
        {
            throw new ArgumentException("Nombre de copia no válido.", nameof(fileName));
        }
        foreach (var dir in Destinations())
        {
            var path = Path.Combine(dir, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        RaiseChanged();
    }

    /// <summary>Ruta de una copia existente, validando el nombre (sin rutas). Null si no existe.</summary>
    public string? ResolveExisting(string fileName)
    {
        if (!BackupName.TryParse(fileName, 0, out _))
        {
            return null;
        }
        return Destinations().Select(d => Path.Combine(d, fileName)).FirstOrDefault(File.Exists);
    }

    private async Task<BackupFile> CreateCoreAsync(BackupKind kind, CancellationToken ct, string? worldOverride = null)
    {
        var active = await GetWorldNameAsync(ct).ConfigureAwait(false);
        var world = worldOverride ?? active;
        var isActive = string.Equals(world, active, StringComparison.Ordinal);
        var worldDir = WorldDirectory(world);
        if (!Directory.Exists(worldDir))
        {
            throw new InvalidOperationException($"No existe el mundo «{world}» (worlds/{world}). Arranca el servidor una vez para crearlo.");
        }
        Directory.CreateDirectory(paths.Backups);

        var status = server.Snapshot.Status;
        if (isActive && status is ServerStatus.Starting or ServerStatus.Stopping or ServerStatus.Installing)
        {
            throw new InvalidOperationException("Espera a que el servidor termine de arrancar o de parar.");
        }

        var now = time.GetUtcNow();
        var tmp = Path.Combine(paths.Backups, $".tmp-{Guid.NewGuid():N}{BackupName.Extension}");
        try
        {
            if (isActive && status == ServerStatus.Running)
            {
                await HotCopyAsync(world, worldDir, tmp, ct).ConfigureAwait(false);
            }
            else
            {
                await ZipFile.CreateFromDirectoryAsync(worldDir, tmp, CompressionLevel.Optimal, includeBaseDirectory: false, ct).ConfigureAwait(false);
            }

            var name = BackupName.Format(world, now, kind);
            var final = Path.Combine(paths.Backups, name);
            File.Move(tmp, final, overwrite: true);
            logger.LogInformation("Copia {Name} creada ({Size} KB)", name, new FileInfo(final).Length / 1024);

            CopyToSecondary(final);
            ApplyRetention();
            BackupName.TryParse(name, new FileInfo(final).Length, out var created);
            return created!;
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    private async Task HotCopyAsync(string world, string worldDir, string zipPath, CancellationToken ct)
    {
        var step = TimeSpan.FromSeconds(15);
        await runner.RunAsync("save hold", lines => lines.Count > 0 ? "ok" : null, step, ct).ConfigureAwait(false);
        try
        {
            var deadline = time.GetUtcNow().AddSeconds(_options.SaveTimeoutSeconds);
            IReadOnlyList<SaveFileEntry>? files = null;
            while (files is null)
            {
                var response = await runner.RunAsync("save query", ParseQueryResponse, step, ct).ConfigureAwait(false);
                if (response is QueryResponse.Ready ready)
                {
                    files = ready.Files;
                    break;
                }
                if (time.GetUtcNow() > deadline)
                {
                    throw new TimeoutException($"BDS no dejó los archivos listos en {_options.SaveTimeoutSeconds} s.");
                }
                await Task.Delay(TimeSpan.FromSeconds(1), time, ct).ConfigureAwait(false);
            }

            await Task.Run(() => WriteZip(world, worldDir, files, zipPath), ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await runner.RunAsync("save resume", lines => lines.Count > 0 ? "ok" : null, step, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo enviar «save resume»: el mundo puede seguir sin guardar cambios hasta reiniciar");
            }
        }
    }

    private static QueryResponse? ParseQueryResponse(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (SaveQueryParser.IsReady(lines[i]))
            {
                // La lista de archivos llega en la línea siguiente.
                return i + 1 < lines.Count && SaveQueryParser.ParseFileList(lines[i + 1]) is { } files
                    ? new QueryResponse.Ready(files)
                    : null;
            }
            if (SaveQueryParser.IsNotReady(lines[i]))
            {
                return new QueryResponse.NotReady();
            }
        }
        return null;
    }

    /// <summary>Escribe el zip con los archivos que indica BDS (truncados) y el resto de archivos del mundo fuera de db/.</summary>
    internal static void WriteZip(string world, string worldDir, IReadOnlyList<SaveFileEntry> files, string zipPath)
    {
        var prefix = world + "/";
        var added = new HashSet<string>(StringComparer.Ordinal);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        foreach (var f in files)
        {
            if (!f.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var rel = f.RelativePath[prefix.Length..];
            var source = Path.GetFullPath(Path.Combine(worldDir, rel));
            if (!source.StartsWith(Path.GetFullPath(worldDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(source))
            {
                continue;
            }
            var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = entry.Open();
            CopyBytes(input, output, f.Length);
            added.Add(rel);
        }

        // Lo que BDS no lista (iconos, nombre del mundo, json de paquetes…) se copia entero; db/ solo lo que lista.
        foreach (var path in Directory.EnumerateFiles(worldDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(worldDir, path).Replace('\\', '/');
            if (added.Contains(rel) || rel.StartsWith("db/", StringComparison.Ordinal))
            {
                continue;
            }
            zip.CreateEntryFromFile(path, rel, CompressionLevel.Optimal);
        }
    }

    private static void CopyBytes(Stream input, Stream output, long length)
    {
        var buffer = new byte[81920];
        var remaining = length;
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                break;
            }
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    /// <summary>Carpeta con level.dat: la raíz del zip o, si el zip trae una carpeta, esa carpeta.</summary>
    private static string? FindLevelRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "level.dat")))
        {
            return dir;
        }
        var subdirs = Directory.GetDirectories(dir);
        return subdirs.Length == 1 && File.Exists(Path.Combine(subdirs[0], "level.dat")) ? subdirs[0] : null;
    }

    private void CopyToSecondary(string file)
    {
        if (SecondaryPath is null)
        {
            SecondaryProblem = null;
            return;
        }
        if (!SecondaryAvailable)
        {
            logger.LogWarning("El segundo destino de copias {Path} no existe: ¿está montado?", SecondaryPath);
            SecondaryProblem = $"La carpeta {SecondaryPath} no existe: ¿está montado el dataset?";
            return;
        }
        try
        {
            var dest = Path.Combine(SecondaryPath, Path.GetFileName(file));
            var tmp = dest + ".part";
            File.Copy(file, tmp, overwrite: true);
            File.Move(tmp, dest, overwrite: true);
            SecondaryProblem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "No se pudo copiar {File} al segundo destino", file);
            SecondaryProblem = $"No se pudo copiar a {SecondaryPath}: {ex.Message}";
        }
    }

    private void ApplyRetention()
    {
        foreach (var dir in Destinations())
        {
            foreach (var b in Retention.SelectToDelete(Enumerate(dir), time.LocalTimeZone))
            {
                try
                {
                    File.Delete(Path.Combine(dir, b.FileName));
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "No se pudo borrar la copia antigua {File}", b.FileName);
                }
            }
        }
    }

    private IEnumerable<string> Destinations()
    {
        yield return paths.Backups;
        if (SecondaryAvailable)
        {
            yield return SecondaryPath!;
        }
    }

    private static IEnumerable<BackupFile> Enumerate(string dir)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }
        foreach (var path in Directory.EnumerateFiles(dir, "*" + BackupName.Extension))
        {
            if (BackupName.TryParse(Path.GetFileName(path), new FileInfo(path).Length, out var b))
            {
                yield return b!;
            }
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Suscriptor de copias con error");
        }
    }

    private abstract record QueryResponse
    {
        public sealed record Ready(IReadOnlyList<SaveFileEntry> Files) : QueryResponse;

        public sealed record NotReady : QueryResponse;
    }
}
