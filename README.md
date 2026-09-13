# Ripcord

**Disaster recovery orchestration for Hyper-V Replica pairs — status, drift detection, and
guided failover.**

On the day of an incident, the operator has to retype into graphical wizards parameters that
are already known and written down: target server, port, certificate thumbprint, frequency,
replication direction. Under pressure, that is where mistakes happen.
'sw'
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

**Milestone 0 shipped. Milestones 1, 1b, 2, 3, 4, most of 4B and 5 built, awaiting validation on
the real hosts.** `status`, `check` (with `--notify`), `test-failover`, `failover` in both
scenarios, `failback`, `fence`, `check-update`, `version`, `serve` and `deploy-listener` are
implemented and covered by tests that run on Linux — including the mTLS handshake end to end, with generated certificates and real
sockets, and a case table per check rule.

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
Per-milestone detail: [`docs/milestones/`](docs/milestones/).
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

`ripcord.yaml`, next to the binary, so updating means replacing the `.exe` while the
configuration stays. It is **not** symmetric: `node` and `peer` are swapped between the two
hosts, and Ripcord validates `node.hostname` against the machine's real name so a
copied-and-not-edited file is refused rather than producing an inverted view.

Two samples ship in [`config/`](config/): [`ripcord.primary.yaml`](config/ripcord.primary.yaml)
and [`ripcord.dr.yaml`](config/ripcord.dr.yaml). Both are parsed and validated by the test
suite, so a sample that stops being valid breaks the build.

Startup validation reports **every** error at once rather than failing on the first, and it
answers one question only: "can this file be read?". Whether reality matches the file — the
switch exists, the target has the RAM, the certificate is still valid — is `ripcord check`.

The machine names, addresses and thumbprints throughout this repository are pseudonymous.

## Commands

```
ripcord status [--config <path>]              read both sides of the pair
ripcord check [--config <path>] [--notify [--dry-run]]
                                              would a failover work right now
ripcord test-failover (--vm <name> | --all) [--dry-run] [--unattended]
                                              boot a replica in isolation, then destroy it
ripcord failover --scenario planned|unplanned (--vm <name> | --all | --priority P1)
                                              move a VM to the other host
ripcord failback (--vm <name> | --all | --priority P1) [--dry-run]
                                              move it back once the pair is protected again
ripcord fence [--dry-run]                     stop this host's VMs starting themselves
ripcord serve [--config <path>]               run the read-only pair listener
ripcord dashboard [--config <path>]           serve the read-only page on 127.0.0.1
ripcord deploy-listener [--dry-run] [--remove]  install or remove that listener
ripcord check-update [--config <path>]        is a newer release published
ripcord version                               version and commit hash
```

`deploy-listener` reconciles rather than installs: it compares the host with the configuration
and applies only the difference, so re-running it on a correct host does nothing, a moved
binary or a changed port becomes an update, and `--remove` is the same list read backwards.
Nothing mutating happens without `--dry-run` first showing the plan and the operator then
typing the node name.

`failover` drives **only the host it is run on**. Each invocation carries out the steps the plan
assigns to this machine, then names the other host and prints the exact command to type there.
That is a deliberate refusal to build a channel that mutates the peer: in an unplanned failover
the primary is dead by definition, so a cross-host execution path is unavailable in precisely the
case the tool exists for. Where the sequence has got to is re-derived from what both hosts
report, never from a stored position, so running it twice is safe and running it on the wrong
host is refused rather than obeyed.

`fence` is the command that is easy to leave out and expensive to forget. After an unplanned
failover the original primary still holds a copy of every VM that moved, set to start itself,
and both hosts sit on the same external switch on the same subnet — so restoring its power
boots the old domain controller beside the failed-over one, with the same identity and the same
address. `fence` records each VM's `AutomaticStartAction`, sets it to `Nothing`, and names any
copy it could not confirm switched off. The unplanned failover prints the instruction to run it,
on the host it has to be run on, before the operator leaves the screen.

A VM can be kept out of sweeps with `failover: manual` in its configuration block. The backup VM
is set that way: it boots without its 4 TB repository, and depending on the backup product it
may start writing fresh full backups into the target's `D:` — the volume already carrying the
replica disks and the failover undo data. Filling it puts the failed-over VMs into
paused-critical, the domain controller included. Failing it over is still possible; it has to be
named.

The pair channel carries one thing in one direction: this host's published state. It has no
verb, no parameter and no request body, so there is nothing to abuse — and the service that
answers it never touches Hyper-V, it serves a file the privileged command wrote.

