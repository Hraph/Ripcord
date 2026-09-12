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

**Milestone 0 shipped. Milestones 1 and 1b built, awaiting validation on the real hosts.**
`status`, `version`, `serve` and `deploy-listener` are implemented and covered by tests that
run on Linux — including the mTLS handshake end to end, with generated certificates and real
sockets. The WMI adapter is written but has never run on a Hyper-V host, so nothing here is
proven against real infrastructure yet.

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
switch exists, the target has the RAM, the certificate is still valid — is `ripcord check`,
milestone 2.

The machine names, addresses and thumbprints throughout this repository are pseudonymous.

## Commands

```
ripcord status [--config <path>]              read both sides of the pair
ripcord serve [--config <path>]               run the read-only pair listener
ripcord deploy-listener [--dry-run] [--remove]  install or remove that listener
ripcord version                               version and commit hash
```

`deploy-listener` reconciles rather than installs: it compares the host with the configuration
and applies only the difference, so re-running it on a correct host does nothing, a moved
binary or a changed port becomes an update, and `--remove` is the same list read backwards.
Nothing mutating happens without `--dry-run` first showing the plan and the operator then
typing the node name.

The pair channel carries one thing in one direction: this host's published state. It has no
verb, no parameter and no request body, so there is nothing to abuse — and the service that
answers it never touches Hyper-V, it serves a file the privileged command wrote.

`status` describes; it does not judge — that is `ripcord check`, milestone 2. Output is fixed
at 75 columns with no colour, because the real reading conditions are a 1024×768 KVM during an
incident.

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
| 2 | invalid invocation, or invalid or missing configuration |
| 3 | local access failure (WMI, privileges, timeout) |

An unreachable peer is a degraded state, not an error: a scheduled `ripcord status` must not
alert because the other host is down.

The PENDING column renders `-` when Hyper-V answers the statistics call asynchronously:
Ripcord declines to poll a job for one column. See
[milestone 1](docs/milestones/milestone-1.md).

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
