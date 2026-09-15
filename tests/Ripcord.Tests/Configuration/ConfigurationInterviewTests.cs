using Ripcord.Adapters.Yaml;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Ports.Configuration;

namespace Ripcord.Tests.Configuration;

/// `ripcord init`, driven from a list of typed strings instead of a console.
///
/// The first two tests are the reason the interview is a state machine in the Domain rather
/// than a loop in the CLI. One asserts that what it produces **validates** — the whole point,
/// because the file this replaces was a template that deliberately did not. The other asserts
/// that running it again over an existing file and pressing nothing but Enter gives that file
/// back, with the sections it never asked about untouched.
public class ConfigurationInterviewTests
{
    private const string Machine = "HV-REPLICA-01";

    private static readonly InterviewFacts Host = new(
        Machine,
        [
            new HostSwitch("vSwitch-LAN", SwitchConnectivity.External),
            new HostSwitch("vSwitch-Isolated", SwitchConnectivity.Private),
        ],
        [
            new InterviewVm("VM-DC-01", Replicated: true, Running: true),
            new InterviewVm("VM-APP-01", Replicated: true, Running: true),
            new InterviewVm("VM-BACKUP-01", Replicated: false, Running: false),
        ]);

    /// A first run on a host being set up: the role, the other host, the switch by number, two
    /// VMs by number, their priorities, and the six figures accepted as shown.
    private static readonly string[] FirstRun =
    [
        "dr",
        "HV-PRIMARY-01",
        "192.0.2.10",
        "1",
        "1,2",
        "P1", "y",
        "P2", "n",
        "y",
    ];

    /// The assertion the whole design exists for.
    [Fact]
    public void What_the_interview_produces_passes_the_validator()
    {
        string yaml = ConfigurationTemplate.Render(Answer(FirstRun));

        ConfigurationRead read = new YamlConfigStore().Read(WrittenTo(yaml));
        Assert.Empty(read.Errors);

        ConfigurationValidation validation =
            ConfigurationValidator.Validate(read.Document, Machine);

        Assert.Empty(validation.Errors);
        Assert.NotNull(validation.Configuration);
    }

    [Fact]
    public void The_answers_land_where_they_were_meant_to()
    {
        ConfigurationDraft draft = Answer(FirstRun);

        Assert.Equal(ExpectedRole.Replica, draft.Role);
        Assert.Equal("HV-PRIMARY-01", draft.PeerHostname);
        Assert.Equal("192.0.2.10", draft.PeerAddress);
        Assert.Equal("vSwitch-LAN", draft.SwitchName);
        Assert.Equal(Machine, draft.NodeHostname);

        Assert.Equal(
            [
                new DraftVm("VM-DC-01", VmPriority.P1, IsDomainController: true),
                new DraftVm("VM-APP-01", VmPriority.P2, IsDomainController: false),
            ],
            draft.Vms);
    }

    /// The node name is taken from the machine and never asked. A file whose node name is not
    /// this host is refused by name at startup, so asking would be offering a way to fail.
    [Fact]
    public void This_host_is_never_something_the_operator_types() =>
        Assert.DoesNotContain(
            Questions(FirstRun), question => question.Key.StartsWith("node.host", StringComparison.Ordinal));

    /// Re-running is an edit. Enter through every question and the file that comes out is the
    /// file that went in.
    [Fact]
    public void A_re_run_answered_with_nothing_but_enter_gives_the_same_file_back()
    {
        string original = ConfigurationTemplate.Render(Answer(FirstRun));
        ConfigurationDocument? seed = new YamlConfigStore().Read(WrittenTo(original)).Document;

        ConfigurationDraft again = Answer(
            [.. Enumerable.Repeat("", 20)], seed);

        Assert.Equal(ConfigurationTemplate.Render(again, original), original);
    }

