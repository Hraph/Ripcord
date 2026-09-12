# Specification coherence review

A read-through of `.claude/ripcord-prompt.md` before the first line of code is written.
The specification is solid and unusually precise. The points below are gaps to close,
not disagreements with the approach.

Status: `open` = user decision needed · `settled` = assumption taken, documented in the
relevant milestone · `verify` = fact to confirm against the real infrastructure.

---

## Blockers for getting started

### B1 — "Start with milestone 1" contradicts milestone 0 — *settled*

The closing line asks to start at milestone 1, while milestone 0 (skeleton + Linux CI) is
explicitly presented as the guard that makes the hexagonal boundary non-negotiable "from the
first line".

**Taken**: milestone 0 first — it fits in one session, and it is what keeps milestone 1 from
drifting. Milestone 1 follows immediately. Shipped together.

### B2 — `dotnet build -c Release` will fail on `ubuntu-latest` — *settled*

`Ripcord.Adapters.Wmi` targets `net10.0-windows`. A solution-level build on Linux tries to
restore it and fails (`NETSDK1100`). The proposed CI cannot pass as written.

**Taken**: a solution filter `Ripcord.Linux.slnf` excluding the Windows projects, used by
both the CI and the Dockerfile. `EnableWindowsTargeting=true` is deliberately **not** used
there: it would compile the WMI code on Linux and dilute the very guard being installed.

It is however useful **locally on macOS**, as a separate opt-in build, to compile-check the
adapter that cannot be run there. That does not weaken the guard: a WMI reference in the
Domain is still caught by the Linux `slnf` build and by the architecture test that parses the
`.csproj`. See the development section of `CLAUDE.md`.

### B3 — The composition root breaks the Linux build — *settled*

`Ripcord.Cli` has to instantiate `WmiHypervProvider`. If it references it directly, the CLI
is no longer buildable on Linux and the boundary is lost at the first milestone.

**Taken**: `Ripcord.Cli` references only `Domain`, `Ports`, `Application`. A
`Ripcord.Host.Windows` project (`net10.0-windows`) is the only one that knows about the WMI
adapters and the only one that produces `ripcord.exe`. It is the entry point for the
`win-x64` publish.

### B4 — Only `IHypervProvider` is planned, but milestone 2 reaches outside Hyper-V — *settled*

Three milestone 2 rules have nothing to do with `root\virtualization\v2`:

| Rule | Actual source |
|---|---|
| BitLocker without auto-unlock on `D:` | `root\cimv2\Security\MicrosoftVolumeEncryption` |
| Free space on `D:` | `root\cimv2` → `Win32_LogicalDisk` |
| Certificate within 60 days of expiry | Windows certificate store, **not WMI** (`X509Store`) |

**Taken**: two extra ports introduced **at milestone 2 only**, not earlier —
`IHostSystemProvider` (volume, BitLocker) and `ICertificateProvider`. The "never
`powershell.exe`" rule holds: `X509Store` is a native .NET API.

### B5 — The peer channel is specified in Security but assigned to no milestone — *open*

The Security section requires, for any communication with the peer: **mTLS with the pair's
certificates, chain *and* CN validation, firewall-restricted to the peer's IP, read-only** —
"the channel carries states, never orders" — and "no command received from the network
triggers a Hyper-V action".

Milestone 1 needs peer state on day one. A remote CIM session (WinRM or DCOM) is the obvious
route, and it satisfies none of that: it is not the specified channel, it is authenticated by
Windows credentials rather than the pair's certificates, and it is not read-only by
construction — the same session that reads state can invoke a method.

Three options:
1. **Remote CIM session, read-only by discipline.** Cheapest. The adapter exposes only read
   methods, and the boundary is a code review promise rather than a property of the channel.
   Requires WinRM between the hosts, which is credentialed access one host has over the other.
2. **A small read-only Ripcord listener on each host**, mTLS with the existing replication
   certificates, serving a serialised `HostState` and nothing else. Matches the specification
   exactly, and the "never orders" guarantee becomes structural: the protocol has no verb.
   Costs a service, a port, a firewall rule and a milestone.
3. **Milestone 1 is local-only.** `ripcord status` runs on one host and shows one side. The
   pair view arrives with option 2, later.

**Recommendation**: option 3 for milestone 1, then option 2 as its own milestone before
milestone 4. It keeps the first deliverable honest — a single-host status view that works is
already better than `Get-VMReplication` — and it avoids standing up a credentialed remote
channel that the specification explicitly does not want, only to remove it later.

This is the largest open point in the review. Milestone 1's scope depends on the answer.

---

## Specification inconsistencies

### S1 — `RecoveryHistory 0` makes recovery point selection impossible — *open*

The context pins `RecoveryHistory 0`: only the most recent replication point exists. Yet
milestone 4 plans, for unplanned failover, to "select the recovery point".

Two ways out:
1. **Accept `RecoveryHistory 0`**: the step disappears, `ripcord` fails over to the only
   available point and reports estimated data loss in seconds. Simpler, faithful to the
   current infrastructure.
2. **Enable history** (`RecoveryHistory` > 0) on the infrastructure, which costs storage on
   the target and requires reference checkpoints.

**Recommendation**: option 1 for milestone 4, while still modelling point selection as a
one-element list — the step exists in the model, it simply has nothing to choose. Moving to
option 2 then becomes a config change rather than a refactor.

### S2 — `ripcord.yaml` is asymmetric; it differs on each host — *settled*

The sample config describes `node: HV-REPLICA-01` / `peer: HV-PRIMARY-01`. On the primary, the
two blocks are swapped. This is never stated, and it is a serious failure mode: a config
copied verbatim from one host to the other yields a tool that believes it is on the target
while running on the primary.

