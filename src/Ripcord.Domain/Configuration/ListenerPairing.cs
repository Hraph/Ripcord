using Ripcord.Domain.Deployment;

namespace Ripcord.Domain.Configuration;

/// What `ripcord pair` would write: this host's certificate and the other host's, into the
/// `listener` section. Either a new text or the reason there is none.
public sealed record PairingPlan(
    string? Local, string? Peer, string? Yaml, bool Changes, string? Refusal)
{
    public static PairingPlan Refused(string reason) => new(null, null, null, false, reason);
}

/// Both thumbprints set from one typed value. The other host's is typed because only it can
/// read it; this host's is found in `LocalMachine\My`, which is what makes one command enough.
public static class ListenerPairing
{
    private const string LocalKey = "local_certificate_thumbprint";

    private const string PeerKey = "peer_certificate_thumbprint";

    /// The samples carry this line above the placeholders; it is false once they are replaced.
    private const string PlaceholderComment =
        "# Placeholders, refused as they are: this host's certificate, then the other host's.";

    /// One line to carry from one host to the other, like a public key: the host's name, so
    /// the receiving side can tell it was pasted on the right machine, and its thumbprint.
    public static string Key(string machineName, string thumbprint) =>
        $"{machineName}:{thumbprint.ToUpperInvariant()}";

    /// `peerHostname` is what the file names as the other host, when it names one.
    public static PairingPlan Plan(
        string? previous,
        string machineName,
        string? peerHostname,
        string? configuredLocal,
        HostCertificates mine,
        string typedKey,
        string? typedLocal = null)
    {
        ArgumentNullException.ThrowIfNull(mine);
        ArgumentNullException.ThrowIfNull(typedKey);

        if (previous is null)
        {
            return PairingPlan.Refused("there is no ripcord.yaml yet: run 'ripcord init' first");
        }

        int colon = typedKey.LastIndexOf(':');
        string? keyHost = colon > 0 ? typedKey[..colon].Trim() : null;

        if (Thumbprint(colon > 0 ? typedKey[(colon + 1)..] : typedKey) is not { } peer)
        {
            return PairingPlan.Refused(
                $"'{typedKey}' is not a pairing key: run 'ripcord service' on the other host, "
                + "it prints the line to paste here");
        }

        if (keyHost is not null && string.Equals(keyHost, machineName, StringComparison.OrdinalIgnoreCase))
        {
            return PairingPlan.Refused(
                $"that key is {machineName}'s own: run this on the other host, with it");
        }

        if (keyHost is not null
            && peerHostname is { Length: > 0 }
            && !string.Equals(keyHost, peerHostname, StringComparison.OrdinalIgnoreCase))
        {
            return PairingPlan.Refused(
                $"that key is {keyHost}'s, and ripcord.yaml names {peerHostname} as the other host");
        }

        if (ListenerSettings.SamplePlaceholders.Contains(peer))
        {
            return PairingPlan.Refused("that is the sample's placeholder, not the other host's thumbprint");
        }

        if (mine.Unreadable is { } unreadable)
        {
            return PairingPlan.Refused($"LocalMachine\\My could not be read: {unreadable}");
        }

        if (Local(mine, Thumbprint(configuredLocal), typedLocal) is not { } local)
        {
            return PairingPlan.Refused(LocalRefusal(mine, typedLocal));
        }

        if (local == peer)
        {
            return PairingPlan.Refused(
                "that is this host's own certificate: run this on the other host, with it");
        }

        if (Rewrite(previous, local, peer) is not { } yaml)
        {
            return PairingPlan.Refused(
                "the listener section is written on one line: edit its two thumbprints by hand");
        }

        return new PairingPlan(local, peer, yaml, yaml != previous, null);
    }

    /// The one already configured if it is still usable, else the only one there is. Two or
    /// more and nothing configured is a choice for the operator, not for a sort order.
    private static string? Local(HostCertificates mine, string? configured, string? typed)
    {
        string[] usable = [.. mine.Usable.Select(certificate => certificate.Thumbprint.ToUpperInvariant())];

        if (typed is not null)
        {
            return Thumbprint(typed) is { } chosen && usable.Contains(chosen) ? chosen : null;
        }

        if (configured is not null && usable.Contains(configured))
        {
            return configured;
        }

        return usable.Length == 1 ? usable[0] : null;
    }

    private static string LocalRefusal(HostCertificates mine, string? typed) =>
        (typed, mine.Usable.Count) switch
        {
            (not null, _) => $"'{typed}' is not one of this host's certificates for {mine.Subject}",
            (null, 0) =>
                $"this host has no certificate for {mine.Subject} with a private key in "
                + "LocalMachine\\My that has not expired",
            _ => $"this host has {mine.Usable.Count} certificates for {mine.Subject}: "
                + "choose one with --local <thumbprint>, as 'ripcord service' lists them",
        };

    /// The same normalisation as the validator: spaces out, upper case.
    private static string? Thumbprint(string? value)
    {
        string normalised = (value ?? "").Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant();

        return normalised.Length == 40 && normalised.All(Uri.IsHexDigit) ? normalised : null;
    }

    /// Only the two keys change, in place; every other line, comment and line ending is the
    /// operator's and stays. A file with no `listener` section gets one at the end.
    private static string? Rewrite(string text, string local, string peer)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];

        int header = lines.FindIndex(line => line.StartsWith("listener:", StringComparison.Ordinal));

        if (header < 0)
        {
            while (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            lines.AddRange(
            [
                "",
                "listener:",
                "  enabled: true",
                $"  {LocalKey}: \"{local}\"",
                $"  {PeerKey}: \"{peer}\"",
                "",
            ]);

            return string.Join(newline, lines);
        }

        string rest = lines[header]["listener:".Length..].Trim();

        if (rest.Length > 0 && !rest.StartsWith('#'))
        {
            return null;
        }

        int end = header + 1;

        while (end < lines.Count && (lines[end].Length == 0 || char.IsWhiteSpace(lines[end][0])))
        {
            end++;
        }

        bool localSet = Set(lines, header + 1, end, LocalKey, local);
        bool peerSet = Set(lines, header + 1, end, PeerKey, peer);

        List<string> missing = [];

        if (!localSet)
        {
            missing.Add($"  {LocalKey}: \"{local}\"");
        }

        if (!peerSet)
        {
            missing.Add($"  {PeerKey}: \"{peer}\"");
        }

        lines.InsertRange(header + 1, missing);
        lines.RemoveAll(line => line.Trim() == PlaceholderComment);

        return string.Join(newline, lines);
    }

    private static bool Set(List<string> lines, int start, int end, string key, string value)
    {
        for (int index = start; index < end; index++)
        {
            string trimmed = lines[index].TrimStart();

            if (trimmed.StartsWith(key + ":", StringComparison.Ordinal))
            {
                string indent = lines[index][..(lines[index].Length - trimmed.Length)];
                lines[index] = $"{indent}{key}: \"{value}\"";
                return true;
            }
        }

        return false;
    }
}