    /// The other half of "without losing it". A section the interview never asks about — and,
    /// worse, a key no version of this binary models — has to come back out.
    [Fact]
    public void Sections_the_interview_never_asks_about_survive_a_re_run()
    {
        const string Kept = """

            # The pair channel, set up by hand months ago.
            listener:
              enabled: true
              port: 7443

            something_a_later_version_added:
              with: a value
            """;

        string original = ConfigurationTemplate.Render(Answer(FirstRun)) + Kept;
        ConfigurationDocument? seed = new YamlConfigStore().Read(WrittenTo(original)).Document;

        string rewritten = ConfigurationTemplate.Render(
            Answer([.. Enumerable.Repeat("", 20)], seed),
            original);

        Assert.Contains("# The pair channel, set up by hand months ago.", rewritten, StringComparison.Ordinal);
        Assert.Contains("  port: 7443", rewritten, StringComparison.Ordinal);
        Assert.Contains("something_a_later_version_added:", rewritten, StringComparison.Ordinal);
        Assert.Equal(["listener", "something_a_later_version_added"], ConfigurationTemplate.CarriedOver(original));
    }

    /// The regression that matters, against the real file shipped in this repository rather
    /// than a fixture written to pass.
    ///
    /// The interview asks about six fields of `vms` and `replication`; the sample declares
    /// twice that many. A rewrite built only from the answers gives a VM back without its
    /// `failover: manual` and without `has_passthrough_disk` — the two lines that exist to
    /// stop a 4 TB backup box being swept into a failover — and quietly revokes the
    /// unattended-test authorisation. Nothing on the screen would say so.
    [Fact]
    public void A_re_run_over_the_shipped_sample_keeps_every_field_it_never_asked_about()
    {
        string sample = File.ReadAllText(
            Path.Combine(Architecture.RepositoryLayout.Root, "config", "ripcord.primary.yaml"));

        ConfigurationDocument? seed = new YamlConfigStore().Read(WrittenTo(sample)).Document;

        // The primary's own file, read on the primary: pressing Enter through all of it.
        string rewritten = ConfigurationTemplate.Render(
            Answer(
                [.. Enumerable.Repeat("", 40)],
                seed,
                InterviewFacts.Unread("HV-PRIMARY-01")),
            sample);

        Assert.Contains("failover: manual", rewritten, StringComparison.Ordinal);
        Assert.Contains("has_passthrough_disk: true", rewritten, StringComparison.Ordinal);
        Assert.Contains("guest_os_support_ends: 2026-10-13", rewritten, StringComparison.Ordinal);
        Assert.Contains("expected_startup_ram_mb: 2048", rewritten, StringComparison.Ordinal);
        Assert.Contains("check_bitlocker_autounlock: true", rewritten, StringComparison.Ordinal);

        // And it still validates, which is the other half: carried fields have to come back in
        // a form the file will take.
        Assert.Empty(
            ConfigurationValidator.Validate(
                new YamlConfigStore().Read(WrittenTo(rewritten)).Document,
                "HV-PRIMARY-01").Errors);
    }

    /// The pair's two files are mirror images, so running this over the other host's copy is a
    /// mistake somebody will make. The peer name it carries is this machine, and offering it as
    /// the default would leave a question Enter refuses for ever.
    [Fact]
    public void The_other_hosts_file_does_not_seed_a_peer_that_is_this_host()
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(
            Host,
            new ConfigurationDocument { Peer = new PeerDocument { Hostname = Machine } });

        interview = interview.Answer("dr");

