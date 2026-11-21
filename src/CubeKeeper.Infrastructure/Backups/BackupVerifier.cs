using System.IO.Compression;
using CubeKeeper.Core.Notifications;
using CubeKeeper.Infrastructure.Notifications;
using CubeKeeper.Infrastructure.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Backups;

/// <summary>
/// Comprueba que una copia se podría restaurar: el zip se lee entero (los datos y su suma de comprobación), tiene
/// level.dat y la base de datos del mundo está completa (db/CURRENT apunta a un MANIFEST que está en la copia).
/// </summary>
public sealed class BackupVerifier(BackupService backups, AppStateStore state, TimeProvider time, ILogger<BackupVerifier> logger, Notifier? notifier = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupCheckRecord? LastCheck => state.Current.BackupCheck;

    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Comprueba <paramref name="fileName"/> o, si es null, una copia al azar. Null si no hay copias.</summary>
    public async Task<BackupCheckRecord?> VerifyAsync(string? fileName = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = backups.List();
            if (entries.Count == 0)
            {
                return null;
            }
            fileName ??= entries[Random.Shared.Next(entries.Count)].File.FileName;
            var path = backups.ResolveExisting(fileName) ?? throw new FileNotFoundException("No existe esa copia.", fileName);
            var (ok, message) = await Task.Run(() => Check(path, ct), ct).ConfigureAwait(false);
            var record = new BackupCheckRecord(time.GetUtcNow(), fileName, ok, message);
            state.Update(s => s with { BackupCheck = record });
            if (ok)
            {
                logger.LogInformation("Copia comprobada: {File} · {Message}", fileName, message);
            }
            else
            {
                logger.LogError("Copia dañada: {File} · {Message}", fileName, message);
                if (notifier is not null)
                {
                    await notifier.NotifyAsync(new Notification(
                        NotificationKind.BackupFailed,
                        "Una copia del mundo está dañada",
                        $"{fileName}: {message} Las demás copias pueden estar bien: comprueba otra en Copias.",
                        "verify:" + fileName,
                        NotificationPriority.High,
                        "/copias"), ct).ConfigureAwait(false);
                }
            }
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static (bool Ok, string Message) Check(string zipPath, CancellationToken ct)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
            var level = names.FirstOrDefault(n => n == "level.dat" || (n.EndsWith("/level.dat", StringComparison.Ordinal) && n.Count(c => c == '/') == 1));
            if (level is null)
            {
                return (false, "No tiene level.dat: no es un mundo completo.");
            }
            var root = level[..^"level.dat".Length];
            var current = zip.GetEntry(root + "db/CURRENT");
            if (current is null)
            {
                return (false, "Falta db/CURRENT: la base de datos del mundo está incompleta.");
            }
            string manifest;
            using (var reader = new StreamReader(current.Open()))
            {
                manifest = reader.ReadToEnd().Trim();
            }
            if (manifest.Length == 0 || !names.Contains(root + "db/" + manifest))
            {
                return (false, $"db/CURRENT apunta a «{manifest}», que no está en la copia.");
            }

            // Descomprime cada archivo y compara su CRC32 con el que se guardó al hacer la copia: así se detecta un
            // archivo dañado o cortado (.NET no lo comprueba por sí solo).
            long bytes = 0;
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                using var stream = entry.Open();
                var crc = Crc32.Seed;
                long length = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    crc = Crc32.Append(crc, buffer.AsSpan(0, read));
                    length += read;
                }
                if (length != entry.Length || Crc32.Finish(crc) != entry.Crc32)
                {
                    return (false, $"El archivo «{entry.FullName}» de la copia está dañado (no coincide su suma de comprobación).");
                }
                bytes += length;
            }
            return (true, $"Bien: {zip.Entries.Count} archivos, {bytes / 1024.0 / 1024:0.0} MB descomprimidos.");
        }
        catch (InvalidDataException ex)
        {
            return (false, $"El archivo está dañado ({ex.Message}).");
        }
        catch (IOException ex)
        {
            return (false, $"No se pudo leer ({ex.Message}).");
        }
    }
}

/// <summary>Comprueba una copia al azar una vez por semana.</summary>
public sealed class BackupVerifyScheduler(BackupVerifier verifier, TimeProvider time, ILogger<BackupVerifyScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(15), time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (verifier.LastCheck is not { } last || time.GetUtcNow() - last.At >= Every)
                {
                    try
                    {
                        await verifier.VerifyAsync(null, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "No se pudo comprobar una copia");
                    }
                }
                await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>CRC-32 (el de zip), sin dependencias.</summary>
internal static class Crc32
{
    public const uint Seed = 0xFFFFFFFFu;

    private static readonly uint[] Table = Build();

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    public static uint Finish(uint crc) => crc ^ 0xFFFFFFFFu;

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