`status` describes; it does not judge — that is `check`. Output is fixed at 75 columns with no
colour, because the real reading conditions are a 1024×768 KVM during an incident.

```
RIPCORD STATUS                                      2026-09-12 14:00:00 UTC

LOCAL   HV-REPLICA-01                                             REACHABLE
  VM                   ROLE     STATE            HEALTH       LAG   PENDING
  -------------------------------------------------------------------------
  VM-DC-01             Replica  Resynchronizing  Critical   6h00m     16 GB
  VM-LEGACY-01         Replica  Replicating      Warning    9m00s    256 MB
  VM-BACKUP-01         None     Disabled         Unknown        -         -

PEER    HV-PRIMARY-01                                               OFFLINE
  No peer channel configured on this node.
  Unreachable since: never contacted
```

| Exit code | Meaning |
|---|---|
| 0 | success — **including an unreachable peer** |
| 1 | at least one critical rule violated (`check` only) |
| 2 | invalid invocation, or invalid or missing configuration |
| 3 | local access failure (WMI, privileges, timeout) |

An unreachable peer is a degraded state, not an error: a scheduled `ripcord status` must not
alert because the other host is down.

### `ripcord dashboard`

The same `check`, in a browser, for reading rather than typing. It is **off unless the
configuration switches it on**, and it is served on `127.0.0.1` and nowhere else — the address
is written in the code, not read from the file, so there is no key that can widen it onto the
network.

```yaml
dashboard:
  enabled: true
  port: 7080
  refresh_sec: 30
```

The page is one self-contained document: no script, no stylesheet, no image, nothing fetched
from anywhere. These hosts have no outbound access by design, and a page that degrades to
unstyled markup the moment the network goes degrades exactly when it is being read. It reloads
itself through a `<meta>` refresh, so it keeps working with scripting switched off.

It serves a read of `/` and nothing else — every other verb is refused, every other path is
absent, and nothing on it changes anything. A failover still needs a human typing a node name
into a terminal.

A reading that failed becomes a page saying the reading failed. There is no stderr anybody is
watching and no exit code to carry it, so the alternative is a page still showing the previous
state, which is the one thing a dashboard must never do.

Browse to `http://127.0.0.1:7080/`, not `http://localhost:7080/` — `localhost` resolves to the
IPv6 loopback first on a modern Windows, and the page is served on the IPv4 one only.

If `HttpListener` refuses to bind under a non-elevated account, reserve the URL once:

```
netsh http add urlacl url=http://127.0.0.1:7080/ user=DOMAIN\account
```

### `ripcord check`

`check` answers one question: if it goes down now, does it hold? Each finding states what was
observed, what that means **on the day of the failover**, and the command that fixes it —
never executing it. The middle line is the one that matters: "incorrect vSwitch" helps nobody,
"this VM will boot with no network on the target" triggers action.

```
RIPCORD CHECK                                       2026-09-13 14:00:00 UTC
ripcord 0.1.0+0000000

  Mode:       NORMAL
  Source:     HV-PRIMARY-01 (holds the primary copies)
  Target:     HV-REPLICA-01 (a failover would land here)
  Verdict:    NOT READY - 1 critical, 1 warning, 3 info, 0 not checked

CRITICAL (1)
---------------------------------------------------------------------------
  [replica-switch-mismatch] VM-DC-01
    Observed:   Network Adapter on HV-REPLICA-01 is on 'vSwitch-OLD',
                expected 'vSwitch-PROD'
    On the day: this VM boots onto the wrong network on the target; the
                host-level switch check still passes, so nothing else would
                report it
    Fix:        Connect-VMNetworkAdapter -VMName VM-DC-01 -Name 'Network
                Adapter' -SwitchName 'vSwitch-PROD'

FEASIBILITY - HV-REPLICA-01
---------------------------------------------------------------------------
  Usable memory: 8192 MB
  VM                   PRI    STARTUP       MAX       MIN  BOOTS
  -------------------------------------------------------------------------
  VM-DC-01             P1     2048 MB   4096 MB   1024 MB  yes
  VM-LEGACY-01         P1     2048 MB   4096 MB   1024 MB  yes
  VM-BACKUP-01         P2     2048 MB   4096 MB   1024 MB  yes
  Headroom after the VMs that would boot: 2048 MB
```

Three things are deliberate in that output.

