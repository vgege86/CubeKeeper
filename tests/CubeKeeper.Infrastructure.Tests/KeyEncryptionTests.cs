using System.Security.Cryptography;
using CubeKeeper.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace CubeKeeper.Infrastructure.Tests;

public sealed class KeyEncryptionTests : IDisposable
{
    private const string Secret = "una-frase-larga-de-prueba-1234";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ck-keys-" + Guid.NewGuid().ToString("N"));

    private ServiceProvider Provider(string? secret)
    {
        var services = new ServiceCollection();
        var s = new KeyEncryptionSecret(secret);
        services.AddSingleton(s);
        services.AddDataProtection().SetApplicationName("CubeKeeper").PersistKeysToFileSystem(new DirectoryInfo(_dir));
        if (s.IsSet)
        {
            services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new SecretXmlEncryptor(s));
        }
        return services.BuildServiceProvider();
    }

    private static IDataProtector Protector(ServiceProvider sp) =>
        sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("prueba");

    private string KeyFile => Directory.GetFiles(_dir, "*.xml").Single();

    [Fact]
    public void New_keys_are_written_encrypted_and_read_back_with_the_secret()
    {
        string token;
        using (var sp = Provider(Secret))
        {
            token = Protector(sp).Protect("token de ntfy");
        }

        var xml = File.ReadAllText(KeyFile);
        Assert.Contains("encryptedKey", xml);
        Assert.DoesNotContain("<masterKey", xml);

        using var again = Provider(Secret);
        Assert.Equal("token de ntfy", Protector(again).Unprotect(token));
    }

    [Fact]
    public void Existing_plain_keys_are_encrypted_in_place_without_losing_data()
    {
        string token;
        using (var sp = Provider(null))
        {
            token = Protector(sp).Protect("sesión de Realms");
        }
        Assert.Contains("<masterKey", File.ReadAllText(KeyFile));

        Assert.Equal(1, KeyFileMigration.EncryptExistingKeys(_dir, new SecretXmlEncryptor(new KeyEncryptionSecret(Secret))));
        Assert.Equal(0, KeyFileMigration.EncryptExistingKeys(_dir, new SecretXmlEncryptor(new KeyEncryptionSecret(Secret))));

        Assert.DoesNotContain("<masterKey", File.ReadAllText(KeyFile));
        using var again = Provider(Secret);
        Assert.Equal("sesión de Realms", Protector(again).Unprotect(token));
    }

    [Theory]
    [InlineData("otra-frase-distinta-de-la-buena")]
    [InlineData(null)]
    public void Wrong_or_missing_secret_cannot_read_the_keys(string? other)
    {
        string token;
        using (var sp = Provider(Secret))
        {
            token = Protector(sp).Protect("x");
        }

        // Al arrancar se detecta antes de que Data Protection cree otra clave en silencio.
        var ex = Assert.Throws<InvalidOperationException>(() => KeyFileMigration.VerifyReadable(_dir, new KeyEncryptionSecret(other)));
        Assert.Contains("CUBEKEEPER_KEY_SECRET", ex.Message);
        KeyFileMigration.VerifyReadable(_dir, new KeyEncryptionSecret(Secret));

        using var again = Provider(other);
        Assert.ThrowsAny<CryptographicException>(() => Protector(again).Unprotect(token));
    }

    [Fact]
    public void Short_secrets_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new KeyEncryptionSecret("corta"));
        Assert.Throws<InvalidOperationException>(() => new KeyEncryptionSecret(KeyEncryptionSecret.Placeholder));
        Assert.False(new KeyEncryptionSecret("").IsSet);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
