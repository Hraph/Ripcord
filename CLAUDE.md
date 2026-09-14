# Ripcord — project instructions

Disaster recovery tool for a Hyper-V Replica host pair.
Source specification: `.claude/ripcord-prompt.md` (reference; do not change without approval).
Progress tracking: `docs/TRACKING.md`. Command reference: `docs/commands/`.

## Language

**All project artifacts are written in English** — code, identifiers, comments, commit
messages, documentation, Markdown files, console output, and error messages. The source
specification is in French; it is the only exception.

## Design criterion

The handle of a reserve parachute: simple, rarely pulled, pulled under pressure, and not
allowed to fail. Settle every decision against that.

## Stack

- .NET 10 (LTS), C#, single self-contained `win-x64` binary
- Hexagonal architecture — `Ripcord.Domain` references **nothing**
- Native WMI (`Microsoft.Management.Infrastructure`), **never** a call to `powershell.exe`
- xUnit, tests run in a Linux container

## Non-negotiable rules

1. **One milestone at a time.** Ship it, use it for real, validate it, then move on.
   No speculative code: if a later milestone is not reached, its code does not exist.
2. **Logic lives in the Domain.** Adapters translate, they do not decide.
   If adapter code starts to need a test, that code belongs in the Domain.
3. **Read-only by default.** No mutating operation without explicit typed confirmation.
4. **`--dry-run` on every mutating operation** — that output is what gets read on the day.
5. **Graceful degradation.** Unreachable peer or WMI failure: show a clear state,
   never crash, never stay silent.
6. **Console output legible on a 1024×768 KVM.** That is the real usage context.
7. **No secrets in plaintext** in config or logs. Explicit timeouts everywhere.
8. The hexagonal boundary is guarded by `HexagonalBoundaryTests`, which parses every
   `.csproj` and asserts the reference matrix. The Linux build is only a secondary check
   (`NU1201`, if the Domain ever targets Windows) — the Windows projects compile off Windows,
   so the build alone proves nothing. Treat a change to that test as an architecture change.

## Development workflow

### Commits

- **Conventional Commits are mandatory**: `type(scope): description`.
  Types: `feat`, `fix`, `docs`, `refactor`, `test`, `chore`, `ci`, `perf`, `build`.
  Usual scopes: `domain`, `ports`, `application`, `cli`, `wmi`, `fake`, `config`, `tests`, `ci`, `docs`.
  **The description starts with a capital letter.**
  Example: `feat(domain): Add lag computation for replication state`
- **No reference to Claude or Anthropic** anywhere — commit messages, pull request
  descriptions, code comments, documentation. No `Co-Authored-By: Claude`, no
  `Claude-Session:` line, no "Generated with Claude Code" footer.
- **One-line commit messages.** Subject line only, no body, no bullet list. Keep it terse
  and factual — say what changed, not why at length. Under ~72 characters where possible.
- Atomic commits: one logical change each. Committing does not need to be asked for —
  commit a unit of work once it stands on its own.
- **Never push.** Commit locally and stop there. Pushing is the user's call, always — including
  after a commit made without being asked. The same goes for creating pull requests, tags
  and releases.

### Comments

Same discipline as commits: short, one line, only where the code cannot say it itself.
Comment the non-obvious — a WMI quirk, a sequence constraint, a deliberate trade-off.
No block comments restating the code, no XML doc ceremony on self-evident members,
no section banners.

### Mandatory review pass

**At the end of every unit of work, launch a review/refactoring subagent** before
considering the task done. This step is not optional.

The subagent checks:
- the hexagonal boundary holds (no adapter leaking into the Domain)
- test coverage of the decision logic that was added
- simplification opportunities and duplication worth factoring out
- compliance with the non-negotiable rules above
- no code anticipating an unreached milestone
- no Claude/Anthropic reference, and no French left in code or docs

Act on its findings, then report.

## Testing

| Level | Where | Validates |
|---|---|---|
| Unit + sequences | Docker Linux, CI | the logic is correct |
| WMI adapter | Windows Server VM, manual | the CIM mapping is correct |
| End to end | Nested lab | failover actually works |
| Real | Monthly test failover | production infrastructure works |

Every piece of decision logic must be testable with `FakeHypervProvider`, without Windows.

## Developing on macOS

Primary development happens on macOS, which shapes what can and cannot be verified locally.

- **Runs locally**: everything in `Ripcord.Linux.slnf` — Domain, Ports, Application, Cli,
  Adapters.Fake, Tests. Same target as the CI and the Docker image, so a green local run is a
  green CI run. Full failover sequences are exercised here against `FakeHypervProvider`.
- **Cannot run locally, ever**: `WmiHypervProvider`. There is no `root\virtualization\v2` on
  macOS and no WMI emulator. The fake substitutes the *port*, not WMI — the adapter itself is
  never exercised off Windows.
- **Compiles locally**: the Windows projects build off Windows with no special flag —
  `Microsoft.Management.Infrastructure` restores fine and a plain `net10.0-windows` target
  needs no targeting pack. `dotnet build Ripcord.sln -c Release` compiles everything.
  `-p:EnableWindowsTargeting=true` is harmless and stays documented in case a future
  `net10.0-windows10.x` bump needs it. Never add it to the CI test run.

`HexagonalBoundaryTests` is the guard. It asserts project references exactly, and packages as a
subset of an allow-list per project — so adding a package means widening that list on purpose.
Every milestone that introduces a project or a dependency has to extend the matrix; that is the
intended friction, not an obstacle to route around.

**Consequence**: `WmiHypervProvider` is written blind and validated on real hardware. That is
what turns "the adapter is thin and dumb" from architectural hygiene into an operational
constraint — any decision that creeps into the adapter is a decision that cannot be tested
until someone is on Windows. When in doubt, push it down into the Domain.
