using Ripcord.Adapters.Yaml;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Inventory;

namespace Ripcord.Tests.Configuration;

/// `ripcord pair`: one line carried from the other host sets both thumbprints, and nothing
/// else in the file moves.
public class ListenerPairingTests
{
    private const string Me = "HV-REPLICA-01";

    private const string Other = "HV-PRIMARY-01";

    private const string Mine = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";

    private const string Theirs = "0F1E2D3C4B5A69788796A5B4C3D2E1F012345678";

    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    private static HostCertificates Certificates(params string[] thumbprints) =>
        HostCertificates.For(
            [.. thumbprints.Select(thumbprint => new CertificateFact(thumbprint, $"CN={Me}", Now.AddYears(1)))],
            Me,
            Now);

    private static PairingPlan Plan(
        string? previous, string key, HostCertificates? mine = null, string? typedLocal = null) =>
        ListenerPairing.Plan(
            previous,
            previous is null ? null : Yaml.Read(previous).Document,
            Me,
            mine ?? Certificates(Mine),
            key,
            typedLocal);

    private const string Head = "schema_version: 1\npeer:\n  hostname: HV-PRIMARY-01\n";

    /// The shipped sample, as `install.ps1` leaves it: placeholders in, a valid file out.
    [Fact]
    public void Over_the_sample_the_two_placeholders_are_replaced_and_the_file_validates()
    {
        string sample = Samples.Read("ripcord.dr.yaml");

        PairingPlan plan = Plan(sample, ListenerPairing.Key(Other, Theirs));

        Assert.Null(plan.Refusal);
        Assert.True(plan.Changes);
        Assert.Contains($"  local_certificate_thumbprint: \"{Mine}\"", plan.Yaml, StringComparison.Ordinal);
        Assert.Contains($"  peer_certificate_thumbprint: \"{Theirs}\"", plan.Yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("# Placeholders", plan.Yaml, StringComparison.Ordinal);

        // Nothing but those lines changed.
        string[] before = sample.Split('\n');
        string[] after = plan.Yaml!.Split('\n');
        Assert.Equal(before.Length - 1, after.Length);
        Assert.Equal(3, before.Except(after).Count());

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");
        File.WriteAllText(path, plan.Yaml);
        Assert.Empty(ConfigurationValidator.Validate(new YamlConfigStore().Read(path).Document, Me).Errors);
    }

    [Fact]
    public void Running_it_again_changes_nothing()
    {
        string once = Plan(Samples.Read("ripcord.dr.yaml"), ListenerPairing.Key(Other, Theirs)).Yaml!;

        PairingPlan again = Plan(once, ListenerPairing.Key(Other, Theirs));

        Assert.False(again.Changes);
        Assert.Equal(once, again.Yaml);
    }

    /// A bare thumbprint works too, and the Windows forms of it are the same certificate.
    [Theory]
    [InlineData(Theirs)]
    [InlineData("0f 1e 2d 3c 4b 5a 69 78 87 96 a5 b4 c3 d2 e1 f0 12 34 56 78")]
    public void A_bare_thumbprint_is_taken(string key) =>
        Assert.Equal(Theirs, Plan(Samples.Read("ripcord.dr.yaml"), key).Peer);

    [Fact]
    public void A_file_with_no_listener_gets_one_with_crlf_kept()
    {
        const string Previous = "schema_version: 1\r\nnode:\r\n  hostname: HV-REPLICA-01\r\n";

        PairingPlan plan = Plan(Previous, ListenerPairing.Key(Other, Theirs));

        Assert.StartsWith(Previous, plan.Yaml, StringComparison.Ordinal);
        Assert.Contains(
            "listener:\r\n  enabled: true\r\n"
                + $"  local_certificate_thumbprint: \"{Mine}\"\r\n"
                + $"  peer_certificate_thumbprint: \"{Theirs}\"\r\n",
            plan.Yaml,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData($"{Me}:{Theirs}", "own")]
    [InlineData($"HV-ELSEWHERE:{Theirs}", "ripcord.yaml names HV-PRIMARY-01")]
    [InlineData("HV-PRIMARY-01:not-a-thumbprint", "not a pairing key")]
    [InlineData("AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555", "placeholder")]
    [InlineData(Mine, "this host's own certificate")]
    public void A_key_from_the_wrong_place_is_refused(string key, string said)
    {
        PairingPlan plan = Plan(Samples.Read("ripcord.dr.yaml"), key);

        Assert.Null(plan.Yaml);
        Assert.Contains(said, plan.Refusal, StringComparison.Ordinal);
    }

    /// Two usable certificates and none configured is the operator's choice, not a sort's.
    [Fact]
    public void Two_certificates_ask_for_local_and_take_it()
    {
        const string Second = "1234567890ABCDEF1234567890ABCDEF12345678";
        HostCertificates two = Certificates(Mine, Second);

        Assert.Contains("--local", Plan(Samples.Read("ripcord.dr.yaml"), Theirs, two).Refusal, StringComparison.Ordinal);
        Assert.Equal(Second, Plan(Samples.Read("ripcord.dr.yaml"), Theirs, two, Second).Local);
    }

    [Fact]
    public void No_certificate_for_this_host_is_refused_by_subject() =>
        Assert.Contains(
            $"no certificate for CN={Me}",
            Plan(Samples.Read("ripcord.dr.yaml"), Theirs, Certificates()).Refusal,
            StringComparison.Ordinal);

    [Fact]
    public void No_file_yet_points_to_init() =>
        Assert.Contains("ripcord init", Plan(null, Theirs).Refusal, StringComparison.Ordinal);

    [Fact]
    public void A_listener_written_on_one_line_is_left_to_the_operator() =>
        Assert.Contains(
            "on one line",
            Plan("listener: { enabled: true }\n", Theirs).Refusal,
            StringComparison.Ordinal);

    /// A column-0 comment does not end the section: the key under it is found, not duplicated.
    [Fact]
    public void A_key_below_a_column_zero_comment_is_rewritten_in_place()
    {
        string previous = Head + "listener:\n  enabled: true\n# the pair\n"
            + $"  local_certificate_thumbprint: \"{Mine}\"\n  peer_certificate_thumbprint: \"\"\n";

        PairingPlan plan = Plan(previous, Theirs);

        Assert.Single(plan.Yaml!.Split('\n'), line => line.Contains("peer_certificate", StringComparison.Ordinal));
        Assert.True(ListenerPairing.Landed(plan, Yaml.Read(plan.Yaml).Document));
    }

    /// Keys commented out and a four-space section: the inserted lines must parse.
    [Fact]
    public void Inserted_keys_take_the_sections_indentation()
    {
        string previous = Head + "listener:\n    enabled: true\n    # peer_certificate_thumbprint: \"\"\n";

        PairingPlan plan = Plan(previous, Theirs);

        Assert.Contains($"\n    peer_certificate_thumbprint: \"{Theirs}\"", plan.Yaml, StringComparison.Ordinal);
        Assert.Contains("    # peer_certificate_thumbprint", plan.Yaml, StringComparison.Ordinal);
        Assert.True(ListenerPairing.Landed(plan, Yaml.Read(plan.Yaml!).Document));
    }

    [Fact]
    public void A_trailing_comment_on_the_key_line_stays()
    {
        string previous = Head + "listener:\n  enabled: true\n"
            + $"  peer_certificate_thumbprint: \"{Mine}\"  # the other host\n";

        Assert.Contains(
            $"  peer_certificate_thumbprint: \"{Theirs}\" # the other host",
            Plan(previous, Theirs).Yaml,
            StringComparison.Ordinal);
    }

    /// The certificate dialog's copy starts with an invisible left-to-right mark.
    [Fact]
    public void The_certificate_dialogs_invisible_mark_is_dropped() =>
        Assert.Equal(Theirs, Plan(Samples.Read("ripcord.dr.yaml"), "\u200e" + Theirs.ToLowerInvariant()).Peer);

    [Fact]
    public void A_file_that_does_not_load_is_refused() =>
        Assert.Contains("does not load", Plan("listener: [\n", Theirs).Refusal, StringComparison.Ordinal);

    [Theory]
    [InlineData("listener:\n  enabled: false\n", true)]
    [InlineData("listener:\n  port: 7443\n", true)]
    [InlineData("listener:\n  enabled: true\n", false)]
    [InlineData("", false)]
    public void A_listener_left_disabled_is_said(string section, bool said) =>
        Assert.Equal(said, Plan(Head + section, Theirs).StaysDisabled);

    [Fact]
    public void The_file_as_it_was_has_not_landed()
    {
        string sample = Samples.Read("ripcord.dr.yaml");

        Assert.False(ListenerPairing.Landed(Plan(sample, Theirs), Yaml.Read(sample).Document));
    }
}
