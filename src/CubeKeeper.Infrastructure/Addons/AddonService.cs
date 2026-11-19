using System.IO.Compression;
using System.Text;
using CubeKeeper.Core.Addons;
using CubeKeeper.Core.Worlds;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Addons;

public sealed record InstalledPack(PackManifest Manifest, string Folder, bool Enabled);

/// <summary>
/// Complementos del mundo activo: instala .mcpack / .mcaddon en worlds/&lt;mundo&gt;/behavior_packs y resource_packs y
/// los activa en world_behavior_packs.json / world_resource_packs.json. Los cambios se aplican al reiniciar.
/// </summary>
public sealed class AddonService(BackupService backups, DataPaths paths, ILogger<AddonService> logger)
{
    public const long MaxUploadBytes = 512L * 1024 * 1024;
    private const int MaxNesting = 3;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action? Changed;

    public async Task<string> ActiveWorldDirectoryAsync(CancellationToken ct = default) =>
        backups.WorldDirectory(await backups.GetWorldNameAsync(ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<InstalledPack>> ListAsync(CancellationToken ct = default)
    {
        var world = await ActiveWorldDirectoryAsync(ct).ConfigureAwait(false);
        var result = new List<InstalledPack>();
        foreach (var type in Enum.GetValues<PackType>())
        {
            var enabled = await LoadPacksJsonAsync(world, type, ct).ConfigureAwait(false);
            var folder = Path.Combine(world, FolderName(type));
            if (!Directory.Exists(folder))
            {
                continue;
            }
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                if (ReadManifest(dir) is { } m && m.Type == type)
                {
                    result.Add(new InstalledPack(m, Path.GetFileName(dir), enabled.Contains(m.Uuid)));
                }
            }
        }
        return result.OrderBy(p => p.Manifest.Type).ThenBy(p => p.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Instala los paquetes de un .mcpack o .mcaddon en el mundo activo y los activa. Devuelve los instalados.</summary>
    public async Task<IReadOnlyList<PackManifest>> InstallAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        var world = await ActiveWorldDirectoryAsync(ct).ConfigureAwait(false);
        if (!Directory.Exists(world))
        {
            throw new InvalidOperationException("El mundo activo todavía no existe: arranca el servidor una vez antes de añadir complementos.");
        }
        Directory.CreateDirectory(paths.Downloads);
        var work = Path.Combine(paths.Downloads, $".addon-{Guid.NewGuid():N}");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(work);
            var zipPath = Path.Combine(work, "upload.zip");
            await using (var file = File.Create(zipPath))
            {
                await CopyLimitedAsync(content, file, MaxUploadBytes, ct).ConfigureAwait(false);
            }
            var extracted = Path.Combine(work, "x");
            await ExtractNestedAsync(zipPath, extracted, 0, ct).ConfigureAwait(false);

            var packRoots = Directory.EnumerateFiles(extracted, "manifest.json", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Select(d => (Dir: d!, Manifest: ReadManifest(d!)))
                .Where(x => x.Manifest is not null)
                .ToList();
            // Un paquete dentro de otro (subpacks) no se instala por separado.
            packRoots = packRoots.Where(x => !packRoots.Any(o => o.Dir != x.Dir && x.Dir.StartsWith(o.Dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))).ToList();
            if (packRoots.Count == 0)
            {
                throw new InvalidDataException("No se ha encontrado ningún paquete de comportamiento o de recursos (manifest.json) en el archivo.");
            }

            var installed = new List<PackManifest>();
            foreach (var (dir, manifest) in packRoots)
            {
                var m = manifest!;
                var typeFolder = Path.Combine(world, FolderName(m.Type));
                Directory.CreateDirectory(typeFolder);
                // Si ya estaba (mismo UUID), se sustituye.
                foreach (var existing in Directory.EnumerateDirectories(typeFolder))
                {
                    if (ReadManifest(existing)?.Uuid == m.Uuid)
                    {
                        Directory.Delete(existing, recursive: true);
                    }
                }
                var target = Path.Combine(typeFolder, $"{WorldNames.FromText(m.Name)}_{m.Uuid.ToString()[..8]}");
                CopyDirectory(dir, target);
                await UpdatePacksJsonAsync(world, m.Type, doc => doc.Enable(m.Uuid, m.Version), ct).ConfigureAwait(false);
                installed.Add(m);
                logger.LogInformation("Complemento instalado: {Name} {Version} ({Type})", m.Name, m.VersionText, m.Type);
            }
            return installed;
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task SetEnabledAsync(Guid uuid, PackType type, bool enabled, CancellationToken ct = default)
    {
        var world = await ActiveWorldDirectoryAsync(ct).ConfigureAwait(false);
        var pack = (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Manifest.Uuid == uuid && p.Manifest.Type == type)
                   ?? throw new InvalidOperationException("Ese complemento no está instalado en el mundo activo.");
        await UpdatePacksJsonAsync(world, type, doc => enabled ? doc.Enable(uuid, pack.Manifest.Version) : doc.Disable(uuid), ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task RemoveAsync(Guid uuid, PackType type, CancellationToken ct = default)
    {
        var world = await ActiveWorldDirectoryAsync(ct).ConfigureAwait(false);
        await UpdatePacksJsonAsync(world, type, doc => doc.Disable(uuid), ct).ConfigureAwait(false);
        var folder = Path.Combine(world, FolderName(type));
        if (Directory.Exists(folder))
        {
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                if (ReadManifest(dir)?.Uuid == uuid)
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
        Changed?.Invoke();
    }

    private static string FolderName(PackType type) => type == PackType.Behavior ? "behavior_packs" : "resource_packs";

    private static string JsonName(PackType type) => type == PackType.Behavior ? "world_behavior_packs.json" : "world_resource_packs.json";

    private static async Task<WorldPacksDocument> LoadPacksJsonAsync(string world, PackType type, CancellationToken ct)
    {
        var path = Path.Combine(world, JsonName(type));
        return WorldPacksDocument.Parse(File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null);
    }

    private static async Task UpdatePacksJsonAsync(string world, PackType type, Func<WorldPacksDocument, bool> change, CancellationToken ct)
    {
        var doc = await LoadPacksJsonAsync(world, type, ct).ConfigureAwait(false);
        if (!change(doc))
        {
            return;
        }
        var path = Path.Combine(world, JsonName(type));
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, doc.ToJson(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Lee manifest.json de una carpeta de paquete, resolviendo el nombre con texts/es_ES.lang o en_US.lang.</summary>
    internal static PackManifest? ReadManifest(string dir)
    {
        var path = Path.Combine(dir, "manifest.json");
        if (!File.Exists(path))
        {
            return null;
        }
        string? Lang(string key)
        {
            foreach (var lang in new[] { "es_ES.lang", "es_MX.lang", "en_US.lang" })
            {
                var file = Path.Combine(dir, "texts", lang);
                if (File.Exists(file) && PackManifest.LookupLang(File.ReadAllText(file), key) is { } v)
                {
                    return v;
                }
            }
            return null;
        }
        return PackManifest.Parse(File.ReadAllText(path), Lang);
    }

    /// <summary>Descomprime y, si dentro hay .mcpack/.mcaddon/.zip, también los descomprime (hasta 3 niveles).</summary>
    private static async Task ExtractNestedAsync(string zip, string target, int depth, CancellationToken ct)
    {
        try
        {
            await ZipFile.ExtractToDirectoryAsync(zip, target, overwriteFiles: true, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("El archivo no es un .mcpack/.mcaddon válido (debería ser un zip).");
        }
        if (depth >= MaxNesting)
        {
            return;
        }
        foreach (var nested in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                     .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".mcpack" or ".mcaddon" or ".zip")
                     .ToList())
        {
            var dest = Path.Combine(Path.GetDirectoryName(nested)!, Path.GetFileNameWithoutExtension(nested) + "_" + Guid.NewGuid().ToString("N")[..6]);
            await ExtractNestedAsync(nested, dest, depth + 1, ct).ConfigureAwait(false);
            File.Delete(nested);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
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
