using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using CubeKeeper.Core.Backups;
using CubeKeeper.Core.Maps;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.State;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Maps;

public sealed record MapEngineStatus(MapEngine Engine, string? Executable, string Command, bool IsCustomCommand);

/// <summary>
/// Mapa del mundo con un programa externo (uNmINeD o bedrock-viz) que instala el administrador: subiendo el archivo o
/// desde un enlace. Se dibuja a partir de la copia más reciente del mundo (nunca del mundo en marcha), en una carpeta
/// aparte que solo sustituye a la anterior si sale bien.
/// </summary>
public sealed class MapService(BackupService backups, DataPaths paths, AppStateStore state, HttpClient http, TimeProvider time, ILogger<MapService> logger)
{
    public const string HttpClientName = "map-tools";
    public const long MaxToolBytes = 300L * 1024 * 1024;
    public static readonly TimeSpan RenderTimeout = TimeSpan.FromHours(2);
    /// <summary>Si la última copia tiene más de esto, se hace una antes de dibujar.</summary>
    public static readonly TimeSpan MaxBackupAge = TimeSpan.FromHours(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _logLock = new();
    private readonly List<string> _log = [];

    public event Action? Changed;

    public bool IsRendering => _gate.CurrentCount == 0;

    public string ToolsRoot => Path.Combine(paths.Root, "tools");

    public string MapsRoot => Path.Combine(paths.Root, "map");

    public MapSettings Settings => state.Current.Map;

    public MapEngine ActiveEngine => MapEngines.Find(Settings.EngineId) ?? MapEngines.Unmined;

    public IReadOnlyList<string> RecentLog
    {
        get
        {
            lock (_logLock)
            {
                return [.. _log];
            }
        }
    }

    public MapEngineStatus GetStatus(MapEngine engine)
    {
        var custom = Settings.Commands.TryGetValue(engine.Id, out var c) && !string.IsNullOrWhiteSpace(c);
        return new MapEngineStatus(engine, FindExecutable(engine), custom ? c! : engine.DefaultCommand, custom);
    }

    public string OutputDirectory(MapEngine engine) => Path.Combine(MapsRoot, engine.Id);

    /// <summary>Página principal del mapa generado (relativa a su carpeta), o null si no hay mapa.</summary>
    public string? EntryPage(MapEngine engine)
    {
        var dir = OutputDirectory(engine);
        if (!Directory.Exists(dir))
        {
            return null;
        }
        foreach (var name in new[] { "index.html", "unmined.index.html" })
        {
            if (File.Exists(Path.Combine(dir, name)))
            {
                return name;
            }
        }
        return Directory.EnumerateFiles(dir, "*.html").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>Ruta de un archivo del mapa del motor activo, validando que no se salga de su carpeta.</summary>
    public string? ResolveMapFile(string relativePath)
    {
        var root = Path.GetFullPath(OutputDirectory(ActiveEngine));
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('\\', '/')));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    public void SaveSettings(string engineId, string? command, bool nightly, string time)
    {
        var engine = MapEngines.Find(engineId) ?? throw new ArgumentException("Motor desconocido.");
        if (Core.Scheduling.DailySchedule.ParseTime(time) is null)
        {
            throw new ArgumentException("Hora no válida: usa HH:mm, por ejemplo 04:15.");
        }
        if (!string.IsNullOrWhiteSpace(command))
        {
            // Que se pueda interpretar ya, y no a las cuatro de la mañana.
            CommandTemplate.Build(command, SampleValues);
        }
        state.Update(s =>
        {
            var commands = new Dictionary<string, string>(s.Map.Commands);
            if (string.IsNullOrWhiteSpace(command) || command.Trim() == engine.DefaultCommand)
            {
                commands.Remove(engine.Id);
            }
            else
            {
                commands[engine.Id] = command.Trim();
            }
            return s with { Map = s.Map with { EngineId = engine.Id, Commands = commands, Nightly = nightly, Time = time.Trim() } };
        });
        Changed?.Invoke();
    }

    /// <summary>Instala el motor desde un archivo subido: .zip, .tar.gz/.tgz, .tar o el ejecutable suelto.</summary>
    public async Task<string> InstallAsync(MapEngine engine, Stream content, string fileName, CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.Downloads);
        var tmp = Path.Combine(paths.Downloads, $".tool-{Guid.NewGuid():N}");
        try
        {
            await using (var file = File.Create(tmp))
            {
                await CopyLimitedAsync(content, file, MaxToolBytes, ct).ConfigureAwait(false);
            }
            return await InstallFromFileAsync(engine, tmp, fileName, ct).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    /// <summary>Descarga el motor desde un enlace https e instala lo descargado.</summary>
    public async Task<string> InstallFromUrlAsync(MapEngine engine, string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("El enlace debe empezar por https://.");
        }
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"La descarga falló: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        var name = response.Content.Headers.ContentDisposition?.FileNameStar
                   ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                   ?? Path.GetFileName(response.RequestMessage?.RequestUri?.AbsolutePath ?? uri.AbsolutePath);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await InstallAsync(engine, stream, string.IsNullOrWhiteSpace(name) ? "descarga" : name, ct).ConfigureAwait(false);
    }

    public void Uninstall(MapEngine engine)
    {
        var dir = Path.Combine(ToolsRoot, engine.Id);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
        Changed?.Invoke();
    }

    /// <summary>Dibuja el mapa con el motor activo. Devuelve el resultado (también queda en el estado).</summary>
    public async Task<MapRenderRecord> RenderAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Ya se está dibujando el mapa.");
        }
        var engine = ActiveEngine;
        var started = time.GetUtcNow();
        var work = Path.Combine(MapsRoot, $".work-{Guid.NewGuid():N}");
        lock (_logLock)
        {
            _log.Clear();
        }
        Changed?.Invoke();
        try
        {
            var status = GetStatus(engine);
            if (status.Executable is null)
            {
                return Finish(engine, started, false, $"{engine.Name} no está instalado: súbelo o pega su enlace de descarga.");
            }

            // 1. Una copia reciente del mundo, descomprimida en una carpeta de trabajo.
            var world = await backups.GetWorldNameAsync(ct).ConfigureAwait(false);
            var latest = backups.List().FirstOrDefault(b => b.File.World == world)?.File;
            if (latest is null || time.GetUtcNow() - latest.CreatedAt > MaxBackupAge)
            {
                Log("Haciendo una copia del mundo para dibujarlo…");
                latest = await backups.CreateAsync(BackupKind.Scheduled, ct).ConfigureAwait(false);
            }
            var worldDir = Path.Combine(work, "world");
            await ZipFile.ExtractToDirectoryAsync(backups.ResolveExisting(latest.FileName)!, worldDir, overwriteFiles: true, ct).ConfigureAwait(false);
            Log($"Mundo «{world}» de la copia {latest.FileName}.");

            // 2. Ejecutar el motor hacia una carpeta nueva.
            var output = Path.Combine(work, "output");
            Directory.CreateDirectory(output);
            var args = CommandTemplate.Build(status.Command, new Dictionary<string, string>
            {
                ["world"] = worldDir,
                ["output"] = output,
                ["threads"] = Math.Max(1, Environment.ProcessorCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            Log($"> {Path.GetFileName(status.Executable)} {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
            var exit = await RunAsync(status.Executable, args, ct).ConfigureAwait(false);
            if (exit != 0)
            {
                return Finish(engine, started, false, $"{engine.Name} terminó con código {exit}. Revisa el registro y, si hace falta, el comando.");
            }
            if (!Directory.EnumerateFiles(output, "*.html", SearchOption.TopDirectoryOnly).Any())
            {
                return Finish(engine, started, false, $"{engine.Name} terminó sin generar ninguna página .html en la carpeta de salida. Revisa el comando.");
            }

            // 3. Sustituir el mapa anterior solo ahora que el nuevo está bien.
            var target = OutputDirectory(engine);
            var old = target + ".old";
            if (Directory.Exists(old))
            {
                Directory.Delete(old, recursive: true);
            }
            if (Directory.Exists(target))
            {
                Directory.Move(target, old);
            }
            Directory.Move(output, target);
            if (Directory.Exists(old))
            {
                Directory.Delete(old, recursive: true);
            }
            return Finish(engine, started, true, $"Mapa generado con {engine.Name}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Fallo al dibujar el mapa");
            return Finish(engine, started, false, ex.Message);
        }
        finally
        {
            if (Directory.Exists(work))
            {
                try
                {
                    Directory.Delete(work, recursive: true);
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "No se pudo borrar {Work}", work);
                }
            }
            _gate.Release();
            Changed?.Invoke();
        }
    }

    private static readonly IReadOnlyDictionary<string, string> SampleValues = new Dictionary<string, string>
    {
        ["world"] = "/w",
        ["output"] = "/o",
        ["threads"] = "1",
    };

    private async Task<int> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        // Algunos programas .NET autocontenidos piden HOME o un directorio de caché escribible.
        psi.Environment["HOME"] = Path.GetTempPath();
        var cache = Path.Combine(ToolsRoot, ".cache");
        Directory.CreateDirectory(cache);
        psi.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = cache;

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"No se pudo ejecutar {Path.GetFileName(executable)}: {ex.Message}. ¿Es la versión de Linux x64? ¿Está el dataset montado sin «exec»?", ex);
        }
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal; // que no le quite CPU al juego
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RenderTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"El mapa tardaba más de {RenderTimeout.TotalHours:0} h: se ha cancelado.");
        }
        return process.ExitCode;
    }

    private async Task<string> InstallFromFileAsync(MapEngine engine, string file, string fileName, CancellationToken ct)
    {
        var target = Path.Combine(ToolsRoot, engine.Id);
        var staging = target + $".new-{Guid.NewGuid():N}";
        Directory.CreateDirectory(staging);
        try
        {
            var lower = fileName.ToLowerInvariant();
            if (lower.EndsWith(".zip", StringComparison.Ordinal) || IsZip(file))
            {
                await ZipFile.ExtractToDirectoryAsync(file, staging, overwriteFiles: true, ct).ConfigureAwait(false);
            }
            else if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal) || IsGzip(file))
            {
                await using var fs = File.OpenRead(file);
                await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gz, staging, overwriteFiles: true, ct).ConfigureAwait(false);
            }
            else if (lower.EndsWith(".tar", StringComparison.Ordinal))
            {
                await TarFile.ExtractToDirectoryAsync(file, staging, overwriteFiles: true, ct).ConfigureAwait(false);
            }
            else if (IsElf(file))
            {
                File.Copy(file, Path.Combine(staging, engine.ExecutableNames[0]));
            }
            else
            {
                throw new InvalidDataException("No es un .zip, .tar.gz ni un ejecutable de Linux.");
            }

            var exe = FindExecutableIn(engine, staging)
                      ?? throw new InvalidDataException($"No se ha encontrado {string.Join(" ni ", engine.ExecutableNames)} dentro del archivo. ¿Es la versión de Linux?");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.UserRead);
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            Directory.Move(staging, target);
            var installed = FindExecutable(engine)!;
            logger.LogInformation("{Engine} instalado: {Exe}", engine.Name, installed);
            Changed?.Invoke();
            return installed;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private string? FindExecutable(MapEngine engine)
    {
        var dir = Path.Combine(ToolsRoot, engine.Id);
        return Directory.Exists(dir) ? FindExecutableIn(engine, dir) : null;
    }

    private static string? FindExecutableIn(MapEngine engine, string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => engine.ExecutableNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.Length)
            .FirstOrDefault();

    private static bool StartsWith(string file, params byte[] magic)
    {
        using var fs = File.OpenRead(file);
        var buf = new byte[magic.Length];
        return fs.Read(buf, 0, buf.Length) == buf.Length && buf.AsSpan().SequenceEqual(magic);
    }

    private static bool IsZip(string f) => StartsWith(f, 0x50, 0x4B, 0x03, 0x04);

    private static bool IsGzip(string f) => StartsWith(f, 0x1F, 0x8B);

    private static bool IsElf(string f) => StartsWith(f, 0x7F, 0x45, 0x4C, 0x46);

    private void Log(string line)
    {
        lock (_logLock)
        {
            _log.Add(line);
            if (_log.Count > 300)
            {
                _log.RemoveRange(0, _log.Count - 300);
            }
        }
        logger.LogInformation("Mapa: {Line}", line);
    }

    private MapRenderRecord Finish(MapEngine engine, DateTimeOffset started, bool ok, string message)
    {
        Log(message);
        var record = new MapRenderRecord(time.GetUtcNow(), engine.Id, ok, message, time.GetUtcNow() - started);
        state.Update(s => s with { Map = s.Map with { LastRender = record } });
        return record;
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
