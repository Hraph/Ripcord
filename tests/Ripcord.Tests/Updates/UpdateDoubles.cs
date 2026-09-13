using System.Security.Cryptography;
using System.Text;
using Ripcord.Domain;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Updates;

namespace Ripcord.Tests.Updates;

/// A real key pair, generated once for the test run. The update tests sign with it and the
/// code verifies against it, so nothing here is a stub of the security property — it is the
/// security property, exercised.
internal static class Keys
{
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static readonly ECDsa Stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string Pinned { get; } = Pem(Signer);

    public static byte[] Sign(byte[] payload) => Sign(Signer, payload);

    public static byte[] SignAsStranger(byte[] payload) => Sign(Stranger, payload);

    private static byte[] Sign(ECDsa key, byte[] payload) =>
        key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    private static string Pem(ECDsa key) =>
        new(PemEncoding.Write("PUBLIC KEY", key.ExportSubjectPublicKeyInfo()));
}

/// A release feed that hands back bytes, signed or not, or nothing at all.
internal sealed class Source(FetchedRelease answer) : IReleaseSource
{
    public static readonly byte[] Binary = Encoding.UTF8.GetBytes("a new ripcord.exe");

    public Task<FetchedRelease> FetchAsync(string version, CancellationToken cancellationToken) =>
        Task.FromResult(answer);

    public static Source Genuine() =>
        new(FetchedRelease.Fetched(Binary, Keys.Sign(Binary)));

    public static Source SignedByAStranger() =>
        new(FetchedRelease.Fetched(Binary, Keys.SignAsStranger(Binary)));

    public static Source Unreachable() =>
        new(FetchedRelease.Failed("api.github.com could not be reached"));
}

internal static class Subjects
{
    public static UpdateSubject Available() => Of(UpdateStatus.Between("0.1.0", "0.2.0"));

    public static UpdateSubject UpToDate() => Of(UpdateStatus.Between("0.2.0", "0.2.0"));

    private static UpdateSubject Of(UpdateStatus status) =>
        new(
            status,
            true,
            VersionSkew.Between(Build(), Build()),
            OperatingMode.Normal,
            "HV-PRIMARY-01");

    private static BuildIdentity Build() => new("0.1.0", "abc123def456");
}
