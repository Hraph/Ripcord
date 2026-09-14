# Ripcord

**Disaster recovery orchestration for Hyper-V Replica pairs — status, drift detection, and
guided failover.**

On the day of an incident, the operator has to retype into graphical wizards parameters that
are already known and written down: target server, port, certificate thumbprint, frequency,
replication direction. Under pressure, that is where mistakes happen.
Worse, some steps are invisible in the Microsoft interfaces. After an unplanned failover,
reversing replication requires marking the original primary as a replica first:

```powershell
Set-VMReplication -VMName '<VM>' -AsReplica
```

That command **cannot be run from Hyper-V Manager or Failover Cluster Manager**, and it is
blocking. It is exactly the kind of knowledge that belongs in tested code rather than in
somebody's memory at 3 a.m.

**The goal: one command, no parameter to look up.** Everything else comes from a configuration
file written in calm conditions.

## Project state

**Milestone 0 shipped. Milestones 1, 1b, 2, 3, 4, most of 4B, 5 and 7 built, awaiting validation
on the real hosts.** `status`, `check` (with `--notify`), `test-failover`, `failover` in both
scenarios, `failback`, `fence`, `check-update`, `update`, `dashboard`, `version`, `serve` and
`ripcord service` are implemented and covered by tests that run on Linux — including the mTLS
handshake end to end with generated certificates and real sockets, the read-only page served
over a real loopback socket, release signatures verified against a real generated key pair, and
a case table per check rule.

`reprotect` is **not** built. It re-establishes replication onto the returning host, and the
`Set-VMReplication -AsReplica -AllowedPrimaryServer` step it turns on is the one no GUI offers
and no documentation settles (V3, V16). Writing that sequence before the lab answers those
would be guessing at the one operation whose failure mode is a pair that looks protected and
is not.

**Nothing here has run on a Hyper-V host.** The WMI adapters are written blind from the
Microsoft reference, and a round of checking names against that reference found four that did
not exist — including one whose absence meant a critical rule had never fired at all. Treat the
adapters as unverified until the lab says otherwise; the outstanding facts are listed as V-items
in [`docs/TRACKING.md`](docs/TRACKING.md).

One limitation worth knowing before reading further: **a planned failover cannot yet cross from
the primary to the replica.** `Start-VMFailover -Prepare` leaves no state this binary can name,
so the replica cannot observe that the primary's half has run, and refuses rather than assuming.
It is question Q5, and it is pinned by a test rather than left silent.

Progress and decisions: [`docs/TRACKING.md`](docs/TRACKING.md).
Command reference: [`docs/commands/`](docs/commands/).
Specification review before coding: [`docs/COHERENCE.md`](docs/COHERENCE.md).

## Layout

```
src/Ripcord.Domain/           state, rules, decision sequences — zero external dependencies
  Replication/                the replication model, and the CIM lookups the adapter must not own
  Configuration/              the config document, the validated model, and the validator
src/Ripcord.Ports/            IHypervProvider, IConfigStore, IClock
src/Ripcord.Application/      use cases
src/Ripcord.Cli/              argument parsing, console rendering (a library, not the exe)
src/Ripcord.Adapters.Fake/    in-memory provider, used by every test
src/Ripcord.Adapters.Yaml/    ripcord.yaml loading (translation only; validation is Domain)
src/Ripcord.Adapters.Pairing/ snapshot wire format, mTLS listener and client — runs on Linux
src/Ripcord.Adapters.Wmi/     net10.0-windows only — CIM to domain translation, no decisions
src/Ripcord.Host.Windows/     composition root; the only project that produces ripcord.exe
tests/Ripcord.Tests/          net10.0 — runs on Linux and macOS, no Windows required
```

`Ripcord.Domain` references nothing at all. That is asserted by `HexagonalBoundaryTests`,
which parses the `.csproj` files rather than reflecting over compiled assemblies — an unused
`PackageReference` is invisible to reflection. Project references are asserted exactly;
packages as a subset of a per-project allow-list, so a new dependency has to be declared there
on purpose.

## Building and testing

Everything except the two Windows projects builds and tests on any OS:

```bash
dotnet build Ripcord.Linux.slnf -c Release
dotnet test  Ripcord.Linux.slnf -c Release
```

`Ripcord.Linux.slnf` excludes `Ripcord.Adapters.Wmi` and `Ripcord.Host.Windows`, keeping the
tested surface Linux-only. It is not a workaround for a build failure — a solution-level build
off Windows compiles the WMI adapter fine. The boundary is guarded by
`HexagonalBoundaryTests` plus the `NU1201` that a Windows-targeting Domain would trigger
inside the filtered build.

In a container, the same thing the CI runs:

```bash
docker build -t ripcord-tests .
```

### On macOS and Linux, compile-checking the Windows projects

The Windows projects cannot *run* off Windows, but they do compile — with no special flag:

```bash
dotnet build Ripcord.sln -c Release
```

A plain `net10.0-windows` target needs no Windows targeting pack, and
`Microsoft.Management.Infrastructure` restores off Windows too (checked in a scratch project;
the package itself lands with milestone 1). So `WmiHypervProvider` will not be written
entirely blind: typos and API misuse are caught locally.

What cannot be checked this way is the CIM mapping itself — whether those classes and
properties actually hold the values expected. That needs a real Hyper-V host, which is why the
WMI adapter has its own manual test level.

