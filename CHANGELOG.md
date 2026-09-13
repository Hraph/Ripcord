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
- `ripcord failover --scenario unplanned` — the disaster path. Three steps, all on the replica;
  nothing is asked of the host that is gone and replication is not reversed, because reversing
  needs a primary that is there to accept the new direction.
- `ripcord fence` — the first command to run on the original primary when it comes back. It
  records each failed-over VM's `AutomaticStartAction`, sets it to `Nothing`, and names any copy
  it could not confirm switched off. Without it, restoring that host's power boots the original
  domain controller beside the failed-over one, on the same switch and the same address.
- `ripcord failback` — the planned sequence pointed home, reversing replication exactly once.
- `--all` and `--priority P1` sweeps, in priority order, with `failover: auto | manual | never`
  per VM. `VM-BACKUP-01` is `manual`: it boots without its 4 TB repository and can fill the
  target volume the failed-over VMs are living on. Failing it over is still possible; it has to
  be named.
- An append-only JSON Lines audit trail (D8). What a run could not establish is written
  **before** the first mutation, because afterwards the host may not be writable.
- Split brain, version skew and `Stop-VMFailover` intent resolution, each of which halts a
  mutating command rather than reporting a finding to read later.
- A VM power state carried end to end, distinguishing "not reported" from "switched off".
- `ripcord check --notify` — notification over SMTP or webhook when a critical rule appears.
  On transitions rather than on every run, grouped into one message, with a repeat threshold and
  quiet hours that hold an alert until the window ends rather than dropping it. `--dry-run`
  shows what would go where. Delivery never changes the exit code, and what was sent is recorded
  only after a transport accepted it.
- An `alerting` block in `ripcord.yaml`, off by default. The SMTP password is named by
  `password_secret` and read from the environment; a `password:` key in the file is refused.
- `ripcord check-update` — reports that a newer release exists and nothing more. Off unless
  `updates.check` switches it on, and it addresses the repository by numeric id rather than by
  `owner/name`.

### Known limitations

- **A planned failover cannot yet cross from the primary to the replica.** The prepare leaves
  no state this binary can name (V35), so the replica cannot observe that the primary's half
  has run and refuses rather than assuming. It is pinned by a test rather than left silent.
- **`reprotect` is not implemented**, so after an unplanned failover the pair stays unprotected
  until it is put back by hand. The step it turns on —
  `Set-VMReplication -AsReplica -AllowedPrimaryServer` — is the one no GUI offers and no
  documentation settles (V3, V16); writing it blind would be guessing at the one operation whose
  failure mode is a pair that looks protected and is not.
- Every CIM name in the failover adapter is unverified on real hardware (V37), and the fence's
  own write is unverified too (V43).
