# Ripcord

**Disaster recovery for a pair of Hyper-V Replica hosts.** See both sides at once, know whether
a failover would actually work, and run one when it has to happen — with one command and no
parameter to look up.

On the day, the operator retypes into graphical wizards what is already written down: target
server, port, certificate thumbprint, frequency, replication direction. That is where mistakes
happen. And some steps are not in the interfaces at all — after an unplanned failover,
reversing replication needs `Set-VMReplication -AsReplica`, which neither Hyper-V Manager nor
Failover Cluster Manager offers, and which blocks everything until it has run.

That belongs in tested code, not in somebody's memory at three in the morning.

## Install

From an elevated PowerShell:

```powershell
irm https://raw.githubusercontent.com/Hraph/Ripcord/main/install.ps1 | iex
```

It checks the release's SHA-256 **and** its signature before anything lands, and installs
nothing if either fails. For hosts with no outbound access — which is what these two are meant
to be — `-Prepare` downloads and verifies elsewhere and `-FromPath` installs from the copied
folder. [Installing in full](docs/install.md).

Then fill in `ripcord.yaml` beside the binary and run `ripcord status`. It refuses until the
file is right, and the refusals are the checklist.

## What it looks like

```
RIPCORD STATUS                                      2026-09-12 14:00:00 UTC
ripcord 0.2.1+abc123def456 on both hosts

LOCAL   HV-REPLICA-01                                             REACHABLE
  VM                   ROLE     STATE            HEALTH       LAG   PENDING
  -------------------------------------------------------------------------
  VM-DC-01             Replica  Resynchronizing  Critical   6h00m     16 GB
  VM-LEGACY-01         Replica  Replicating      Warning    9m00s    256 MB
  VM-BACKUP-01         None     Disabled         Unknown        -         -

PEER    HV-PRIMARY-01                                               OFFLINE
  Unreachable since: 2026-09-12 13:41:02 UTC

LISTENER  running                                        0.2.1+abc123def456
```

75 columns, no colour, nothing that needs a wide terminal — the real reading conditions are a
1024×768 KVM during an incident.

## Commands

One page each, in [`docs/commands/`](docs/commands/).

| | | |
|---|---|---|
| [`init`](docs/commands/init.md) | write this host's `ripcord.yaml` by interview | mutating |
| [`status`](docs/commands/status.md) | what both hosts are doing | read-only |
| [`check`](docs/commands/check.md) | would a failover work right now | read-only |
| [`service`](docs/commands/service.md) | the listener: state, install, remove, restart, start, stop | bare form read-only |
| [`serve`](docs/commands/serve.md) | the listener itself | read-only |
| [`dashboard`](docs/commands/dashboard.md) | the same answer, in a browser | read-only |
| [`test-failover`](docs/commands/test-failover.md) | boot a replica in isolation, then destroy it | mutating |
| [`failover`](docs/commands/failover.md) | move a VM to the other host | mutating |
| [`failback`](docs/commands/failback.md) | move it home again | mutating |
| [`fence`](docs/commands/fence.md) | stop a returning host starting its old copies | mutating |
| [`update`](docs/commands/update.md) | install a newer release here | mutating |
| [`rollback`](docs/commands/rollback.md) | go back to the binary the last update set aside | mutating |
| [`check-update`](docs/commands/check-update.md) | is a newer release published | read-only |
| [`version`](docs/commands/version.md) | which binary is this | read-only |

Nothing mutates without an explicit answer: the node name typed in full for what moves
production VMs (`failover`, `failback`, `fence`), `y/n` with Enter declining for the rest. Every mutating command takes `--dry-run` and
prints its whole plan without touching anything. The exit codes are the same everywhere and are
listed once, in [the command index](docs/commands/README.md#exit-codes).

Also: [alerting](docs/alerting.md) · [`ripcord.yaml`](docs/configuration.md) ·
[the diagnostic log](docs/diagnostics.md) ·
[releasing and updating a pair](docs/RELEASING.md) · [the threat model](SECURITY.md).

## Where it stands

Everything above is built and covered by tests that run on Linux — including the mutual TLS
handshake end to end with generated certificates and real sockets, and a case table for each of
the twenty-two check rules.

**None of it has run on a Hyper-V host.** The WMI adapters are written blind from the Microsoft
reference; one pass over that reference found four class or property names that did not exist,
one of them meaning a critical rule had never fired at all. Treat them as unverified until a
real pair says otherwise.

Two gaps worth knowing before you rely on this:

- **`reprotect` is not built.** It re-establishes replication onto a returning host, and the
  step it turns on is the one no interface offers and no documentation settles. Writing it
  blind would be guessing at the one operation whose failure mode is a pair that looks
  protected and is not. Until it exists, coming home after an unplanned failover is done by
  hand.
- **A planned failover cannot yet cross from the primary to the replica.** `Start-VMFailover
  -Prepare` leaves no state this binary can name, so the replica cannot observe that the
  primary's half has run — and it refuses rather than assuming. A test pins that refusal.

## Building

Everything but the two Windows projects builds and tests anywhere:

```bash
dotnet test Ripcord.Linux.slnf -c Release     # or: docker build -t ripcord-tests .
```

The Windows projects compile off Windows too — `dotnet build Ripcord.sln -c Release` — so
typos and API misuse are caught locally even though nothing WMI can be executed.

```
src/Ripcord.Domain/        state, rules, decision sequences — references nothing at all
src/Ripcord.Ports/         the interfaces the adapters implement
src/Ripcord.Application/   use cases
src/Ripcord.Cli/           argument parsing and rendering (a library, not the exe)
src/Ripcord.Adapters.*/    WMI, YAML, the pair channel, notification, updates, the page
src/Ripcord.Host.Windows/  composition root; the only project that produces ripcord.exe
tests/Ripcord.Tests/       runs on Linux and macOS, no Windows required
```

The Domain references nothing, and a test asserts it by parsing the project files rather than
by reflecting over assemblies — an unused package reference is invisible to reflection.
Adapters translate; they never decide. A decision that cannot be tested without Windows is a
decision nobody checks.

## License

MIT — see [`LICENSE`](LICENSE).