`-p:EnableWindowsTargeting=true` is not required today; it stays documented in case a future
`net10.0-windows10.x` bump needs it. It has no place in the CI test run.

## Configuration

One `ripcord.yaml` per host, beside the binary. The two are mirror images — the `node` and
`peer` blocks swapped — and copying one across without swapping them is refused at startup, by
name. Every command validates the whole file first and reports every error at once.

The reference is [`docs/configuration.md`](docs/configuration.md); the shipped samples in
[`config/`](config) carry a comment on every key and are the place to start.

## Installation

From an elevated PowerShell, on a host that can reach GitHub:

```powershell
irm https://raw.githubusercontent.com/Hraph/Ripcord/main/install.ps1 | iex
```

It fetches the release, checks the SHA-256, and **verifies the detached ECDSA P-256 signature
against a public key written into the script** — the same key the binary carries. A release
that fails either check is not installed and nothing on the host is touched. There is no
switch to skip that, because a switch to skip verification is the switch somebody uses at
3 a.m. Then it puts `ripcord.exe` in `C:\Program Files\Ripcord`, adds that to the machine
`PATH`, and leaves a `ripcord.yaml` beside it — the annotated sample if it could fetch one, a
deliberately incomplete template otherwise. Either way the file names no peer and no VM, and
every command refuses until you fill it in: those refusals are the checklist. An existing
`ripcord.yaml` is never touched.

One thing to be clear about: `irm | iex` runs a script nobody checked. The script verifies what
it installs; nothing verifies the script. On a host that runs a domain controller that is worth
one moment's thought, and the alternative is below.

**These two hosts are meant to have no outbound access at all**, which is the arrangement the
rest of this tool assumes. For that, the download happens somewhere else:

```powershell
# on a machine with network, which never touches the pair
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/Hraph/Ripcord/main/install.ps1))) -Prepare D:\ripcord-release
```

That verifies the release and leaves a folder holding it, the sample configurations, and a copy
of `install.ps1` — the offline host cannot fetch the installer any more than it can fetch the
binary. Copy the folder to each host and, from an elevated PowerShell there:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -FromPath .
```

The checksum and the signature are checked again on the host. A folder that arrived on a USB
stick is not more trusted than a download.

| Switch | |
|---|---|
| `-Role primary\|dr` | Which sample configuration to place. Asked for if omitted. |
| `-Path <dir>` | Somewhere other than `C:\Program Files\Ripcord`. |
| `-Version v0.1.0` | A particular release rather than the latest. |
| `-CheckTask` | Create the scheduled `ripcord check --notify` task (see [Alerting](#alerting)). |
| `-Shortcut` | A Start Menu shortcut to the dashboard page. |
| `-Force` | Reinstall over an existing binary. It never replaces an existing `ripcord.yaml`. |

**The listener reads `ripcord.yaml` once, when the service starts.** Editing the file later
changes nothing until `ripcord service restart` — the running listener goes on serving the
configuration it was started with, and the peer sees no difference. `ripcord status` and
`ripcord check`, being commands rather than the service, read the file every time they run, so
the two can disagree until the service is restarted.

Installing is not configuring. `node.hostname`, the peer address and both certificate
thumbprints are per-host, and `ripcord status` refuses a file that names another machine — by
name, at startup. The installer ends by saying so, and by naming the three commands to run in
order.

Updating an installed host is [`ripcord update`](#commands), not this script.

## Commands

One page per command, in [`docs/commands/`](docs/commands/) — what it answers, what it refuses,
and what its exit code means.

| | | |
|---|---|---|
| [`status`](docs/commands/status.md) | what both hosts are doing | read-only |
| [`check`](docs/commands/check.md) | would a failover work right now | read-only |
| [`service`](docs/commands/service.md) | the listener: state, install, remove, restart | bare form read-only |
| [`serve`](docs/commands/serve.md) | the listener itself | read-only |
| [`dashboard`](docs/commands/dashboard.md) | the same answer, in a browser | read-only |
| [`test-failover`](docs/commands/test-failover.md) | boot a replica in isolation, then destroy it | mutating |
| [`failover`](docs/commands/failover.md) | move a VM to the other host | mutating |
| [`failback`](docs/commands/failback.md) | move it home again | mutating |
| [`fence`](docs/commands/fence.md) | stop a returning host starting its old copies | mutating |
| [`update`](docs/commands/update.md) | install a newer release here | mutating |
| [`check-update`](docs/commands/check-update.md) | is a newer release published | read-only |
| [`version`](docs/commands/version.md) | which binary is this | read-only |

Every mutating command takes `--dry-run` and prints its whole plan without touching anything.
None of them changes a thing until a word is typed in full. The exit codes are the same
everywhere and are listed once, in [the command index](docs/commands/README.md#exit-codes).

Further reading: [alerting](docs/alerting.md), [`ripcord.yaml`](docs/configuration.md),
[releasing and updating a pair](docs/RELEASING.md),
[the threat model](SECURITY.md).

## License

MIT — see [`LICENSE`](LICENSE).

## Repository metadata

Topics: `hyper-v` `hyper-v-replica` `disaster-recovery` `failover` `replication`
`windows-server` `dotnet` `csharp` `cli` `sysadmin` `high-availability`
`business-continuity`

"Hyper-V" stays out of the repository name — it is a Microsoft trademark. It lives in the
description and the topics, which is enough for discoverability.
