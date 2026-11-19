using System.Buffers.Binary;
using CubeKeeper.Core.Realms;

namespace CubeKeeper.Core.Tests;

public class XboxRequestSignerTests
{
    private static readonly Uri Url = new("https://xsts.auth.xboxlive.com/xsts/authorize");

    [Fact]
    public void Signature_header_has_version_timestamp_and_p1363_signature()
    {
        using var signer = XboxRequestSigner.CreateNew();
        var now = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

        var raw = Convert.FromBase64String(signer.Sign(Url, "", "{}", now));

        Assert.Equal(4 + 8 + 64, raw.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(raw));
        Assert.Equal(XboxRequestSigner.WindowsTimestamp(now), BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(4)));
    }

    [Fact]
    public void Windows_timestamp_counts_100ns_since_1601()
    {
        Assert.Equal(116444736000000000UL, XboxRequestSigner.WindowsTimestamp(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Signature_verifies_and_detects_tampering()
    {
        using var signer = XboxRequestSigner.CreateNew();
        var header = signer.Sign(Url, "", """{"a":1}""", DateTimeOffset.UtcNow);

        Assert.True(signer.Verify(Url, "", """{"a":1}""", header));
        Assert.False(signer.Verify(Url, "", """{"a":2}""", header));
        Assert.False(signer.Verify(new Uri("https://xsts.auth.xboxlive.com/otra"), "", """{"a":1}""", header));
    }

    [Fact]
    public void Key_survives_export_and_import()
    {
        using var signer = XboxRequestSigner.CreateNew();
        using var again = XboxRequestSigner.Import(signer.ExportPrivateKey());
        var header = again.Sign(Url, "", "x", DateTimeOffset.UtcNow);

        Assert.True(signer.Verify(Url, "", "x", header));
        Assert.Equal(signer.ProofKey().ToJsonString(), again.ProofKey().ToJsonString());
    }

    [Fact]
    public void Proof_key_is_an_es256_jwk()
    {
        using var signer = XboxRequestSigner.CreateNew();
        var jwk = signer.ProofKey();

        Assert.Equal("EC", jwk["kty"]!.GetValue<string>());
        Assert.Equal("P-256", jwk["crv"]!.GetValue<string>());
        Assert.Equal("ES256", jwk["alg"]!.GetValue<string>());
        Assert.Equal("sig", jwk["use"]!.GetValue<string>());
        var x = jwk["x"]!.GetValue<string>();
        Assert.DoesNotContain('=', x);
        Assert.DoesNotContain('+', x);
        Assert.Equal(43, x.Length);
    }
}
