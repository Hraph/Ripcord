using Ripcord.Adapters.Yaml;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;
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
    /// VMs by number, their priorities, the six figures accepted as shown, and no update check.
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
        "n",
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

    /// The snapshot's default sits beside the file; `init` must not write one on another volume.
    [Fact]
    public void Init_never_writes_a_snapshot_path()
    {
        string yaml = ConfigurationTemplate.Render(Answer(FirstRun));

        Assert.DoesNotContain("snapshot_path", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("state.json", yaml, StringComparison.Ordinal);
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
              snapshot_path: "D:\\Ripcord\\state.json"

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
        Assert.Contains(@"  snapshot_path: ""D:\\Ripcord\\state.json""", rewritten, StringComparison.Ordinal);
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
                new InterviewFacts(
                    "HV-PRIMARY-01",
                    [],
                    [
                        new InterviewVm("VM-DC-01", true, true),
                        new InterviewVm("VM-LEGACY-01", true, true),
                        new InterviewVm("VM-BACKUP-01", false, true),
                    ])),
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

    /// Once `updates` is written for real, the sample's commented example of it would read as a
    /// second block. The examples around it are other sections' and stay.
    [Fact]
    public void A_re_run_over_the_sample_drops_its_commented_updates_example_only()
    {
        string sample = File.ReadAllText(
            Path.Combine(Architecture.RepositoryLayout.Root, "config", "ripcord.primary.yaml"));
        ConfigurationDocument? seed = new YamlConfigStore().Read(WrittenTo(sample)).Document;
        InterviewFacts facts = new(
            "HV-PRIMARY-01",
            [],
            [
                new InterviewVm("VM-DC-01", true, true),
                new InterviewVm("VM-LEGACY-01", true, true),
                new InterviewVm("VM-BACKUP-01", false, true),
            ]);

        string rewritten = ConfigurationTemplate.Render(
            Answer([.. Enumerable.Repeat("", 40)], seed, facts), sample);

        Assert.Contains("# updates:", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("# updates:", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("#   install: true", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("Two switches, because they buy different things", rewritten, StringComparison.Ordinal);
        Assert.Single(rewritten.Split('\n'), line => line == "updates:");

        foreach (string example in new[] { "# alerting:", "# dashboard:", "# diagnostics:" })
        {
            Assert.Contains(example, rewritten, StringComparison.Ordinal);
        }

        Assert.Contains(
            "#     url: \"https://hooks.example.net/ripcord\"\n\n# The read-only page",
            rewritten.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    /// The first rewrite of a sample may reshape it; every one after must give it back. Blank
    /// lines between carried sections used to grow by one per run.
    [Theory]
    [InlineData("ripcord.primary.yaml", "HV-PRIMARY-01")]
    [InlineData("ripcord.dr.yaml", "HV-REPLICA-01")]
    public void A_second_re_run_over_a_sample_changes_nothing(string file, string machine)
    {
        string sample = File.ReadAllText(
            Path.Combine(Architecture.RepositoryLayout.Root, "config", file));
        InterviewFacts facts = new(
            machine,
            [],
            [
                new InterviewVm("VM-DC-01", true, true),
                new InterviewVm("VM-LEGACY-01", true, true),
                new InterviewVm("VM-BACKUP-01", false, true),
            ]);

        string once = Rerun(sample);

        Assert.Equal(once, Rerun(once));

        string Rerun(string previous) =>
            ConfigurationTemplate.Render(
                Answer(
                    [.. Enumerable.Repeat("", 40)],
                    new YamlConfigStore().Read(WrittenTo(previous)).Document,
                    facts),
                previous);
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
            ["primary", "HV-PRIMARY-01", "192.0.2.10", "vSwitch-LAN", "VM-DC-01", "P1", "y", "y", "n"],
            facts: InterviewFacts.Unread(Machine));

        Assert.Equal("vSwitch-LAN", draft.SwitchName);
        Assert.Equal("VM-DC-01", Assert.Single(draft.Vms).Name);
    }

    [Fact]
    public void All_takes_every_vm_the_host_reported() =>
        Assert.Equal(
            ["VM-DC-01", "VM-APP-01", "VM-BACKUP-01"],
            Answer(["dr", "HV-PRIMARY-01", "192.0.2.10", "1", "all",
                    "P1", "y", "P2", "n", "P2", "n", "y", "n"])
                .Vms.Select(vm => vm.Name));

    /// Declining the grouped question unrolls it, and every one of the six is then bounded the
    /// way the validator bounds it.
    [Fact]
    public void Declining_the_six_figures_asks_for_each_of_them()
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(Host);

        foreach (string answer in FirstRun[..^2])
        {
            interview = interview.Answer(answer);
        }

        interview = interview.Answer("n");

        Assert.Equal("node.reserve", interview.Question!.Key);
        Assert.Equal("4", interview.Question.Default);

        Assert.Equal("0 is out of range for this.", interview.Answer("0").Rejection);

        ConfigurationDraft draft = Drain(interview, ["8", "60", "15", "5", "e", "500", "n"]);

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

    /// Decision 3: the list is what Hyper-V reports, never what the previous file said.
    [Fact]
    public void A_vm_in_the_file_but_not_on_the_host_is_neither_offered_nor_kept()
    {
        ConfigurationDocument seed = SeedWith("VM-DC-01", "VM-GONE-01");
        InterviewFacts facts = new(
            Machine,
            [new HostSwitch("vSwitch-LAN", SwitchConnectivity.External)],
            [new InterviewVm("VM-DC-01", true, true), new InterviewVm("VM-APP-01", true, true)]);

        InterviewQuestion vms = QuestionOf("vms", facts, seed);

        Assert.DoesNotContain(vms.Choices, choice => choice.Contains("VM-GONE-01", StringComparison.Ordinal));
        Assert.Equal(
            ["No longer on this host, so dropped from the file:", "  - VM-GONE-01",
             "Numbers from the list or names, separated by commas, or 'all'."],
            vms.Explanation);
        Assert.Equal("1", vms.Default);

        ConfigurationDraft draft = Answer(
            ["primary", "HV-PRIMARY-01", "192.0.2.10", "1", "", "P1", "y", "y", "n"], seed, facts);

        Assert.Equal("VM-DC-01", Assert.Single(draft.Vms).Name);
    }

    /// The field report: `install.ps1` left the sample on the host, and init offered its
    /// placeholders beside the real VMs and kept them on Enter.
    [Fact]
    public void The_shipped_samples_vms_never_reach_a_host_that_does_not_have_them()
    {
        string sample = File.ReadAllText(
            Path.Combine(Architecture.RepositoryLayout.Root, "config", "ripcord.primary.yaml"));
        ConfigurationDocument? seed = new YamlConfigStore().Read(WrittenTo(sample)).Document;
        InterviewFacts facts = new(
            "HV-PRIMARY-01",
            [],
            [new InterviewVm("SR-DC", true, true), new InterviewVm("SR-APP", true, true)]);

        InterviewQuestion vms = QuestionOf("vms", facts, seed);
        string rewritten = ConfigurationTemplate.Render(
            Answer(["", "", "", "", "", "P1", "", "P2", "", "", ""], seed, facts), sample);

        Assert.Equal("all", vms.Default);
        Assert.Equal(["SR-DC  (replicated, running)", "SR-APP  (replicated, running)"], vms.Choices);

        // Comments aside, only the carried `checks` block may still name one, which is what
        // the warning before writing is for.
        string owned = string.Join(
            "\n",
            rewritten[..rewritten.IndexOf("\nchecks:", StringComparison.Ordinal)]
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith('#')));

        foreach (string placeholder in new[] { "VM-DC-01", "VM-LEGACY-01", "VM-BACKUP-01" })
        {
            Assert.DoesNotContain(placeholder, owned, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_host_with_no_vm_writes_an_empty_list_and_asks_nothing_per_vm()
    {
        InterviewFacts facts = new(Machine, [new HostSwitch("vSwitch-LAN", SwitchConnectivity.External)], []);
        ConfigurationInterview interview = ConfigurationInterview.Start(facts);
        List<InterviewQuestion> asked = [];

        foreach (string answer in new[] { "primary", "HV-PRIMARY-01", "192.0.2.10", "1", "", "y", "n" })
        {
            asked.Add(interview.Question!);
            interview = interview.Answer(answer);
        }

        Assert.Null(interview.Question);
        Assert.DoesNotContain(asked, question => question.Key.Contains(':', StringComparison.Ordinal));

        InterviewQuestion vms = asked.Single(question => question.Key == "vms");
        Assert.Equal("none", vms.Default);
        Assert.False(vms.Listed);
        Assert.Contains("This host has no virtual machine, so the file declares none.", vms.Explanation);

        string yaml = ConfigurationTemplate.Render(interview.Draft!);
        Assert.Contains("vms: []", yaml, StringComparison.Ordinal);

        ConfigurationValidation validation = ConfigurationValidator.Validate(
            new YamlConfigStore().Read(WrittenTo(yaml)).Document, Machine);
        Assert.Empty(validation.Errors);
        Assert.Empty(validation.Configuration!.Vms);
    }

    [Fact]
    public void With_no_vm_on_the_host_a_typed_name_is_refused()
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(new InterviewFacts(Machine, [], []));

        foreach (string answer in new[] { "primary", "HV-PRIMARY-01", "192.0.2.10", "vSwitch-LAN" })
        {
            interview = interview.Answer(answer);
        }

        Assert.Equal("this host has no VM to choose.", interview.Answer("VM-X").Rejection);
        Assert.Equal("this host has no VM to choose.", interview.Answer("all").Rejection);
    }

    /// Read, but no switch: that is not "could not be read".
    [Fact]
    public void A_host_with_no_switch_is_not_called_unreadable() =>
        Assert.Equal(
            ["This host has no virtual switch, so type the name."],
            QuestionOf("switch", new InterviewFacts(Machine, [], [])).Explanation);

    [Fact]
    public void A_host_whose_hyper_v_cannot_be_read_offers_no_name_from_the_file()
    {
        InterviewQuestion vms = QuestionOf(
            "vms", InterviewFacts.Unread(Machine), SeedWith("VM-DC-01", "VM-BACKUP-01"));

        Assert.Null(vms.Default);
        Assert.False(vms.Listed);
        Assert.Empty(vms.Choices);
        Assert.DoesNotContain(vms.Explanation, line => line.Contains("VM-", StringComparison.Ordinal));
    }

    /// Typing a name again still brings back what the file said about it.
    [Fact]
    public void A_name_typed_again_on_an_unread_host_keeps_what_the_file_said_about_it()
    {
        string sample = File.ReadAllText(
            Path.Combine(Architecture.RepositoryLayout.Root, "config", "ripcord.primary.yaml"));
        ConfigurationDocument? seed = new YamlConfigStore().Read(WrittenTo(sample)).Document;

        ConfigurationDraft draft = Answer(
            ["", "", "", "", "VM-BACKUP-01", "", "", "", ""], seed, InterviewFacts.Unread("HV-PRIMARY-01"));

        DraftVm vm = Assert.Single(draft.Vms);
        Assert.Equal("manual", vm.Failover);
        Assert.True(vm.HasPassthroughDisk);
    }

    [Fact]
    public void Dropping_a_vm_removes_it_from_the_unattended_authorisation()
    {
        ConfigurationDocument seed = SeedWith("VM-DC-01", "VM-GONE-01");
        seed.Replication = new ReplicationDocument
        {
            UnattendedTestFailoverVms = ["VM-GONE-01", "VM-DC-01"],
        };

        ConfigurationDraft draft = Answer(
            ["primary", "HV-PRIMARY-01", "192.0.2.10", "1", "", "P1", "y", "y", "n"], seed);

        Assert.Equal(["VM-DC-01"], draft.Carried.UnattendedTestFailoverVms);
        Assert.Empty(
            ConfigurationValidator.Validate(
                new YamlConfigStore().Read(WrittenTo(ConfigurationTemplate.Render(draft))).Document,
                Machine).Errors);
    }

    [Fact]
    public void An_acknowledgement_naming_a_dropped_vm_is_reported()
    {
        ConfigurationDocument seed = SeedWith("VM-DC-01", "VM-GONE-01");
        seed.Checks = new ChecksDocument
        {
            Acknowledgements =
            [
                new AcknowledgementDocument { Rule = "passthrough-disk-on-replicated-vm", Vm = "VM-DC-01" },
                new AcknowledgementDocument { Rule = "passthrough-disk-on-replicated-vm", Vm = "VM-GONE-01" },
            ],
        };

        ConfigurationInterview interview = ConfigurationInterview.Start(Host, seed);

        foreach (string answer in new[] { "primary", "HV-PRIMARY-01", "192.0.2.10", "1", "", "P1", "y", "y", "n" })
        {
            interview = interview.Answer(answer);
        }

        Assert.Equal(
            ["checks.acknowledgements[1]: VM-GONE-01 is no longer declared"],
            interview.StaleAcknowledgements);
    }

    [Fact]
    public void Test_failover_copies_are_not_offered()
    {
        HostState state = new(
            Machine,
            [Vm("VM-DC-01", ReplicationRole.Replica), Vm("VM-DC-01 - Test", ReplicationRole.TestReplica)],
            HostReachability.Reachable());

        Assert.Equal(
            ["VM-DC-01"],
            InterviewFacts.From(Machine, [], state).Vms.Select(vm => vm.Name));
    }

    [Fact]
    public void A_name_hyper_v_reports_twice_is_offered_once()
    {
        HostState state = new(
            Machine,
            [Vm("VM-DC-01", ReplicationRole.Replica), Vm("vm-dc-01", ReplicationRole.None)],
            HostReachability.Reachable());

        Assert.Equal(
            ["VM-DC-01"],
            InterviewFacts.From(Machine, [], state).Vms.Select(vm => vm.Name));
    }

    [Fact]
    public void An_unreachable_host_state_is_read_as_unread() =>
        Assert.False(
            InterviewFacts.From(
                Machine, [], HostState.Unreachable(Machine, HostReachability.NotConfigured())).Read);

    /// Decision 4: the default is what the prompt asks for — numbers — and Enter on it keeps
    /// the file's order, which is the start order within a priority.
    [Fact]
    public void Enter_on_a_re_run_keeps_the_files_vm_order_in_the_draft()
    {
        ConfigurationDocument seed = SeedWith("VM-BACKUP-01", "VM-DC-01");

        Assert.Equal("3,1", QuestionOf("vms", Host, seed).Default);

        ConfigurationDraft draft = Answer(
            ["primary", "HV-PRIMARY-01", "192.0.2.10", "1", .. Enumerable.Repeat("", 7)], seed);

        Assert.Equal(["VM-BACKUP-01", "VM-DC-01"], draft.Vms.Select(vm => vm.Name));
    }

    [Fact]
    public void A_seed_vm_absent_from_the_host_does_not_make_enter_refuse()
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(
            Host, SeedWith("VM-GONE-01", "VM-APP-01"));

        foreach (string answer in new[] { "primary", "HV-PRIMARY-01", "192.0.2.10", "1" })
        {
            interview = interview.Answer(answer);
        }

        Assert.Equal("2", interview.Question!.Default);

        interview = interview.Answer("");

        Assert.Null(interview.Rejection);
        Assert.Equal("priority:VM-APP-01", interview.Question!.Key);
    }

    /// Stored one per line, so a comma inside a Hyper-V name survives being picked.
    [Fact]
    public void A_vm_name_with_a_comma_is_chosen_by_its_number()
    {
        InterviewFacts facts = new(
            Machine,
            [new HostSwitch("vSwitch-LAN", SwitchConnectivity.External)],
            [new InterviewVm("SQL, reporting", true, true), new InterviewVm("VM-DC-01", true, true)]);

        ConfigurationDraft draft = Answer(
            ["primary", "HV-PRIMARY-01", "192.0.2.10", "1", "1", "P1", "n", "y", "n"], facts: facts);

        Assert.Equal("SQL, reporting", Assert.Single(draft.Vms).Name);
    }

    /// Off unless answered: these hosts are meant to have no outbound access.
    [Fact]
    public void The_update_check_is_asked_last_and_defaults_to_off()
    {
        InterviewQuestion asked = Questions(FirstRun)[^1];

        Assert.Equal("updates.check", asked.Key);
        Assert.Equal("n", asked.Default);
        Assert.False(Answer(FirstRun).CheckUpdates);
        Assert.Contains("  check: false", ConfigurationTemplate.Render(Answer(FirstRun)), StringComparison.Ordinal);
    }

    [Fact]
    public void Checking_for_updates_is_written_validated_and_offered_back_on_a_re_run()
    {
        string yaml = ConfigurationTemplate.Render(Answer([.. FirstRun[..^1], "y"]));
        ConfigurationDocument? seed = new YamlConfigStore().Read(WrittenTo(yaml)).Document;

        ConfigurationValidation validation = ConfigurationValidator.Validate(seed, Machine);
        Assert.Empty(validation.Errors);
        Assert.True(validation.Configuration!.Updates.Check);
        Assert.False(validation.Configuration.Updates.Install);

        Assert.Equal("y", UpdateCheckQuestion(seed).Default);
    }

    /// Install is never asked, so a re-run keeps it — unless the check it depends on is turned
    /// off, which the validator would otherwise refuse.
    [Theory]
    [InlineData("y", true)]
    [InlineData("n", false)]
    public void Install_is_carried_only_while_the_check_stays_on(string check, bool kept)
    {
        ConfigurationDocument seed = SeedWith("VM-DC-01");
        seed.Updates = new UpdatesDocument { Check = true, Install = true };

        Assert.Contains(
            "'n' also turns off updates.install, which cannot be on without it.",
            UpdateCheckQuestion(seed).Explanation);

        string yaml = ConfigurationTemplate.Render(
            Answer(["primary", "HV-PRIMARY-01", "192.0.2.10", "1", "", "P1", "y", "y", check], seed));

        Assert.Equal(kept, yaml.Contains("  install: true", StringComparison.Ordinal));
        Assert.Empty(
            ConfigurationValidator.Validate(new YamlConfigStore().Read(WrittenTo(yaml)).Document, Machine).Errors);
    }

    /// A hand-edited file the validator refuses: Enter answers 'n', and the result validates.
    [Fact]
    public void Install_without_the_check_is_dropped_on_enter()
    {
        ConfigurationDocument seed = SeedWith("VM-DC-01");
        seed.Updates = new UpdatesDocument { Check = false, Install = true };

        string yaml = ConfigurationTemplate.Render(
            Answer(["primary", "HV-PRIMARY-01", "192.0.2.10", "1", "", "P1", "y", "y", ""], seed));

        Assert.DoesNotContain("install:", yaml, StringComparison.Ordinal);
        Assert.Empty(
            ConfigurationValidator.Validate(new YamlConfigStore().Read(WrittenTo(yaml)).Document, Machine).Errors);
    }

    [Fact]
    public void Enter_through_a_re_run_keeps_both_update_switches_and_their_trailer()
    {
        ConfigurationDocument seed = SeedWith("VM-DC-01");
        seed.Updates = new UpdatesDocument { Check = true, Install = true };

        string original = ConfigurationTemplate.Render(
            Answer(["primary", "HV-PRIMARY-01", "192.0.2.10", "1", "", "P1", "y", "y", ""], seed))
            + "  # install: false  # until the next change window\n";
        ConfigurationDocument? again = new YamlConfigStore().Read(WrittenTo(original)).Document;

        Assert.Equal(
            original,
            ConfigurationTemplate.Render(Answer([.. Enumerable.Repeat("", 20)], again), original));
    }

    private static InterviewQuestion UpdateCheckQuestion(ConfigurationDocument? seed)
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(Host, seed);

        foreach (string answer in new[] { "primary", "HV-PRIMARY-01", "192.0.2.10", "1" })
        {
            interview = interview.Answer(answer);
        }

        while (interview.Question!.Key != "updates.check")
        {
            interview = interview.Answer("");
        }

        return interview.Question;
    }

    private static VmReplicationState Vm(string name, ReplicationRole role) =>
        new(name, role, ReplicationState.Replicating, ReplicationHealth.Normal, null, null);

    private static ConfigurationDocument SeedWith(params string[] names) =>
        new()
        {
            Vms = [.. names.Select(name => new VmDocument { Name = name, Priority = "P1" })],
        };

    /// The question with this key, reached by pressing Enter or giving the first-run answers.
    private static InterviewQuestion QuestionOf(
        string key, InterviewFacts facts, ConfigurationDocument? seed = null)
    {
        ConfigurationInterview interview = ConfigurationInterview.Start(facts, seed);
        string[] typed = ["primary", "HV-OTHER-01", "192.0.2.10", "vSwitch-LAN"];

        for (int index = 0; interview.Question!.Key != key; index++)
        {
            interview = interview.Answer(typed[index]);
        }

        return interview.Question;
    }

    /// The old text claimed a P1 on both hosts halts mutation; split brain halts it for any VM.
    [Fact]
    public void The_first_priority_question_says_what_P1_changes()
    {
        string text = string.Join(
            ' ', Questions(FirstRun).First(question => question.Key.StartsWith(
                "priority:", StringComparison.Ordinal)).Explanation);

        Assert.Contains("first", text, StringComparison.Ordinal);
        Assert.Contains("'check'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("halts", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_priority_explanation_is_given_once() =>
        Assert.Empty(Questions(FirstRun).Single(
            question => question.Key == "priority:VM-APP-01").Explanation);

    [Fact]
    public void The_domain_controller_question_explains_itself_once()
    {
        List<InterviewQuestion> asked = Questions(FirstRun);
        string first = string.Join(
            ' ', asked.Single(question => question.Key == "dc:VM-DC-01").Explanation);

        Assert.Contains("USN", first, StringComparison.Ordinal);
        Assert.Contains("P1", first, StringComparison.Ordinal);
        Assert.Empty(asked.Single(question => question.Key == "dc:VM-APP-01").Explanation);
    }

    [Fact]
    public void The_template_does_not_claim_priority_gates_mutation()
    {
        string yaml = ConfigurationTemplate.Render(Answer(FirstRun));

        Assert.DoesNotContain("halts every mutating command", yaml, StringComparison.Ordinal);
        Assert.Contains("fails over first", yaml, StringComparison.Ordinal);
    }

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
