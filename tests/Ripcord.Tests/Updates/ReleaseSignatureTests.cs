using System.Security.Cryptography;
using System.Text;
using Ripcord.Domain.Updates;

namespace Ripcord.Tests.Updates;

/// The one decision that stands between a release page and a binary running with Hyper-V
/// privileges on both hosts. Every test below is a way of getting that decision wrong, and
/// every one of them has to end in a refusal: an unverifiable release is not installed, and
/// nothing about that is a degraded mode.
///
/// The key pair is generated here rather than fixed in the file. A test that signs with a real
/// key and verifies with the matching one is testing the thing; a test with a recorded
/// signature is testing that a constant did not change.
public sealed class ReleaseSignatureTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("this is a ripcord release");

    [Fact]
    public void A_release_signed_by_the_pinned_key_is_genuine()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        SignatureVerdict verdict = ReleaseSignature.Verify(
            Payload, Sign(key, Payload), PublicKeyOf(key));

        Assert.True(verdict.IsGenuine);
    }

    /// The whole point. A key that is not the pinned one proves nothing, however well formed
    /// the signature is.
    [Fact]
    public void A_release_signed_by_another_key_is_refused()
    {
        using ECDsa pinned = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        SignatureVerdict verdict = ReleaseSignature.Verify(
            Payload, Sign(stranger, Payload), PublicKeyOf(pinned));

        Assert.False(verdict.IsGenuine);
        Assert.Contains("signature", verdict.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_payload_altered_by_one_byte_is_refused()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = Sign(key, Payload);

        byte[] tampered = [.. Payload];
        tampered[^1] ^= 0x01;

        Assert.False(ReleaseSignature.Verify(tampered, signature, PublicKeyOf(key)).IsGenuine);
    }

    [Fact]
    public void A_truncated_signature_is_refused()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = Sign(key, Payload);

        Assert.False(
            ReleaseSignature.Verify(Payload, signature.AsSpan()[..^4], PublicKeyOf(key)).IsGenuine);
    }

    /// A release published with no signature file at all. It must not read as "nothing to
    /// check" — an absent proof is a failed proof.
    [Fact]
    public void An_empty_signature_is_refused()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        SignatureVerdict verdict = ReleaseSignature.Verify(Payload, [], PublicKeyOf(key));

        Assert.False(verdict.IsGenuine);
        Assert.Contains("no signature", verdict.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// A key that cannot be read is a binary that cannot decide. It refuses rather than
    /// installing, because the alternative is trusting a release nothing checked.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a key at all")]
    [InlineData("-----BEGIN PUBLIC KEY-----\nZm9v\n-----END PUBLIC KEY-----")]
    public void A_pinned_key_that_cannot_be_read_refuses_rather_than_trusting(string key)
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        SignatureVerdict verdict = ReleaseSignature.Verify(Payload, Sign(signer, Payload), key);

        Assert.False(verdict.IsGenuine);
        Assert.Contains("key", verdict.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_payload_is_refused_whatever_is_presented_with_it()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        SignatureVerdict verdict = ReleaseSignature.Verify([], Sign(key, []), PublicKeyOf(key));

        Assert.False(verdict.IsGenuine);
        Assert.Contains("empty", verdict.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// A refusal always says which one it was. "It did not verify" sends somebody to read the
    /// wrong half of the release process.
    [Fact]
    public void Every_refusal_explains_itself()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        foreach (SignatureVerdict verdict in new[]
        {
            ReleaseSignature.Verify(Payload, [], PublicKeyOf(key)),
            ReleaseSignature.Verify(Payload, Sign(key, Payload), "rubbish"),
            ReleaseSignature.Verify([], Sign(key, []), PublicKeyOf(key)),
        })
        {
            Assert.False(verdict.IsGenuine);
            Assert.NotEmpty(verdict.Explanation);
        }
    }

    /// DER, the format the release workflow's `openssl dgst -sha256 -sign` writes. Signing
    /// here in .NET's default format instead would make every test pass against an
    /// implementation that refuses every real release.
    private static byte[] Sign(ECDsa key, byte[] payload) =>
        key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    private static string PublicKeyOf(ECDsa key) =>
        new(PemEncoding.Write("PUBLIC KEY", key.ExportSubjectPublicKeyInfo()));
}
