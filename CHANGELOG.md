# Changelog

Ripcord follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

What "breaking" means here is narrower than for a library, and worth stating because it decides
the version number: a **major** bump is a change that makes an existing `ripcord.yaml` stop
loading, or that changes what an exit code means. Both are things a scheduled task or a
half-updated pair depends on.

A release is cut by tagging `vMAJOR.MINOR.PATCH`. Nothing else publishes a binary.

## Unreleased

### Added

- `install.ps1` — the first install, in one line:
  `irm https://raw.githubusercontent.com/Hraph/Ripcord/main/install.ps1 | iex`. It checks the
  SHA-256 and verifies the release signature against a key written into the script before
  anything lands, and installs nothing if either fails. `-Prepare` and `-FromPath` split that
  in two for a host with no outbound access: download and verify on a machine that has some,
  copy the folder, verify again on the host. It refuses a host that already has a binary —
  that host wants `ripcord update`.

### Security

- Anything a host names — a VM, a switch, an adapter, the other host itself — is stripped of
  control characters before it reaches a console or a mail header. A name carrying an ANSI
  escape could otherwise repaint the verdict an operator is reading during an incident.
- SMTP credentials on a connection that never starts TLS are refused by the configuration
  validator rather than sent in the clear.
- A mail the framework refuses to build is a failed delivery, not an exception out of
  `ripcord check`.
- The release workflow no longer substitutes its inputs into a shell script, and its token is
  read-only except on the job that publishes. Third-party actions are pinned to a commit.
- `SECURITY.md` states how to report a vulnerability and what the tool assumes.

### Changed

- A deployment that fails part-way exits **5** (the host is between two states) rather than 3,
  and says "nothing was changed" instead when the first step failed. A declined confirmation
  exits **4**, like every other declined confirmation.
- The peer's address is compared as an address, so a peer arriving over IPv6 from its own
  address is still the peer.

### Added

- `ripcord dashboard` — the read-only page of milestone 7, served on `127.0.0.1` only and off
  unless `dashboard.enabled` says otherwise. It runs the same `check` the console does and lays
  its answer out in a browser: one reading per refresh, taken from the pair the findings were
  judged from. One self-contained document, no script and nothing fetched from anywhere, so it
  renders on a host with no outbound access. A reading that failed becomes a page saying so.
- `dashboard.port` and `dashboard.refresh_sec` in `ripcord.yaml`. There is deliberately no
  address key: the page is bound to the loopback interface by construction.

- `ripcord update` — installs a newer published release on the host it is run on. Off unless
  `updates.install` says so, which is a second switch beside `updates.check`: permission to look
  is not permission to replace the binary this host runs its failovers with. It asks for the
  node name, `--dry-run` prints the plan and stops, and it is never unattended.

  It refuses any release whose detached ECDSA signature does not verify against a public key
  compiled into the running binary — absent, malformed, signed by anybody else, or a host
  carrying no key at all are all refusals, taken before anything on the host is moved. The
  running binary is then set aside and kept; if the last move fails it goes back, and only if
  that fails too does the command exit 5 and print the renames to type.
- `updates.install` in `ripcord.yaml`. `install` without `check` is refused by the validator.
- Releases now carry `ripcord.exe.sig` beside the `.exe` and the `.sha256`.

### Changed

- **Ripcord can now update itself, which earlier releases said it never would.** `RELEASING.md`,
  `RELEASE_NOTES.md`, `SECURITY.md` and milestone 5 are rewritten rather than left contradicting
  the code. The objection that produced that rule has not gone away; what changed is that the
  install path now verifies a signature against a pinned key instead of trusting the release
  page. The signing key lives in the release workflow's secrets, so an account with write access
  to the repository can still sign — stated in `SECURITY.md` rather than implied.

### Notes

- Milestone 7 was gated on an evaluation of Windows Admin Center, recorded in
  `docs/milestones/milestone-7.md`. WAC is supported and its Virtualization Mode does cover
  Hyper-V Replica, but it is still public preview, it assumes live connectivity to both hosts,
  and it has no equivalent of the pre-failover check. The milestone proceeds.

## 0.1.0 — 2026-09-13

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