        Assert.Equal("peer.hostname", interview.Question!.Key);
        Assert.Null(interview.Question.Default);
    }

    /// A key absent from the file stays absent. Writing `false` where there was nothing turns
    /// a default nobody chose into a decision somebody appears to have made.
    [Fact]
    public void A_setting_the_file_never_mentioned_is_not_invented()
    {
        Assert.DoesNotContain(
            "check_bitlocker_autounlock",
            ConfigurationTemplate.Render(Answer(FirstRun, seedWithoutStorage())),
            StringComparison.Ordinal);

        static ConfigurationDocument seedWithoutStorage() => new() { Storage = new StorageDocument() };
    }

    /// With a list on the screen, a name that matches nothing is a typo. Accepting it would
    /// write a VM that does not exist into the file, to be discovered on the day of a failover.
    [Theory]
    [InlineData(3, "vSwitch-LAM", "there is no switch vSwitch-LAM in the list.")]
    [InlineData(4, "VM-DC-1", "there is no VM VM-DC-1 in the list.")]
    public void A_name_that_matches_nothing_in_the_list_is_a_typo(
        int step, string typed, string expected)
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(Host);

        foreach (string answer in FirstRun.Take(step))
        {
            interview = interview.Answer(answer);
        }

        Assert.Equal(expected, interview.Answer(typed).Rejection);
    }

    [Theory]
    [InlineData(1, "HV-REPLICA-01", "that is this host. Name the other one.")]
    [InlineData(2, "192.0.2", "'192.0.2' is not an IP address written in full.")]
    [InlineData(2, "the-dr-box", "'the-dr-box' is not an IP address written in full.")]
    [InlineData(3, "9", "there is no switch 9 in the list.")]
    [InlineData(4, "7", "there is no VM 7 in the list.")]
    [InlineData(0, "maybe", "answer primary or dr.")]
    public void An_answer_that_would_not_survive_the_file_is_refused_with_its_reason(
        int step, string typed, string expected)
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(Host);

        foreach (string answer in FirstRun.Take(step))
        {
            interview = interview.Answer(answer);
        }

        string asked = interview.Question!.Key;
        ConfigurationInterview refused = interview.Answer(typed);

        Assert.Equal(expected, refused.Rejection);

        // Re-asked, not skipped and not silently corrected.
        Assert.Equal(asked, refused.Question!.Key);
    }

    /// The question and the screen above it have to agree. It used to offer `all` even with no
    /// list above it, and then refuse that exact answer.
    [Fact]
    public void With_no_list_to_pick_from_the_question_does_not_offer_all()
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(
            InterviewFacts.Unread(Machine));

        foreach (string answer in new[] { "primary", "HV-PRIMARY-01", "192.0.2.10", "vSwitch-LAN" })
        {
            interview = interview.Answer(answer);
        }

        InterviewQuestion asked = interview.Question!;

        Assert.Equal("vms", asked.Key);
        Assert.DoesNotContain("all", asked.Prompt, StringComparison.Ordinal);
        Assert.False(asked.Listed);
        Assert.Null(asked.Default);
    }

    /// And where there is a list, `all` is the default: on a host being set up, every VM it
    /// replicates is normally the answer.
    [Fact]
    public void With_a_list_all_is_offered_and_taken()
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(Host);

        foreach (string answer in new[] { "dr", "HV-PRIMARY-01", "192.0.2.10", "1" })
        {
            interview = interview.Answer(answer);
        }

        Assert.Equal("all", interview.Question!.Default);
        Assert.Null(interview.Answer("all").Rejection);
    }

    /// The lists are a convenience, not a dependency. A host whose Hyper-V cannot be read still
    /// has to be able to complete this — that is rule 5 applied to a setup command.
    [Fact]
    public void A_host_whose_hyper_v_cannot_be_read_is_completed_by_typing()
    {
        ConfigurationDraft draft = Answer(
            ["primary", "HV-PRIMARY-01", "192.0.2.10", "vSwitch-LAN", "VM-DC-01", "P1", "y", "y"],
            facts: InterviewFacts.Unread(Machine));

        Assert.Equal("vSwitch-LAN", draft.SwitchName);
        Assert.Equal("VM-DC-01", Assert.Single(draft.Vms).Name);
    }

    [Fact]
    public void All_takes_every_vm_the_host_reported() =>
        Assert.Equal(
            ["VM-DC-01", "VM-APP-01", "VM-BACKUP-01"],
            Answer(["dr", "HV-PRIMARY-01", "192.0.2.10", "1", "all",
                    "P1", "y", "P2", "n", "P2", "n", "y"])
                .Vms.Select(vm => vm.Name));

    /// Declining the grouped question unrolls it, and every one of the six is then bounded the
    /// way the validator bounds it.
    [Fact]
    public void Declining_the_six_figures_asks_for_each_of_them()
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(Host);

        foreach (string answer in FirstRun[..^1])
        {
            interview = interview.Answer(answer);
        }

        interview = interview.Answer("n");

        Assert.Equal("node.reserve", interview.Question!.Key);
        Assert.Equal("4", interview.Question.Default);

        Assert.Equal("0 is out of range for this.", interview.Answer("0").Rejection);

        ConfigurationDraft draft = Drain(interview, ["8", "60", "15", "5", "e", "500"]);

        Assert.Equal(8, draft.HostMemoryReserveGb);
        Assert.Equal(60, draft.PeerOfflineAfterSec);
        Assert.Equal(15, draft.FrequencySec);
        Assert.Equal(5, draft.LagMultiplier);
        Assert.Equal("E:", draft.DataVolume);
        Assert.Equal(500, draft.FreeSpaceWarningGb);
    }

    /// Accepting the six writes the figures that were on the screen. A grouped question that
    /// accepted something other than what it displayed would be the worst of both.
    [Fact]
    public void Accepting_the_six_writes_what_was_shown()
    {
        ConfigurationDraft draft = Answer(FirstRun);

        Assert.Equal(DraftDefaults.HostMemoryReserveGb, draft.HostMemoryReserveGb);
        Assert.Equal(DraftDefaults.PeerOfflineAfterSec, draft.PeerOfflineAfterSec);
        Assert.Equal(DraftDefaults.DataVolume, draft.DataVolume);
    }

    [Fact]
    public void The_switches_are_offered_with_what_they_reach() =>
        Assert.Equal(
            ["vSwitch-LAN  (external)", "vSwitch-Isolated  (private)"],
            Questions(FirstRun).Single(question => question.Key == "switch").Choices);

    [Fact]
    public void The_vms_are_offered_with_whether_they_replicate_and_run() =>
        Assert.Equal(
            [
                "VM-DC-01  (replicated, running)",
                "VM-APP-01  (replicated, running)",
                "VM-BACKUP-01  (not replicated, off)",
            ],
            Questions(FirstRun).Single(question => question.Key == "vms").Choices);

    /// `--role dr` is the one answer the command line can supply, so an install script does
    /// not have to ask the question it already knows the answer to.
    [Fact]
    public void A_role_given_on_the_command_line_is_not_asked_again() =>
        Assert.Equal(
            "peer.hostname",
            ConfigurationInterview.Start(Host, role: ExpectedRole.Replica).Question!.Key);

    private static ConfigurationDraft Answer(
        IReadOnlyList<string> typed,
        ConfigurationDocument? seed = null,
        InterviewFacts? facts = null)
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(facts ?? Host, seed);

        foreach (string answer in typed)
        {
            interview = interview.Answer(answer);
        }

        Assert.Null(interview.Question);
        return interview.Draft!;
    }

    private static ConfigurationDraft Drain(
        ConfigurationInterview interview, IReadOnlyList<string> typed)
    {
        foreach (string answer in typed)
        {
            interview = interview.Answer(answer);
        }

        return interview.Draft!;
    }

    /// Every question the interview asks along one path, for assertions about what is offered.
    private static List<InterviewQuestion> Questions(IReadOnlyList<string> typed)
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(Host);
        List<InterviewQuestion> asked = [];

        foreach (string answer in typed)
        {
            asked.Add(interview.Question!);
            interview = interview.Answer(answer);
        }

        return asked;
    }

    private static string WrittenTo(string yaml)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");
        File.WriteAllText(path, yaml);

        return path;
    }
}