**Taken**: startup validation checks `node.hostname` against the machine's real name, with a
blocking, explicit error otherwise. Two sample files shipped, `ripcord.primary.yaml` and
`ripcord.dr.yaml`. Documented in milestone 1.

### S3 — `startup_ram_mb` duplicated between config and WMI — *settled*

All three VMs are declared at `2048` MB, which reads like filler. The same information is
readable through WMI (`Msvm_MemorySettingData.VirtualQuantity`), and that is the value that
is true on the day of the failover. A stale config value would produce a wrong feasibility
calculation — exactly the kind of reassuring false negative this tool must never produce.

**Taken**: effective RAM **always** comes from WMI. `startup_ram_mb` in config becomes
`expected_startup_ram_mb`, **optional**, and serves only as a drift rule ("this VM has been
resized since the configuration was written in calm conditions").

**To verify**: the real startup RAM of the three VMs. The feasibility calculation is only
meaningful with real values — 3 × 2 GB against 12 GB usable passes trivially, which does not
look like infrastructure that would warrant a feasibility calculator.

### S4 — Thumbprints are known, but config identifies by subject (CN) — *settled*

The context provides three precise thumbprints and an expiry date (September 2029). The
config only exposes `local_certificate_subject` / `peer_certificate_subject`. A CN lookup can
return several certificates, including an expired one — and pick the wrong one.

**Taken**: add `local_certificate_thumbprint` and `peer_certificate_thumbprint`. The
thumbprint identifies; the CN is still checked for consistency. Documented in milestone 2.

### S5 — The 48 GB → 12 GB usable gap is never qualified — *settled*

The primary has 48 GB, the target roughly 12 GB usable. If the VMs run with dynamic memory
and a high maximum on the primary, they will start on the target at their startup RAM and
then be squeezed. A failover that technically succeeds but leaves a domain controller
crawling is not a successful failover.

**Taken**: the feasibility report shows both startup and dynamic maximum per VM, and flags
the gap as a `warning`. Not a critical rule — it does boot — but the operator must know
beforehand, not during.

### S6 — No exit code table — *settled*

Milestone 2 requires a "non-zero exit code if a critical rule is violated", for use in a
scheduled task. Without a defined table there is no way to tell "critical rule" from "the
tool itself failed" — and a scheduled task that conflates the two is useless.

**Taken** (fixed at milestone 1, extended later):

| Code | Meaning |
|---|---|
| 0 | success, no critical finding |
| 1 | at least one critical rule violated |
| 2 | invalid or missing configuration |
| 3 | local access failure (WMI, privileges, timeout) |
| 4 | operation cancelled by the operator |
| 5 | failure during a mutating operation — intermediate state, see the audit log |

An unreachable peer does **not** produce an error code: it is a degraded state that gets
displayed, per the graceful degradation principle.

---

## Technical points to verify

### T1 — WMI equivalent of `Set-VMReplication -AsReplica` — *verify, milestone 4*

This is the command that justifies the project. The documented path is
`Msvm_ReplicationService.CreateReplicationRelationship` / `ModifyReplicationSettings` with an
`Msvm_ReplicationSettingData` in replica mode. The narrow open question is whether that API
can create a replica-side relationship with **no initiating primary** — which is precisely
the situation after an unplanned failover, and precisely why the cmdlet exists.

To be validated in the nested lab **before** writing the failback sequence. If the API cannot
do it, this is the one place where the "never `powershell.exe`" rule would deserve to be
reopened — and it would then be an explicit decision, not a quiet workaround.

### T2 — Nested virtualization on `HV-PRIMARY-01` — *verify*

The nested lab is described as what "makes the project serious", and it is planned on the
primary. Windows Server 2016 supports nested virtualization on **Intel VT-x only** (AMD
support arrives with Windows Server 2022). The primary's CPU is not documented.

If the primary is AMD, the lab has to move to `HV-REPLICA-01` (WS2022) — but its ~12 GB usable
cannot cover the 16 GB two lab hosts need with fixed memory. That would be a hardware
blocker for milestone 3, where the lab is first needed, worth surfacing early rather than discovering it late.

### T3 — Append-only audit log — *settled*

"Not modifiable from the application" is not achievable by application code alone: a process
that can write can rewrite. Windows offers `FILE_APPEND_DATA` without `FILE_WRITE_DATA`
through an ACL set at install time.

**Taken**: log file opened in strict append mode, append-only ACL documented as a manual
setup step, JSON Lines format (one line per operation, never re-read for rewriting).
Immutability comes from the ACL, not the application — and that is stated plainly.

### T4 — `ripcord version` with commit hash — *settled*

Planned and logged on every operation, but no mechanism is described. Taken: `SourceLink` plus
an `InformationalVersion` injected at build time, read by reflection. Wired at milestone 0
(free then, expensive to retrofit into the milestone 4 audit log).

---

## Minor points

- **Repository name**: the specification says `ripcord` (lowercase); the local folder and
  `RipCord.sln` say `RipCord`. Taken: solution `Ripcord.sln`, assemblies `Ripcord.*`, binary
  `ripcord.exe`, repository `ripcord`. The existing `RipCord.sln` gets replaced.
- **`LICENSE` missing** while MIT is announced. To add at milestone 0.
- **`.gitignore` missing**, and `.idea/` plus `.DS_Store` are already untracked in the tree.
  To add at milestone 0.
- **No commit in the repository**: `main` is empty. Milestone 0 produces the initial commit.
- **`VM-LEGACY-01`, ESU ending October 2026**: purely informational for the tool, but
  worth an `info` line in `ripcord check` output — the tool is the right place to surface a
  deadline people forget.
