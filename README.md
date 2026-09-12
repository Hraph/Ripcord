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

**Milestone 0 — skeleton and test CI.** No functionality yet. The hexagonal boundary is in
place and enforced by a test; `ripcord status` arrives with milestone 1.

Progress and decisions: [`docs/TRACKING.md`](docs/TRACKING.md).
Per-milestone detail: [`docs/milestones/`](docs/milestones/).
Specification review before coding: [`docs/COHERENCE.md`](docs/COHERENCE.md).

## Layout

```
src/Ripcord.Domain/           state, rules, decision sequences — zero external dependencies
src/Ripcord.Ports/            the interfaces the Domain is driven through (empty until milestone 1)
src/Ripcord.Application/      use cases
src/Ripcord.Cli/              argument parsing, console rendering (a library, not the exe)
src/Ripcord.Adapters.Fake/    in-memory provider, used by every test
src/Ripcord.Adapters.Wmi/     net10.0-windows only — CIM to domain translation, no decisions
src/Ripcord.Host.Windows/     composition root; the only project that produces ripcord.exe
tests/Ripcord.Tests/          net10.0 — runs on Linux and macOS, no Windows required
```

`Ripcord.Domain` references nothing at all. That is asserted by
`HexagonalBoundaryTests`, which parses the `.csproj` files rather than reflecting over
compiled assemblies — an unused `PackageReference` is invisible to reflection.

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

Schema and samples arrive with milestone 1.

## License

MIT — see [`LICENSE`](LICENSE).

## Repository metadata

Topics: `hyper-v` `hyper-v-replica` `disaster-recovery` `failover` `replication`
`windows-server` `dotnet` `csharp` `cli` `sysadmin` `high-availability`
`business-continuity`

"Hyper-V" stays out of the repository name — it is a Microsoft trademark. It lives in the
description and the topics, which is enough for discoverability.
