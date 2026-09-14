# Changelog

Ripcord follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

What "breaking" means here is narrower than for a library, and worth stating because it decides
the version number: a **major** bump is a change that makes an existing `ripcord.yaml` stop
loading, or that changes what an exit code means. Both are things a scheduled task or a
half-updated pair depends on.

A release is cut by tagging `vMAJOR.MINOR.PATCH`. Nothing else publishes a binary.

## Unreleased

### Changed

- **The changelog no longer gates a release.** It is used when it describes the version being
  released and skipped when it does not — with "no changelog entry was written for this
  version" on the release page in place of the section, rather than an empty one. Twice in one
  evening the gate stopped a release for a file that was behind the code, which is a cost paid
  every time to catch something a reader of the release page can see for themselves.

### Fixed

- **The listener service could never start.** `sc start` waits for the service control manager
  handshake, `ripcord serve` was a console loop that never answered it, and the manager gave up
  with 1053 every time — so `deploy-listener` has never produced a running listener on any
  host. Milestone 1b designed the fix and it was never built; it is built now, in the
  composition root only. Same binary, same verb, and a verb that returns on its own stops the
  service rather than leaving it reported as running with nothing behind it.
- `ripcord service restart` — the one move that follows every configuration edit, since the
  listener reads `ripcord.yaml` only when it starts. A service that is not running is started
  rather than restarted, because `sc stop` on a stopped service is an error and an operator
  asking for the configuration to take effect means the same thing either way. No typed
  confirmation: it is over in a second and changes nothing that outlives it, and a confirmation
  asked for that becomes the reflex the failover ones must not be.
- **`deploy-listener` is now `ripcord service`**, and bare it changes nothing: it says whether
  the service is installed, whether it is running and the command line it is registered with.
  `ripcord service install` and `ripcord service remove` are the two things that change it —
  words rather than flags, because both mutate a host that may be running a domain controller,
  and a word is harder to type by accident than a flag next to the one you meant. The old name
  is gone rather than aliased: it never produced a running listener on any host, so nothing can
  depend on it.
- `ripcord service` says what is on the host before what would change about it: whether
  the service is installed, whether it is **running**, the command line it is registered with,
  the firewall rule and the snapshot access. Installed and running are two facts — a registered
  service that is stopped serves nothing, and the other host reports the pair offline, which
  reads as a network fault rather than as a service somebody has to start. The running state is
  read from `Win32_Service.Started`, a boolean, rather than from `sc query`, whose words are in
  the language of the host.
- A service that stops because its own verb failed now exits with that code, and writes what
  happened to `listener.log` beside the binary. It exited 0 whatever it decided, so Windows
  could not tell a configuration that will never load from an operator stopping the service on
  purpose — no recovery policy fires on a clean stop — and a service has no console, so the
  reason went nowhere at all. Truncated at each start, so the file holds the run somebody is
  asking about.
- Creating the service and starting it are two steps of the deployment plan rather than one
  action doing both. A `sc create` that succeeded followed by a `sc start` that timed out used
  to report that nothing had been changed, on a host that then held a registered service.
- The plan sees a service that is installed, correct and **stopped**, and starts it. It only
  compared paths before, so re-running the command on that host did nothing while the other
  side reported the pair offline — which reads as a network fault rather than as a service
  somebody has to start.
- Starting the service is the last step, after the firewall rule and after the access it needs
  to the snapshot it serves. Starting it first brings up a listener that cannot read its own
  file.

## 0.2.1 — 2026-09-14

### Fixed

- `status` and `check` say one line when a newer release has been found — by `check-update`,
  which now writes what it saw beside the binary. No command makes a network call of its own:
  on hosts meant to have no outbound access, checking at startup would put a fifteen-second
  timeout in front of every command, including the ones typed during an outage.

- **`install.ps1` could not be parsed, so the one-liner in the README died at load.** An
  operator warning added in 0.2.0 was written with its `+` at the start of a continuation
  line; PowerShell ends a statement at the newline unless the line *ends* with the operator, so
  the expression never closed and the file failed before a single function was defined.
  `irm … | iex` reported only `Missing closing ')' in expression`. This is the second parse
  defect in a file no machine here can execute, and the CI job that runs the installer under
  both PowerShell versions caught it in fourteen seconds — which is what it is for.
- The version is derived in one place. `VersionPrefix` was a second name for the release, kept
  by hand beside the one the workflow works out from the commits, and the two disagreed
  silently: on Windows, environment variables are case-insensitive, so the job's `VERSION`
  was picked up by MSBuild as its own `Version` property and every build in that job was
  stamped with a version the tree did not declare.

## 0.2.0 — 2026-09-14

