using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CubeKeeper.Core.Realms;

/// <summary>
/// Firma de las peticiones a Xbox Live (cabecera <c>Signature</c>), como hace el cliente oficial y prismarine-auth:
/// ECDSA P-256 con SHA-256 sobre
/// <c>versión(1, int32 BE) · 0 · marca de tiempo Windows (uint64 BE) · 0 · "POST\0" · ruta\0 · token\0 · cuerpo\0</c>,
/// y la cabecera es <c>versión(int32 BE) · marca de tiempo (uint64 BE) · firma (r||s)</c> en base64.
/// </summary>
public sealed class XboxRequestSigner : IDisposable
{
    private const long WindowsEpochOffsetSeconds = 11644473600;
    private readonly ECDsa _key;

    public XboxRequestSigner(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.KeySize != 256)
        {
            throw new ArgumentException("La clave debe ser ECDSA P-256.", nameof(key));
        }
        _key = key;
    }

    public static XboxRequestSigner CreateNew() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Clave privada en PKCS#8, para guardarla (cifrada) y reutilizarla.</summary>
    public byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();

    public static XboxRequestSigner Import(byte[] pkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return new XboxRequestSigner(key);
    }

    /// <summary>Clave pública en JWK (<c>ProofKey</c> de las peticiones de dispositivo, título y XSTS).</summary>
    public JsonObject ProofKey()
    {
        var p = _key.ExportParameters(includePrivateParameters: false);
        return new JsonObject
        {
            ["kty"] = "EC",
            ["x"] = Base64Url(p.Q.X!),
            ["y"] = Base64Url(p.Q.Y!),
            ["crv"] = "P-256",
            ["alg"] = "ES256",
            ["use"] = "sig",
        };
    }

    public string Sign(Uri url, string authorizationToken, string body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(url);
        var timestamp = WindowsTimestamp(now);
        var data = SignedData(url, authorizationToken, body, timestamp);
        var signature = _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var header = new byte[4 + 8 + signature.Length];
        BinaryPrimitives.WriteInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(4), timestamp);
        signature.CopyTo(header, 12);
        return Convert.ToBase64String(header);
    }

    /// <summary>Comprueba una cabecera de firma (para las pruebas y para depurar).</summary>
    public bool Verify(Uri url, string authorizationToken, string body, string signatureHeader)
    {
        var header = Convert.FromBase64String(signatureHeader);
        if (header.Length < 12 || BinaryPrimitives.ReadInt32BigEndian(header) != 1)
        {
            return false;
        }
        var timestamp = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4));
        var data = SignedData(url, authorizationToken, body, timestamp);
        return _key.VerifyData(data, header.AsSpan(12), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Segundos desde 1601 en unidades de 100 ns, redondeado al segundo como el cliente oficial.</summary>
    public static ulong WindowsTimestamp(DateTimeOffset now) =>
        (ulong)(now.ToUnixTimeSeconds() + WindowsEpochOffsetSeconds) * 10_000_000UL;

    private static byte[] SignedData(Uri url, string authorizationToken, string body, ulong timestamp)
    {
        using var ms = new MemoryStream();
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(buf, 1);
        ms.Write(buf[..4]);
        ms.WriteByte(0);
        BinaryPrimitives.WriteUInt64BigEndian(buf, timestamp);
        ms.Write(buf);
        ms.WriteByte(0);
        WriteNullTerminated(ms, "POST");
        WriteNullTerminated(ms, url.AbsolutePath);
        WriteNullTerminated(ms, authorizationToken ?? string.Empty);
        WriteNullTerminated(ms, body ?? string.Empty);
        return ms.ToArray();
    }

    private static void WriteNullTerminated(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        s.Write(bytes);
        s.WriteByte(0);
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _key.Dispose();
}
