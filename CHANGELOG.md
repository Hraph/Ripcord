# Changelog

Ripcord follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

What "breaking" means here is narrower than for a library, and worth stating because it decides
the version number: a **major** bump is a change that makes an existing `ripcord.yaml` stop
loading, or that changes what an exit code means. Both are things a scheduled task or a
half-updated pair depends on.

A release is cut by tagging `vMAJOR.MINOR.PATCH`. Nothing else publishes a binary.

## Unreleased

### Added

- `ripcord failover --scenario planned --vm <name> [--dry-run]` — carries out this host's half
  of a planned failover and names the host that continues it.
- An append-only JSON Lines audit trail (D8). What a run could not establish is written
  **before** the first mutation, because afterwards the host may not be writable.
- Split brain, version skew and `Stop-VMFailover` intent resolution, each of which halts a
  mutating command rather than reporting a finding to read later.
- A VM power state carried end to end, distinguishing "not reported" from "switched off".

### Known limitations

- **A planned failover cannot yet cross from the primary to the replica.** The prepare leaves
  no state this binary can name (V35), so the replica cannot observe that the primary's half
  has run and refuses rather than assuming. It is pinned by a test rather than left silent.
- `--scenario unplanned`, `failback` and `reprotect` are not implemented and are refused by
  name rather than treated as `planned`.
- Every CIM name in the failover adapter is unverified on real hardware (V37).
