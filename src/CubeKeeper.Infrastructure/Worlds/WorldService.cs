using System.IO.Compression;
using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Backups;
using CubeKeeper.Core.Properties;
using CubeKeeper.Core.Server;
using CubeKeeper.Core.Worlds;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Worlds;

public sealed record WorldInfo(string Name, string? DisplayName, long Size, DateTimeOffset LastModified, bool IsActive);

public sealed record NewWorldOptions(string Name, string? Seed, string GameMode, string Difficulty);

/// <summary>Mundos de worlds/: cambiar el activo (level-name), crear, importar y exportar .mcworld y borrar.</summary>
public sealed class WorldService(
    IServerController server,
    BackupService backups,
    DataPaths paths,
    TimeProvider time,
    ILogger<WorldService> logger)
{
    /// <summary>Tamaño máximo de un .mcworld importado (descomprimido se comprueba aparte).</summary>
    public const long MaxImportBytes = 2L * 1024 * 1024 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string WorldsDirectory => Path.Combine(paths.Server, "worlds");

    public event Action? Changed;

    public async Task<IReadOnlyList<WorldInfo>> ListAsync(CancellationToken ct = default)
    {
        var active = await backups.GetWorldNameAsync(ct).ConfigureAwait(false);
        if (!Directory.Exists(WorldsDirectory))
        {
            return [];
        }
        var result = new List<WorldInfo>();
        foreach (var dir in Directory.EnumerateDirectories(WorldsDirectory))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.'))
            {
                continue; // temporales de importación o restauración
            }
            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
            var levelName = Path.Combine(dir, "levelname.txt");
            var display = File.Exists(levelName) ? (await File.ReadAllTextAsync(levelName, ct).ConfigureAwait(false)).Trim() : null;
            result.Add(new WorldInfo(
                name,
                string.IsNullOrEmpty(display) || display == name ? null : display,
                files.Sum(f => f.Length),
                files.Count == 0 ? Directory.GetLastWriteTimeUtc(dir) : files.Max(f => f.LastWriteTimeUtc),
                string.Equals(name, active, StringComparison.Ordinal)));
        }
        // Puede que level-name apunte a un mundo que aún no existe (se crea al arrancar).
        if (!result.Any(w => w.IsActive))
        {
            result.Add(new WorldInfo(active, "se creará al arrancar el servidor", 0, time.GetUtcNow(), true));
        }
        return result.OrderByDescending(w => w.IsActive).ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Cambia el mundo activo: copia del actual, level-name nuevo y reinicio si estaba en marcha.</summary>
    public async Task SwitchAsync(string name, CancellationToken ct = default)
    {
        Validate(name);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(Path.Combine(WorldsDirectory, name)))
            {
                throw new InvalidOperationException($"No existe el mundo «{name}».");
            }
            await ChangeActiveAsync(doc => doc.Set("level-name", name), ct).ConfigureAwait(false);
            logger.LogInformation("Mundo activo: {World}", name);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Prepara un mundo nuevo: pone level-name, semilla, modo y dificultad. BDS lo genera al arrancar (si estaba en
    /// marcha se reinicia ya).
    /// </summary>
    public async Task CreateAsync(NewWorldOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options.Name);
        if (Directory.Exists(Path.Combine(WorldsDirectory, options.Name)))
        {
            throw new InvalidOperationException($"Ya existe un mundo llamado «{options.Name}».");
        }
        foreach (var (key, value) in new[] { ("gamemode", options.GameMode), ("difficulty", options.Difficulty), ("level-seed", options.Seed ?? string.Empty) })
        {
            if (PropertyDefinitions.For(key).Validate(value) is { } error)
            {
                throw new ArgumentException($"{PropertyDefinitions.For(key).Label}: {error}");
            }
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ChangeActiveAsync(doc =>
            {
                doc.Set("level-name", options.Name);
                doc.Set("level-seed", options.Seed?.Trim() ?? string.Empty);
                doc.Set("gamemode", options.GameMode);
                doc.Set("difficulty", options.Difficulty);
            }, ct).ConfigureAwait(false);
            logger.LogInformation("Mundo nuevo preparado: {World}", options.Name);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Importa un .mcworld (zip con level.dat en la raíz o en una carpeta). Devuelve el nombre de la carpeta creada.
    /// </summary>
    public async Task<string> ImportAsync(Stream content, string fileName, string? desiredName, bool activate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        Directory.CreateDirectory(paths.Downloads);
        Directory.CreateDirectory(WorldsDirectory);
        var tmpZip = Path.Combine(paths.Downloads, $".import-{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(WorldsDirectory, $".import-{Guid.NewGuid():N}");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var file = File.Create(tmpZip))
            {
                await CopyLimitedAsync(content, file, MaxImportBytes, ct).ConfigureAwait(false);
            }

            using (var zip = ZipFile.OpenRead(tmpZip))
            {
                var uncompressed = zip.Entries.Sum(e => e.Length);
                if (uncompressed > MaxImportBytes * 4)
                {
                    throw new InvalidDataException("El mundo descomprimido es demasiado grande.");
                }
            }
            // ExtractToDirectory rechaza entradas que se salgan del destino.
            await ZipFile.ExtractToDirectoryAsync(tmpZip, staging, overwriteFiles: true, ct).ConfigureAwait(false);
            var root = FindLevelRoot(staging) ?? throw new InvalidDataException("El archivo no contiene level.dat: no parece un mundo de Bedrock (.mcworld).");

            var levelNameFile = Path.Combine(root, "levelname.txt");
            var suggested = File.Exists(levelNameFile)
                ? (await File.ReadAllTextAsync(levelNameFile, ct).ConfigureAwait(false)).Trim()
                : Path.GetFileNameWithoutExtension(fileName);
            var name = string.IsNullOrWhiteSpace(desiredName) ? WorldNames.FromText(suggested) : desiredName.Trim();
            Validate(name);
            var target = Path.Combine(WorldsDirectory, name);
            if (Directory.Exists(target))
            {
                throw new InvalidOperationException($"Ya existe un mundo llamado «{name}». Elige otro nombre.");
            }
            Directory.Move(root, target);
            logger.LogInformation("Mundo importado: {World} desde {File}", name, fileName);

            if (activate)
            {
                await ChangeActiveAsync(doc => doc.Set("level-name", name), ct).ConfigureAwait(false);
            }
            return name;
        }
        finally
        {
            if (File.Exists(tmpZip))
            {
                File.Delete(tmpZip);
            }
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Prepara un .mcworld para descargar. El mundo activo se copia con <see cref="BackupService"/> (en caliente si
    /// está en marcha) y queda como copia manual; los demás se comprimen a un temporal que se borra al cerrarlo.
    /// </summary>
    public async Task<(Stream Stream, string FileName)> ExportAsync(string name, CancellationToken ct = default)
    {
        Validate(name);
        var dir = Path.Combine(WorldsDirectory, name);
        if (!Directory.Exists(dir))
        {
            throw new FileNotFoundException($"No existe el mundo «{name}».");
        }
        var active = await backups.GetWorldNameAsync(ct).ConfigureAwait(false);
        var download = $"{WorldNames.FromText(name)}.mcworld";
        if (name == active)
        {
            var b = await backups.CreateAsync(BackupKind.Manual, ct).ConfigureAwait(false);
            var path = backups.ResolveExisting(b.FileName)!;
            return (File.OpenRead(path), download);
        }

        Directory.CreateDirectory(paths.Downloads);
        var tmp = Path.Combine(paths.Downloads, $".export-{Guid.NewGuid():N}.mcworld");
        await ZipFile.CreateFromDirectoryAsync(dir, tmp, CompressionLevel.Optimal, includeBaseDirectory: false, ct).ConfigureAwait(false);
        return (new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose), download);
    }

    /// <summary>Borra un mundo que no esté activo. Antes deja una copia «antes de un cambio».</summary>
    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        Validate(name);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var active = await backups.GetWorldNameAsync(ct).ConfigureAwait(false);
            if (name == active)
            {
                throw new InvalidOperationException("No se puede borrar el mundo activo. Cambia antes a otro.");
            }
            var dir = Path.Combine(WorldsDirectory, name);
            if (!Directory.Exists(dir))
            {
                return;
            }
            await backups.CreateAsync(BackupKind.PreChange, ct, world: name).ConfigureAwait(false);
            Directory.Delete(dir, recursive: true);
            logger.LogInformation("Mundo borrado: {World}", name);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Copia del mundo activo, cambio en server.properties y reinicio si estaba en marcha.</summary>
    private async Task ChangeActiveAsync(Action<ServerPropertiesDocument> change, CancellationToken ct)
    {
        var current = await backups.GetWorldNameAsync(ct).ConfigureAwait(false);
        if (Directory.Exists(backups.WorldDirectory(current)))
        {
            await backups.CreateAsync(BackupKind.PreChange, ct).ConfigureAwait(false);
        }

        var wasRunning = server.Snapshot.Status.IsProcessAlive();
        if (wasRunning)
        {
            await server.StopAsync(ct).ConfigureAwait(false);
        }
        var doc = await ServerPropertiesDocument.LoadAsync(paths.ServerProperties, ct).ConfigureAwait(false);
        change(doc);
        await doc.SaveAsync(paths.ServerProperties, ct).ConfigureAwait(false);
        if (wasRunning)
        {
            await server.StartAsync(ct).ConfigureAwait(false);
        }
    }

    private static void Validate(string name)
    {
        if (WorldNames.Validate(name) is { } error)
        {
            throw new ArgumentException($"Nombre de mundo no válido: {error}");
        }
    }

    internal static string? FindLevelRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "level.dat")))
        {
            return dir;
        }
        var subdirs = Directory.GetDirectories(dir);
        return subdirs.Length == 1 && File.Exists(Path.Combine(subdirs[0], "level.dat")) ? subdirs[0] : null;
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long max, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > max)
            {
                throw new InvalidDataException($"El archivo supera el máximo de {max / 1024 / 1024} MB.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }
}
