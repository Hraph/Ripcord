using System.Security.Cryptography;

namespace Ripcord.Domain.Updates;

/// Whether a downloaded release is the one this binary's publisher signed. Never a bare bool:
/// a refusal has to say which refusal it was, or somebody spends an outage reading the wrong
/// half of the release process.
public sealed record SignatureVerdict(bool IsGenuine, string Explanation)
{
    public static SignatureVerdict Genuine { get; } =
        new(true, "the signature matches the key this binary was built with");

    public static SignatureVerdict Refused(string explanation) => new(false, explanation);
}

/// ECDSA P-256 over SHA-256, against a public key compiled into the binary.
///
/// The signature is **DER**, the format `openssl dgst -sha256 -sign` writes, because that
/// is what the release workflow signs with. .NET's own default is the other one — the
/// fixed-width r||s concatenation — and the two are not distinguishable by length alone, so
/// the format is named at the call rather than left to a default. A mismatch here would
/// refuse every genuine release, which is the safe direction but an outage all the same.
///
/// This lives in the Domain, which holds no infrastructure, because it is the single most
/// important decision the update path makes and the project's rule is that a decision which
/// cannot be tested without Windows is a decision nobody checks. It is pure — bytes in, a
/// verdict out, no I/O, no clock, no file — and `System.Security.Cryptography` is in the
/// shared framework, so it costs the Domain no package and the boundary matrix is untouched.
///
/// Every path that is not a proof is a refusal. An absent signature, an unreadable key and an
/// empty payload are all *failures to establish* that the release is genuine, and none of them
/// is allowed to read as "nothing to check".
public static class ReleaseSignature
{
    public static SignatureVerdict Verify(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature, string? pinnedPublicKey)
    {
        if (payload.IsEmpty)
        {
            return SignatureVerdict.Refused("the downloaded release is empty");
        }

        if (signature.IsEmpty)
        {
            return SignatureVerdict.Refused(
                "the release carries no signature; an absent proof is a failed proof");
        }

        if (string.IsNullOrWhiteSpace(pinnedPublicKey))
        {
            return SignatureVerdict.Refused(
                "this binary carries no signing key, so it cannot tell a genuine release "
                + "from any other");
        }

        using ECDsa? key = Read(pinnedPublicKey);

        if (key is null)
        {
            return SignatureVerdict.Refused(
                "the signing key compiled into this binary could not be read");
        }

        // A malformed signature throws rather than returning false, and a throw here would
        // become an unhandled exception in a command that is about to replace a binary.
        try
        {
            return key.VerifyData(
                payload, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence)
                ? SignatureVerdict.Genuine
                : SignatureVerdict.Refused(
                    "the signature does not match this release and the key this binary "
                    + "was built with");
        }
        catch (CryptographicException exception)
        {
            return SignatureVerdict.Refused(
                $"the signature could not be checked: {exception.Message}");
        }
    }

    private static ECDsa? Read(string pinnedPublicKey)
    {
        ECDsa key = ECDsa.Create();

        try
        {
            key.ImportFromPem(pinnedPublicKey);
            return key;
        }
        catch (Exception exception) when (
            exception is CryptographicException or ArgumentException)
        {
            key.Dispose();
            return null;
        }
    }
}
