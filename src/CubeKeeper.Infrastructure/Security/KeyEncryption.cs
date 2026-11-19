using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace CubeKeeper.Infrastructure.Security;

/// <summary>
/// Secreto (CUBEKEEPER_KEY_SECRET) con el que se cifran las claves de /data/keys. Sin él, quien copie el dataset (o una
/// instantánea) puede descifrar las cookies, el token de ntfy y la sesión de Realms. Va fuera del dataset.
/// </summary>
public sealed class KeyEncryptionSecret
{
    public const int MinLength = 16;

    public KeyEncryptionSecret(string? value)
    {
        if (!string.IsNullOrEmpty(value) && value.Length < MinLength)
        {
            throw new InvalidOperationException($"CUBEKEEPER_KEY_SECRET es demasiado corto: usa al menos {MinLength} caracteres (mejor 32 o más, al azar).");
        }
        Value = string.IsNullOrEmpty(value) ? null : value;
    }

    public string? Value { get; }

    public bool IsSet => Value is not null;
}

/// <summary>Cifra las claves con AES-256-GCM y una clave derivada del secreto con PBKDF2 (sal distinta por clave).</summary>
public sealed class SecretXmlEncryptor(KeyEncryptionSecret secret) : IXmlEncryptor
{
    internal const int Iterations = 210_000;
    internal static readonly XNamespace Ns = "https://cubekeeper/keys/2026";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        var password = secret.Value ?? throw new InvalidOperationException("No hay secreto para cifrar las claves.");
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(DeriveKey(password, salt, Iterations), tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        CryptographicOperations.ZeroMemory(plaintext);
        var element = new XElement(Ns + "encryptedKey",
            new XAttribute("algorithm", "PBKDF2-SHA256+AES-256-GCM"),
            new XAttribute("iterations", Iterations),
            new XElement(Ns + "salt", Convert.ToBase64String(salt)),
            new XElement(Ns + "nonce", Convert.ToBase64String(nonce)),
            new XElement(Ns + "tag", Convert.ToBase64String(tag)),
            new XElement(Ns + "value", Convert.ToBase64String(ciphertext)));
        return new EncryptedXmlInfo(element, typeof(SecretXmlDecryptor));
    }

    internal static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
}

/// <summary>Lo crea Data Protection al leer una clave cifrada (constructor con IServiceProvider).</summary>
public sealed class SecretXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        var secret = services.GetService<KeyEncryptionSecret>();
        if (secret?.Value is not { } password)
        {
            throw new CryptographicException("Las claves de /data/keys están cifradas pero falta CUBEKEEPER_KEY_SECRET.");
        }
        var ns = SecretXmlEncryptor.Ns;
        var salt = Convert.FromBase64String((string)encryptedElement.Element(ns + "salt")!);
        var nonce = Convert.FromBase64String((string)encryptedElement.Element(ns + "nonce")!);
        var tag = Convert.FromBase64String((string)encryptedElement.Element(ns + "tag")!);
        var ciphertext = Convert.FromBase64String((string)encryptedElement.Element(ns + "value")!);
        var iterations = (int?)encryptedElement.Attribute("iterations") ?? SecretXmlEncryptor.Iterations;
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(SecretXmlEncryptor.DeriveKey(password, salt, iterations), tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new CryptographicException("CUBEKEEPER_KEY_SECRET no es el mismo con el que se cifraron las claves de /data/keys.", ex);
        }
        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

/// <summary>Cifra las claves que se guardaron sin cifrar antes de configurar el secreto, sin cambiarlas.</summary>
public static class KeyFileMigration
{
    private static readonly XNamespace DataProtectionNs = "http://schemas.asp.net/2015/03/dataProtection";

    /// <summary>
    /// Comprueba que se pueden leer las claves cifradas. Si el secreto falta o es otro, Data Protection crearía en
    /// silencio una clave nueva y se perderían la sesión de Realms y el token de ntfy: mejor no arrancar.
    /// </summary>
    public static void VerifyReadable(string keysDirectory, KeyEncryptionSecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!Directory.Exists(keysDirectory))
        {
            return;
        }
        var services = new ServiceCollection().AddSingleton(secret).BuildServiceProvider();
        foreach (var file in Directory.GetFiles(keysDirectory, "*.xml"))
        {
            var encrypted = XDocument.Load(file).Descendants(SecretXmlEncryptor.Ns + "encryptedKey").FirstOrDefault();
            if (encrypted is null)
            {
                continue;
            }
            try
            {
                new SecretXmlDecryptor(services).Decrypt(encrypted);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    $"{ex.Message} Pon en CUBEKEEPER_KEY_SECRET el mismo valor que tenía. Si se ha perdido, borra /data/keys: " +
                    "habrá que volver a entrar en el panel, guardar de nuevo el token de ntfy y reconectar Realms.", ex);
            }
        }
    }

    /// <summary>Devuelve cuántos archivos se han cifrado.</summary>
    public static int EncryptExistingKeys(string keysDirectory, IXmlEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);
        if (!Directory.Exists(keysDirectory))
        {
            return 0;
        }
        var changed = 0;
        foreach (var file in Directory.GetFiles(keysDirectory, "*.xml"))
        {
            var doc = XDocument.Load(file);
            var pending = doc.Descendants()
                .Where(e => (bool?)e.Attribute(DataProtectionNs + "requiresEncryption") == true)
                .ToList();
            if (pending.Count == 0)
            {
                continue;
            }
            foreach (var element in pending)
            {
                var info = encryptor.Encrypt(element);
                // El mismo formato que escribe Data Protection: <encryptedSecret decryptorType="…">…</encryptedSecret>.
                element.ReplaceWith(new XElement(DataProtectionNs + "encryptedSecret",
                    new XAttribute("decryptorType", info.DecryptorType.AssemblyQualifiedName!),
                    info.EncryptedElement));
            }
            var tmp = file + ".tmp";
            doc.Save(tmp);
            File.Move(tmp, file, overwrite: true);
            changed++;
        }
        return changed;
    }
}