**A rule whose data is missing is listed under `NOT CHECKED`, never treated as satisfied.** A
reassuring false negative is the worst outcome this tool can produce. `NOT CHECKED` sits above
the warnings, and its count is in the headline, because the exit code cannot carry it: code 1
means "a critical rule is violated", and overloading it would make `check` unusable as the
gate the later milestones depend on.

**Permanent criticals are acknowledged, with a mandatory expiry.** The pass-through disk on
`VM-BACKUP-01` is an accepted, unfixable property of this infrastructure. Without a way to
accept it, `check` exits 1 forever and every milestone that gates on it dies with it. An
acknowledgement names a rule and optionally a VM, carries a reason and a date, and is still
printed — accepted, not hidden. Once the date passes the finding counts again and says why it
came back. The rules whose violation means the service would not come back at all —
`startup-ram-exceeds-target`, `p1-startup-ram-sum-exceeds-target`,
`vhdx-outside-relationship` — can never be acknowledged.

**The operating mode is derived from observed state.** While the pair runs on the disaster
recovery side, "replication direction inverted" is true by definition. Reporting it as a
critical would make `check` red for the whole incident and block the failback meant to end it,
so `FAILED OVER` suppresses that rule and the header says so instead. A *partial* inversion —
some VMs primary on one host and some on the other — is still critical.

The PENDING column renders `-` when Hyper-V answers the statistics call asynchronously:
Ripcord declines to poll a job for one column. See
[milestone 1](docs/milestones/milestone-1.md).

### Alerting

Hyper-V Replica surfaces plenty of state and pushes none of it: a resync can run for three weeks
with nobody knowing. `ripcord check --notify` is the delivery half, and nothing else — the
trigger is the same code 1 the command already returns, so there is no second detection path
and no second opinion about whether the pair is healthy.

```
schtasks /Create /TN "Ripcord check" /SC MINUTE /MO 15 /RL HIGHEST /RU SYSTEM ^
  /TR "\"C:\Program Files\Ripcord\ripcord.exe\" check --notify"
```

Four rules decide what actually leaves the host, and all four exist because an alert nobody
reads is worse than no alert at all.

- **Transitions, not runs.** A critical finding that was not in the last notification is sent at
  once. The same one on the next run is not: a mail every fifteen minutes becomes a filter rule
  within a week, and then the criticals go unread too.
- **A repeat threshold**, 24 hours by default, after which a still-broken pair is mentioned
  again.
- **Quiet hours**, in the host's local time. An alert raised inside the window is **held and
  delivered when the window ends**, never dropped. One that clears before the window ends is
  dropped, because nothing was ever sent to correct.
- **Grouping.** Everything wrong with the pair goes in one message, with each finding's
  implication on the day of the failover and the command that fixes it — not a dump of the
  check output. The pair recovering is itself a transition and is notified once.

Delivery never changes the exit code: a relay that is down must not be reported as a pair that
is broken. A notification that was held, refused or never attempted is printed on stderr beside
the report, and what was sent is remembered in `alert-state.json` next to the binary — written
only after the transport accepted it, so a relay that was down for a minute does not cost a
day's silence. `check --notify --dry-run` shows what would go where and writes nothing.

The SMTP password is never in `ripcord.yaml`; `password_secret` names an environment variable
the service reads it from, and a `password:` key in the file is refused by name. On a host
running the check as SYSTEM that means a machine-wide variable — readable by administrators,
which is the trade-off — so an internal relay that accepts from this subnet without credentials
is the better arrangement where one exists.

`ripcord check-update` asks whether a newer release has been published and reports the version.
It is **off** unless `updates.check` says otherwise, and refuses rather than silently skipping
when it is off: these hosts are meant to have no outbound access, and a host somebody believes
is checking is worse than one that plainly is not. Nothing downloads and nothing installs. It
addresses the repository by numeric id rather than by `owner/name`, because a rename leaves a
redirect that stops failing — and starts returning a stranger's releases — the day somebody
recreates the abandoned name.

Install is a copy: the `.exe` and one of the samples from `config/`, renamed `ripcord.yaml`,
side by side. `--config` overrides the path.

## License

MIT — see [`LICENSE`](LICENSE).

## Repository metadata

Topics: `hyper-v` `hyper-v-replica` `disaster-recovery` `failover` `replication`
`windows-server` `dotnet` `csharp` `cli` `sysadmin` `high-availability`
`business-continuity`

"Hyper-V" stays out of the repository name — it is a Microsoft trademark. It lives in the
description and the topics, which is enough for discoverability.