### Added

- `install.ps1` leaves a `ripcord.yaml` behind on a host that had none and no sample to copy
  from — and writes it so that it **does not validate**. Every field the machine can answer is
  filled in; every field only the operator can answer is blank, and the validator refuses each
  by name. A default that loaded cleanly would describe a pair that does not exist, and
  `ripcord status` would then answer confidently about the wrong peer.

### Fixed

- **`ripcord update` could not download a release, on any host.** The address was built as
  `github.com/repositories/{id}/releases/download/…`, and that is not a route: the numeric
  repository id is an API path and has never been a web one, so every download answered 404.
  The API is asked instead, by id, for the release carrying the tag, and each file is then
  fetched by its own asset id — so no address arriving in a response body is followed, and the
  repository name is never typed at all. It failed safely, as a refusal rather than a wrong
  binary, but it could not have worked. `install.ps1` had the same address and the same fix.
- **`install.ps1` died before its first line when run the way the README says to run it.**
  `irm … | iex` binds the param block in the caller's scope, where `$Role` takes its empty
  default and a `[ValidateSet]` that does not admit the empty string cannot be attached at all.
  Twenty-one green tests and a green CI job said nothing about it, because every one of them
  dot-sources the script rather than piping it.

### Changed

- Every CI and release job is bounded by `timeout-minutes`. There were none, so a step that
  stopped to ask a question on a runner with no keyboard would have waited six hours before
  anybody was told — including on the workflow that ends by publishing a binary two hosts are
  pointed at.
- The version is derived in one place and nowhere else. `Directory.Build.props` no longer
  carries a release number to keep in step by hand — it says `0.0.0`, which is what a working
  copy honestly is — and the released version comes from the tags and the conventional
  commits. The workflow then runs the binary it just built and refuses to sign one that does
  not report the version being released, which is a stronger check than the file comparison it
  replaces, and an automatic one.
- The Windows PowerShell 5.1 installer job no longer spends minutes drawing a progress bar
  nobody can see, trusts the gallery before installing from it so nothing can stop to ask, and
  says which Pester actually loaded: 5.1 ships 3.4.0 in `System32` and it wins load order more
  often than anyone expects.

### Notes

- The names the release workflow publishes are now compared against the names a host goes
  looking for. A rename there would have broken `ripcord update` and `install.ps1` together,
  silently, and only at the next release.

## 0.1.0 — 2026-09-13

### Added

- `install.ps1` — the first install, in one line:
  `irm https://raw.githubusercontent.com/Hraph/Ripcord/main/install.ps1 | iex`. It checks the
  SHA-256 and verifies the release signature against a key written into the script before
  anything lands, and installs nothing if either fails. `-Prepare` and `-FromPath` split that
  in two for a host with no outbound access. It refuses a host that already has a binary —
  that host wants `ripcord update`. **Two defects in this version are fixed in 0.2.0**, and one
  of them stops it running at all; the one-liner is fetched from `main` rather than from a
  release, so it picks the fix up on its own.
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
- `install.ps1` — the first install, in one line:
  `irm https://raw.githubusercontent.com/Hraph/Ripcord/main/install.ps1 | iex`. It checks the
  SHA-256 and verifies the release signature against a key written into the script before
  anything lands, and installs nothing if either fails. `-Prepare` and `-FromPath` split that
  in two for a host with no outbound access: download and verify on a machine that has some,
  copy the folder, verify again on the host. It refuses a host that already has a binary —
  that host wants `ripcord update`.
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

- A deployment that fails part-way exits **5** (the host is between two states) rather than 3,
  and says "nothing was changed" instead when the first step failed. A declined confirmation
  exits **4**, like every other declined confirmation.
- The peer's address is compared as an address, so a peer arriving over IPv6 from its own
  address is still the peer.
- **Ripcord can now update itself, which earlier releases said it never would.** `RELEASING.md`,
  `RELEASE_NOTES.md`, `SECURITY.md` and milestone 5 are rewritten rather than left contradicting
  the code. The objection that produced that rule has not gone away; what changed is that the
  install path now verifies a signature against a pinned key instead of trusting the release
  page. The signing key lives in the release workflow's secrets, so an account with write access
  to the repository can still sign — stated in `SECURITY.md` rather than implied.

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

### Notes

- `ripcord dashboard` was gated on an evaluation of Windows Admin Center, which was carried
  out before any of it was written. WAC is supported and its Virtualization Mode does cover
  Hyper-V Replica, but it is still public preview, it assumes live connectivity to both hosts,
  and it has no equivalent of the pre-failover check — so it does not answer for a pair whose
  peer is down, which is the case this tool exists for.
