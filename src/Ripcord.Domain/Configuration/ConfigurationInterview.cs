using System.Globalization;
using System.Net;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Configuration;

/// What `ripcord init` can see of the host before there is a configuration to read.
///
/// `Read` tells a host with no VM apart from a host whose Hyper-V could not be read: both have
/// an empty list, but only the second one is completed by typing names.
public sealed record InterviewFacts(
    string MachineName,
    IReadOnlyList<HostSwitch> Switches,
    IReadOnlyList<InterviewVm> Vms,
    bool Read = true)
{
    public static InterviewFacts Unread(string machineName) => new(machineName, [], [], false);

    /// Only what is really on this host. A test-failover copy is Hyper-V's own temporary VM,
    /// never one to declare, and a name reported twice is offered once so `all` cannot write
    /// the same VM twice.
    public static InterviewFacts From(
        string machineName, IReadOnlyList<HostSwitch> switches, HostState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.IsReachable)
        {
            return Unread(machineName);
        }

        List<InterviewVm> vms = [];

        foreach (VmReplicationState vm in state.Vms)
        {
            if (vm.Role == ReplicationRole.TestReplica
                || vms.Exists(known => string.Equals(known.Name, vm.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            vms.Add(new InterviewVm(
                vm.Name,
                vm.Role != ReplicationRole.None,
                vm.PowerState == VmPowerState.Running));
        }

        return new InterviewFacts(machineName, switches, vms);
    }
}

/// A VM as the interview offers it: the name, and the two facts that help somebody recognise
/// which of twelve machines this is.
public sealed record InterviewVm(string Name, bool Replicated, bool Running)
{
    public string Described =>
        $"{this.Name}  ({(this.Replicated ? "replicated" : "not replicated")}, "
        + $"{(this.Running ? "running" : "off")})";
}

/// One question, with everything needed to put it on a console and nothing about how.
///
/// `Group` is what the interview is asking about at that moment, so the console can head a run
/// of questions once instead of repeating the context in every prompt. `Listed` separates the
/// two kinds of choice: a handful of fixed words belongs beside the prompt, while a list read
/// off this host is numbered because the answer is the number.
public sealed record InterviewQuestion(
    string Key,
    string Prompt,
    string? Default,
    IReadOnlyList<string> Choices,
    IReadOnlyList<string> Explanation,
    string Group = "",
    bool Listed = false)
{
    public static InterviewQuestion Of(
        string key, string prompt, string? answer = null, string group = "") =>
        new(key, prompt, answer, [], [], group);

    public bool Equals(InterviewQuestion? other) =>
        other is not null
        && this.Key == other.Key
        && this.Prompt == other.Prompt
        && this.Default == other.Default
        && this.Group == other.Group
        && this.Listed == other.Listed
        && Structural.Same(this.Choices, other.Choices)
        && Structural.Same(this.Explanation, other.Explanation);

    public override int GetHashCode() => HashCode.Combine(this.Key, this.Prompt, this.Default);
}

/// The interview `ripcord init` runs, as a state machine with no console in it.
///
/// Every default, every bound and every refusal is here rather than in the CLI, so the whole
/// thing can be driven from a list of typed strings in a test — including the one assertion
/// that matters, which is that what comes out passes `ConfigurationValidator`.
///
/// Answers are kept as the text the operator typed, canonicalised on the way in. Re-running
/// seeds them from the file already on the host, which is what makes a second run an edit
/// rather than a restart.
public sealed class ConfigurationInterview
{
    private const string None = "none";

    /// What the interview is asking about. A run of questions is headed once rather than each
    /// prompt carrying its own context, which is what made twelve VMs read as one long wall.
    public static class Groups
    {
        public const string ThisHost = "THIS HOST";

        public const string OtherHost = "THE OTHER HOST";

        public const string Replication = "REPLICATION";

        public const string Vms = "THE VIRTUAL MACHINES";

        public const string Priorities = "FAILOVER ORDER";

        public const string Thresholds = "THRESHOLDS";
    }

    private readonly InterviewFacts facts;
    private readonly ConfigurationDocument? seed;
    private readonly Dictionary<string, string> answers;

    private ConfigurationInterview(
        InterviewFacts facts,
        ConfigurationDocument? seed,
        Dictionary<string, string> answers,
        string? rejection)
    {
        this.facts = facts;
        this.seed = seed;
        this.answers = answers;
        this.Rejection = rejection;
    }

    /// Why the last answer was not taken. The console re-asks the same question with this
    /// above it; nothing is ever silently corrected.
    public string? Rejection { get; }

    /// The next thing to ask, or null when there is nothing left.
    public InterviewQuestion? Question => this.Plan().Steps.FirstOrDefault(this.Unanswered);

    public ConfigurationDraft? Draft => this.Plan() is { Complete: true } plan ? this.Build(plan) : null;

    /// `role` is pre-answered when it came from the command line, which is the one answer a
    /// script can supply without turning the interview into something a script can drive.
    public static ConfigurationInterview Start(
        InterviewFacts facts, ConfigurationDocument? seed = null, ExpectedRole? role = null)
    {
        ArgumentNullException.ThrowIfNull(facts);

        Dictionary<string, string> answers = new(StringComparer.Ordinal);

        if (role is { } chosen)
        {
            answers["role"] = chosen == ExpectedRole.Replica ? "dr" : "primary";
        }

        return new ConfigurationInterview(facts, seed, answers, null);
    }

    public ConfigurationInterview Answer(string? typed)
    {
        if (this.Question is not { } question)
        {
            return this;
        }

        string given = (typed ?? "").Trim();

        if (given.Length == 0 && question.Default is { } fallback)
        {
            given = fallback;
        }

        if (this.Accept(question, given) is { } refusal)
        {
            return new ConfigurationInterview(this.facts, this.seed, this.answers, refusal);
        }

        return new ConfigurationInterview(this.facts, this.seed, this.answers, null);
    }

    private bool Unanswered(InterviewQuestion question) =>
        !this.answers.ContainsKey(question.Key);

    /// The steps as they stand. Later ones depend on earlier answers — which VMs were picked
    /// decides how many priority questions there are — so the list is rebuilt each time rather
    /// than held.
    private InterviewPlan Plan()
    {
        List<InterviewQuestion> steps =
        [
            new InterviewQuestion(
                "role",
                "Is this the primary or the DR host?",
                this.SeededRole(),
                ["primary", "dr"],
                ["The pair's two files are mirror images. Nothing observable says which way",
                 "round the replication is meant to run, so this is the one thing only you",
                 "can say."],
                Groups.ThisHost),
        ];

        if (this.Missing(steps, out InterviewPlan incomplete))
        {
            return incomplete;
        }

        steps.Add(InterviewQuestion.Of(
            "peer.hostname", "Its name", this.SeededPeer(), Groups.OtherHost));

        steps.Add(new InterviewQuestion(
            "peer.address",
            "Its IP address",
            this.seed?.Peer?.Address,
            [],
            ["Written in full - 192.0.2.10, not a name and not 192.0.2: it is compared",
             "against where a connection came from, and it lands in a firewall rule."],
            Groups.OtherHost));

        steps.Add(new InterviewQuestion(
            "switch",
            "Which switch are the replicas attached to on the failover target?",
            this.seed?.Replication?.ExpectedSwitchName,
            [.. this.facts.Switches.Select(Described)],
            this.facts.Switches.Count > 0
                ? []
                : [this.facts.Read
                    ? "This host has no virtual switch, so type the name."
                    : "This host's switches could not be read, so type the name."],
            Groups.Replication,
            this.facts.Switches.Count > 0));

        steps.Add(this.VmsQuestion());

        if (this.Missing(steps, out incomplete))
        {
            return incomplete;
        }

        // Nothing to ask per VM on a host that has none.
        IReadOnlyList<string> chosen = this.ChosenVms();
        int width = chosen.Select(name => name.Length).DefaultIfEmpty(0).Max();

        foreach ((string name, int index) in chosen.Select((name, index) => (name, index)))
        {
            // The explanation belongs to the group, not to every VM in it: repeated twelve
            // times it stops being read, which is the same as not being there.
            steps.Add(new InterviewQuestion(
                $"priority:{name}",
                $"{name.PadRight(width)}  priority",
                this.SeededPriority(name),
                ["P1", "P2"],
                index > 0
                    ? []
                    : ["P1 comes back first in a sweep, and a P1 running on both hosts at",
                       "once halts every mutating command. P2 is everything that can wait."],
                Groups.Priorities));

            steps.Add(new InterviewQuestion(
                $"dc:{name}",
                $"{name.PadRight(width)}  domain controller",
                this.SeededDc(name),
                ["y", "n"],
                [],
                Groups.Priorities));
        }

        steps.Add(new InterviewQuestion(
            "defaults",
            "Take all six",
            "y",
            ["y", "n"],
            [.. this.SixLines()],
            Groups.Thresholds));

        if (this.Missing(steps, out incomplete))
        {
            return incomplete;
        }

        if (!Yes(this.answers["defaults"]))
        {
            steps.AddRange(this.SixQuestions());
        }

        return new InterviewPlan(steps, steps.TrueForAll(step => !this.Unanswered(step)));
    }

    private bool Missing(List<InterviewQuestion> steps, out InterviewPlan plan)
    {
        plan = new InterviewPlan(steps, false);

        return steps.Exists(this.Unanswered);
    }

    private static string Described(HostSwitch value) =>
        value.Connectivity == SwitchConnectivity.External
            ? $"{value.Name}  (external)"
            : $"{value.Name}  ({value.Connectivity.ToString().ToLowerInvariant()})";

    /// Validates one answer and stores it canonicalised, or returns the sentence to re-ask
    /// with. Nothing is ever accepted in a form the file would not take.
    private string? Accept(InterviewQuestion question, string given)
    {
        if (given.Length == 0)
        {
            return "an answer is needed here.";
        }

        if (question.Key.StartsWith("priority:", StringComparison.Ordinal))
        {
            return this.Store(question, Priority(given), "answer P1 or P2.");
        }

        if (question.Key.StartsWith("dc:", StringComparison.Ordinal))
        {
            return this.Store(question, YesNo(given), "answer yes or no.");
        }

        return question.Key switch
        {
            "role" => this.Store(question, Role(given), "answer primary or dr."),
            "peer.hostname" => this.PeerHostname(question, given),
            "peer.address" => this.Store(question, Address(given), $"'{given}' is not an IP address written in full."),
            "switch" => this.Store(question, this.Switch(given), $"there is no switch {given} in the list."),
            "vms" => this.Vms(question, given),
            "defaults" => this.Store(question, YesNo(given), "answer yes or no."),
            _ => this.Store(question, Bounded(given, question.Key), $"{given} is out of range for this."),
        };
    }

    private string? Store(InterviewQuestion question, string? value, string refusal)
    {
        if (value is null)
        {
            return refusal;
        }

        this.answers[question.Key] = value;
        return null;
    }

    private string? PeerHostname(InterviewQuestion question, string given) =>
        string.Equals(given, this.facts.MachineName, StringComparison.OrdinalIgnoreCase)
            ? "that is this host. Name the other one."
            : this.Store(question, given, "");

    private static string? Role(string given) =>
        given.ToLowerInvariant() switch
        {
            "primary" or "p" or "1" => "primary",
            "dr" or "d" or "replica" or "2" => "dr",
            _ => null,
        };

    private static string? Priority(string given) =>
        given.ToUpperInvariant() switch
        {
            "P1" or "1" => "P1",
            "P2" or "2" => "P2",
            _ => null,
        };

    private static string? YesNo(string given) =>
        given.ToLowerInvariant() switch
        {
            "y" or "yes" or "true" => "y",
            "n" or "no" or "false" => "n",
            _ => null,
        };

    private static bool Yes(string stored) => stored == "y";

    /// The same rule the validator applies, so an address accepted here cannot be refused by
    /// the file it is written into.
    private static string? Address(string given) =>
        IPAddress.TryParse(given, out IPAddress? parsed)
        && string.Equals(parsed.ToString(), given, StringComparison.OrdinalIgnoreCase)
            ? given
            : null;

    /// A number from the list, or a name typed out. Both, because a host whose switches could
    /// not be read has no numbers to choose from.
    private string? Switch(string given) =>
        Chosen(given, [.. this.facts.Switches.Select(value => value.Name)]);

    /// A name that matches nothing is only accepted when there was no list to match against.
    /// With a list on the screen, an unmatched name is a typo — and a typo accepted here is a
    /// switch or a VM that does not exist, written into the file and found on the day of a
    /// failover rather than now.
    private static string? Chosen(string given, IReadOnlyList<string> names)
    {
        if (int.TryParse(given, CultureInfo.InvariantCulture, out int number))
        {
            return number >= 1 && number <= names.Count ? names[number - 1] : null;
        }

        if (names.FirstOrDefault(
            name => string.Equals(name, given, StringComparison.OrdinalIgnoreCase)) is { } matched)
        {
            return matched;
        }

        return names.Count == 0 ? given : null;
    }

    /// Names are stored one per line, not comma-joined: a Hyper-V name may contain a comma.
    private string? Vms(InterviewQuestion question, string given)
    {
        if (this.HasNoVm)
        {
            return string.Equals(given, None, StringComparison.OrdinalIgnoreCase)
                ? this.Store(question, None, "")
                : "this host has no VM to choose.";
        }

        VmChoice choice = VmSelection.Parse(given, this.OfferedNames);

        return choice.Refusal ?? this.Store(question, string.Join('\n', choice.Names), "");
    }

    private string[] OfferedNames => [.. this.facts.Vms.Select(vm => vm.Name)];

    /// The six that have sensible figures. Bounds match the validator's, so a number taken
    /// here cannot be refused by the file.
    private static string? Bounded(string given, string key)
    {
        // The volume first, whatever it looks like: a bare `4` is a drive letter typed wrong,
        // and answering "out of range" to it sends somebody looking for a bound.
        if (key == "storage.data_volume")
        {
            return Volume(given);
        }

        if (!int.TryParse(given, CultureInfo.InvariantCulture, out int value))
        {
            return null;
        }

        return key switch
        {
            "node.reserve" => Within(value, 1, 512),
            "peer.offline" => Within(value, 1, 86_400),
            "replication.frequency" => Within(value, 1, 86_400),
            "replication.lag" => Within(value, 1, 1_000),
            "storage.free_space" => Within(value, 1, 1_000_000),
            _ => null,
        };
    }

    private static string? Within(int value, int least, int most) =>
        value >= least && value <= most ? value.ToString(CultureInfo.InvariantCulture) : null;

    /// `D`, `d:` and `D:\` all mean the same volume. Normalised here so the validator's own
    /// rule — a single drive letter and a colon — is met whatever was typed.
    private static string? Volume(string given)
    {
        string trimmed = given.Trim().TrimEnd('\\', '/');

        return trimmed.Length is 1 or 2 && char.IsLetter(trimmed[0])
            ? $"{char.ToUpperInvariant(trimmed[0])}:"
            : null;
    }

    /// Read, and nothing there. Distinct from unread, where the names are typed instead.
    private bool HasNoVm => this.facts is { Read: true, Vms.Count: 0 };

    /// Three shapes, because what is on the screen has to match what is accepted: a list read
    /// off the host, a host read with no VM on it, or a host whose Hyper-V could not be read.
    /// The file's own VM names are never offered: they may be the shipped sample's
    /// placeholders, and a name offered here is a name written into the file.
    private InterviewQuestion VmsQuestion()
    {
        if (!this.facts.Read)
        {
            return new InterviewQuestion(
                "vms",
                "Name the ones that matter, separated by commas",
                null,
                [],
                ["This host's VMs could not be read, so type their names.",
                 "What the file said about a VM is kept for any name typed again."],
                Groups.Vms);
        }

        List<string> dropped = [.. this.DroppedVms().Select(name => $"  - {name}")];

        if (dropped.Count > 0)
        {
            dropped.Insert(0, "No longer on this host, so dropped from the file:");
        }

        if (this.HasNoVm)
        {
            return new InterviewQuestion(
                "vms",
                "Continue with none",
                None,
                [],
                [.. dropped,
                 "This host has no virtual machine, so the file declares none.",
                 "Run ripcord init again once they exist."],
                Groups.Vms);
        }

        return new InterviewQuestion(
            "vms",
            "Which ones matter?",
            VmSelection.Default(this.OfferedNames, [.. this.SeededNames()]),
            [.. this.facts.Vms.Select(vm => vm.Described)],
            [.. dropped, "Numbers from the list or names, separated by commas, or 'all'."],
            Groups.Vms,
            true);
    }

    /// The file's VMs this host does not have. Nothing counts as dropped on an unread host:
    /// there is no list to say a name is missing from.
    private IEnumerable<string> DroppedVms() =>
        this.facts.Read
            ? this.SeededNames().Where(name => this.OnHost(name) is null)
            : [];

    private IEnumerable<string> SeededNames() =>
        this.seed?.Vms?.Select(vm => vm.Name?.Trim()).OfType<string>().Where(name => name.Length > 0)
        ?? [];

    private string? OnHost(string name) =>
        this.facts.Vms.FirstOrDefault(
            vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase))?.Name;

    private string[] ChosenVms()
    {
        if (this.HasNoVm)
        {
            return [];
        }

        return this.answers["vms"].Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// The `checks.acknowledgements` entries naming a VM the new file no longer declares.
    /// That section is carried as the original lines, so they cannot be dropped here, and the
    /// validator refuses a file that keeps them: the operator has to be told before saying yes.
    public IReadOnlyList<string> StaleAcknowledgements
    {
        get
        {
            if (this.Draft is not { } draft
                || this.seed?.Checks?.Acknowledgements is not { } entries)
            {
                return [];
            }

            List<string> stale = [];

            for (int index = 0; index < entries.Count; index++)
            {
                if (entries[index]?.Vm?.Trim() is { Length: > 0 } vm
                    && !draft.Vms.Any(kept => string.Equals(kept.Name, vm, StringComparison.OrdinalIgnoreCase)))
                {
                    stale.Add($"checks.acknowledgements[{index}]: {vm} is no longer declared");
                }
            }

            return stale;
        }
    }

    /// A default that would be refused is not offered. Running this against the *other* host's
    /// file — a copy of the pair's mirror image, which is exactly the mistake somebody makes —
    /// seeds a peer name that is this machine, and offering it back would leave a question
    /// Enter can never answer.
    private string? SeededPeer() =>
        this.seed?.Peer?.Hostname is { } peer
        && !string.Equals(peer, this.facts.MachineName, StringComparison.OrdinalIgnoreCase)
            ? peer
            : null;

    private string? SeededRole() =>
        this.seed?.Replication?.ExpectedRole is { } role
            ? role.Equals("replica", StringComparison.OrdinalIgnoreCase) ? "dr" : "primary"
            : null;

    private string? SeededPriority(string name) => this.SeededVm(name)?.Priority;

    private string? SeededDc(string name) =>
        this.SeededVm(name) is { } vm ? (vm.IsDomainController ? "y" : "n") : "n";

    private VmDocument? SeededVm(string name) =>
        this.seed?.Vms?.FirstOrDefault(
            vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<string> SixLines() =>
    [
        "These have sensible figures, and the file explains each of them:",
        $"  host memory reserve      {this.Six("node.reserve")} GB",
        $"  peer offline after       {this.Six("peer.offline")} s",
        $"  replication frequency    {this.Six("replication.frequency")} s",
        $"  lag warning multiplier   {this.Six("replication.lag")}",
        $"  data volume              {this.Six("storage.data_volume")}",
        $"  free space warning       {this.Six("storage.free_space")} GB",
    ];

    private IReadOnlyList<InterviewQuestion> SixQuestions() =>
    [
        InterviewQuestion.Of(
            "node.reserve", "Host memory reserve, in GB", this.Six("node.reserve"), Groups.Thresholds),
        InterviewQuestion.Of(
            "peer.offline", "Call the peer offline after, in seconds", this.Six("peer.offline"), Groups.Thresholds),
        InterviewQuestion.Of(
            "replication.frequency", "Replication frequency, in seconds", this.Six("replication.frequency"), Groups.Thresholds),
        InterviewQuestion.Of(
            "replication.lag", "Warn at how many times that frequency", this.Six("replication.lag"), Groups.Thresholds),
        InterviewQuestion.Of(
            "storage.data_volume", "The volume the VMs live on", this.Six("storage.data_volume"), Groups.Thresholds),
        InterviewQuestion.Of(
            "storage.free_space", "Warn below how many GB free", this.Six("storage.free_space"), Groups.Thresholds),
    ];

    /// What the file already says, or the default. One method so the grouped question and the
    /// six separate ones can never disagree about what is being accepted.
    private string Six(string key) =>
        key switch
        {
            "node.reserve" => Text(this.seed?.Node?.HostMemoryReserveGb, DraftDefaults.HostMemoryReserveGb),
            "peer.offline" => Text(this.seed?.Peer?.OfflineAfterSec, DraftDefaults.PeerOfflineAfterSec),
            "replication.frequency" => Text(this.seed?.Replication?.ExpectedFrequencySec, DraftDefaults.FrequencySec),
            "replication.lag" => Text(this.seed?.Replication?.LagWarningMultiplier, DraftDefaults.LagMultiplier),
            "storage.data_volume" => this.seed?.Storage?.DataVolume is { Length: > 0 } volume
                ? volume
                : DraftDefaults.DataVolume,
            _ => Text(this.seed?.Storage?.FreeSpaceWarningGb, DraftDefaults.FreeSpaceWarningGb),
        };

    private static string Text(int? seeded, int fallback) =>
        DraftDefaults.Text(seeded ?? fallback);

    private ConfigurationDraft Build(InterviewPlan plan)
    {
        _ = plan;

        return new ConfigurationDraft(
            this.facts.MachineName,
            int.Parse(this.Answered("node.reserve"), CultureInfo.InvariantCulture),
            this.answers["peer.hostname"],
            this.answers["peer.address"],
            int.Parse(this.Answered("peer.offline"), CultureInfo.InvariantCulture),
            this.answers["role"] == "dr" ? ExpectedRole.Replica : ExpectedRole.Primary,
            this.answers["switch"],
            int.Parse(this.Answered("replication.frequency"), CultureInfo.InvariantCulture),
            int.Parse(this.Answered("replication.lag"), CultureInfo.InvariantCulture),
            this.Answered("storage.data_volume"),
            int.Parse(this.Answered("storage.free_space"), CultureInfo.InvariantCulture),
            [.. this.ChosenVms().Select(this.DraftVmOf)],
            this.Carried());
    }

    /// Answered only when the grouped question was declined; otherwise it is what that
    /// question displayed and the operator accepted.
    private string Answered(string key) =>
        this.answers.TryGetValue(key, out string? typed) ? typed : this.Six(key);

    /// The three answers, plus whatever the file already said about this VM. A VM marked
    /// `failover: manual` because it boots without its 4 TB repository must not come back as
    /// `auto` because somebody re-ran the interview to add a different machine.
    private DraftVm DraftVmOf(string name)
    {
        VmDocument? seeded = this.SeededVm(name);

        return new DraftVm(
            name,
            this.answers[$"priority:{name}"] == "P1" ? VmPriority.P1 : VmPriority.P2,
            Yes(this.answers[$"dc:{name}"]),
            seeded?.HasPassthroughDisk ?? false,
            seeded?.ExpectedStartupRamMb,
            seeded?.GuestOsSupportEnds,
            seeded?.Failover);
    }

    /// Same reasoning one level up: the parts of `replication` and `storage` no question
    /// covers are the file's, not the interview's, and a rewrite has to give them back.
    ///
    /// The unattended authorisation is kept only for VMs still declared: the validator refuses
    /// an entry naming any other.
    private CarriedSettings Carried()
    {
        if (this.seed is null)
        {
            return CarriedSettings.None;
        }

        IReadOnlyList<string> chosen = this.ChosenVms();

        return new CarriedSettings(
            this.seed.Replication?.HealthWarningAfterSec,
            this.seed.Replication?.TestFailoverSwitch,
            this.seed.Replication?.TestFailoverOrphanAfterHours,
            this.seed.Replication?.UnattendedTestFailoverVms is { } unattended
                ? [.. unattended.Where(name => name is not null
                    && chosen.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))]
                : null,
            this.seed.Storage?.CheckBitlockerAutounlock);
    }

    private sealed record InterviewPlan(IReadOnlyList<InterviewQuestion> Steps, bool Complete);
}
