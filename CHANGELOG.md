# Changelog

Ripcord follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

What "breaking" means here is narrower than for a library, and worth stating because it decides
the version number: a **major** bump is a change that makes an existing `ripcord.yaml` stop
loading, or that changes what an exit code means. Both are things a scheduled task or a
half-updated pair depends on.

A release is cut by tagging `vMAJOR.MINOR.PATCH`. Nothing else publishes a binary.

## 0.9.0 — 2026-09-26

### Changed

- **`ripcord update` shows its progress on one line**, redrawn in place: the step running,
  with the download's percentage (megabytes when the server gives no size), erased once the
  result is printed. Redirected to a file, nothing is drawn. The closing `done:` list is gone,
  and a failed step's description no longer appears twice in the failure message.
- **services.msc names and describes both services**, as *Ripcord listener* and *Ripcord
  publisher*, each with one line on what it does and what stopping it costs. A host installed
  before this gets them on its next `service install`.

## 0.8.0 — 2026-09-26

### Added

- **A second service, `ripcord-publish`, republishes this host's snapshot every 15 seconds.**
  The other host's view no longer ages until somebody runs `ripcord status` here. The
  network-facing listener still reads nothing of Hyper-V; the publisher, which has no socket,
  runs as `NT SERVICE\ripcord-publish` in Hyper-V Administrators, with modify on `state\` and
  on its own `logs\publish\` only, and one ACE on the BitLocker namespace while
  `storage.check_bitlocker_autounlock` is on. BitLocker it still cannot read is carried from
  the last administrator's read, dated. `ripcord publish` does it once by hand.
- **`service install` and `service remove` handle both services**; `restart`, `start` and
  `stop` act on both, and `ripcord service` shows the publisher with the last lines of its log.
  Install also restarts a service still running the build from before `ripcord update`, sets
  both to restart a minute after a crash, and narrows the listener's 0.7.0 read on the install
  folder to its own files.

**Upgrading a host**: `ripcord update`, then `ripcord service install --dry-run` and
`ripcord service install`. It creates the publisher and `state\`, restarts the listener onto
the new binary and `state\state.json`, and starts the publisher. If a stale snapshot remains,
`ripcord service` names the one command that fixes it. The old `state.json` beside
`ripcord.yaml` can be deleted.

### Changed

- **The snapshot moves to `state\state.json` beside `ripcord.yaml`, and is no longer
  configurable.** A folder of its own, so nothing that writes it is ever granted anything
  beside `ripcord.exe`. A `listener.snapshot_path` line still loads, is ignored, and every
  command says so. See **Upgrading a host** above.
- **`service install` grants the listener read access to `ripcord.yaml` explicitly**, on the
  files of the install folder only. It used to come from the snapshot grant on that folder.

### Fixed

- **The peer's lag grew with its snapshot's age.** A snapshot 7 minutes old showed 7m19s of
  lag on a replication 17 s behind: the lag was measured to now instead of to when the
  snapshot was taken. `status` and the dashboard now measure it at the snapshot.

- **`ripcord service install` takes back access nothing uses any more.** The service account
  kept read access to the private key of every certificate replaced by `pair` or a renewal,
  and to the old install folder after the binary moved. Install now revokes them, last, after
  everything the listener needs; with the configured key not found it touches no key.

## 0.7.0 — 2026-09-26

### Changed

- **Two confirmation levels.** Only what moves production VMs — `failover`, `failback`,
  `fence` — still asks for the node name typed in full. `service install`, `remove` and `stop`,
  `test-failover`, `update` and `rollback` ask `y/n [n]` instead, Enter declining: each is
  undone by running it again or touches only a test VM, and a typed name asked for every
  change becomes the reflex the failover confirmation must never be.

### Added

- **`ripcord service` shows this host's certificate**: the thumbprint of each unexpired
  certificate whose CN is this host's, with a private key in `LocalMachine\My`, marked when it
  is the one in `ripcord.yaml`, even when that file does not load — with the one line to run
  on the other host.
- **`ripcord pair <host>:<thumbprint>`** writes both certificate thumbprints into
  `ripcord.yaml` from that line: this host's own is found in `LocalMachine\My`, the other's is
  the one pasted. Two lines change, the previous file is kept as `.1`, `y/n`, `--dry-run`.
  A key pasted on the host it came from, or naming another host than `peer.hostname`, is
  refused. No more editing the thumbprints by hand.
- **`ripcord status` says the listener is running**, in one `LISTENER` line with the build
  its process runs, instead of leaving a missing block to mean it. After an update without a
  restart, a second line names `ripcord service restart`.
- **`ripcord service start` and `ripcord service stop`.** `start` starts a stopped listener
  without confirmation and leaves a running one alone. `stop` asks `y/n` — the
  other host cannot read this one until the listener starts again — and waits for Windows to
  report it stopped.

- **`ripcord service` shows the build the running listener runs**, from a record the listener
  writes when it starts (`logs\listener-process.txt`), believed only when its process id is
  the one Windows gives. After `ripcord update` without a restart it says the process still
  runs the old build and ends with `ripcord service restart`. A listener started before this
  version has no record: `unknown` until it restarts.

### Changed

- **The samples' placeholder thumbprints are refused by name.** A `listener` block still
  carrying `AAAA1111…` or `1111AAAA…` used to load, and the pair channel then failed as a
  `SILENT` peer, which reads as a network fault. **A `ripcord.yaml` whose listener is enabled
  with them no longer loads**: put the thumbprints of the two hosts' certificates, or set
  `enabled: false`, behind which they are ignored.

### Fixed

- **A refusal no longer blames Hyper-V when Hyper-V was not involved.** Every failure line
  began "cannot read the local Hyper-V state", including `check-update` refusing because
  checking was off. Only a failed Hyper-V read says so now.
- **`updates.check` and `updates.install` refusals say how to switch them on**:
  `set updates.check: true in ripcord.yaml`, and likewise for install.
- **Failure and refusal lines wrap at 75 columns**, so a reason from Windows or the network
  no longer runs off a 1024×768 console.

## 0.6.0 — 2026-09-25

### Added

- **`ripcord status` ends with a `LISTENER` block when the other host cannot read this one**:
  the service is not installed, not running, or could not be read,
  with the command to run next. Over there this host only shows `SILENT`, which reads as a
  network fault. The exit code is unchanged.
- **`ripcord init` asks whether to check GitHub for a newer release**, last, defaulting to
  no, and writes `updates.check` either way. `updates.install` is still never asked: a re-run
  keeps it, unless the check is answered no — install cannot be on without it. The samples'
  commented-out `# updates:` example is dropped from the rewritten file.

### Fixed

- **Each `ripcord init` re-run added a blank line between the sections it carries over**
  (`listener`, `checks`, …). A second re-run now gives the file back unchanged.

## 0.3.0 – 0.5.0 — 2026-09-14 to 2026-09-25

These three were released without a section of their own; everything below shipped in one of them.

### Added

- **`ripcord service` says why a stopped listener stopped**: the command Windows runs, its
  start mode, the exit code Windows recorded (`0x2000000N` is Ripcord's exit N) with what it
  means, the day's `logs\listener-YYYY-MM-DD.log` and its last 20 lines, and one line on the
  likely cause with the command to run next — `ripcord check`, `service install --dry-run`, or
  the two `Get-WinEvent` commands when it died before it could log anything.
  `ripcord service status` is the same report; there is no second one.

### Changed

- **`vms: []` is accepted.** `ripcord init` writes it on a host with no VM, and `status` and
  `check` then say no VM is declared. A file with no `vms` key is still refused.

- **`ripcord service` shows the service even when `ripcord.yaml` does not load** — the
  likeliest reason the listener stopped. The firewall, snapshot and logs rows need the
  configuration and are left out; its errors follow, and the exit code is still 2.

- **The snapshot defaults beside `ripcord.yaml`, not to `D:\Ripcord\state.json`.** A host
  without a `D:` volume was being deployed onto a path Windows could not even grant access to.
  **On a pair that never set `listener.snapshot_path`, the snapshot moves**: the listener
  serves nothing until the next `ripcord status` writes one at the new location, and the peer
  reads that host as stale in the meantime. After updating such a host, run `ripcord status`
  (writes the snapshot at the new place), `ripcord service install` (grants the service
  account access to its folder) and `ripcord service restart` — the running listener read the
  old path when it started, and `update` does not restart it. `D:\Ripcord\state.json` can be
  deleted afterwards. Or set `snapshot_path` explicitly first to keep the old location.
- **`ripcord service` names the snapshot file and says what it is**: the snapshot
  `ripcord status` writes and the listener serves to the peer. The sample configurations say
  the same and no longer show a `D:` path.
- **The diagnostic log is one file a day in a `logs` folder beside the binary, kept 30
  days**: `logs\ripcord-YYYY-MM-DD.log` for commands and `logs\listener-YYYY-MM-DD.log` for
  the listener service, by UTC date. Older files are deleted on the first write of a day; only
  files named that way are ever touched. `max_size_mb` now caps each day's file, with one `.1`
  file past it. `diagnostics.path` names a folder — an old value ending in `.log` is read as
  the folder holding it — and the listener service ignores it, writing only to the folder
  `service install` grants it. The old `ripcord.log`, `ripcord.log.1` and `listener.log` are
  left where they are and never deleted; remove them by hand.
- A `listener.snapshot_path` naming no folder — `state.json` — is now refused. It resolved
  against the working directory of whoever ran the command, and a Windows service's is
  `system32`.

### Fixed

- **`service install` and `restart` reported success for a listener that stopped at once.**
  `sc start` returns when the process answers Windows, before the listener reads its
  configuration or opens its log. The start step now watches the service for five seconds and
  fails, with the exit code Windows recorded, if it stops in that time.
- **`ripcord service` believed an older run's log over Windows' newer exit code.** A start
  that fails before writing its start line leaves the log as an earlier run left it; a clean
  stop logged yesterday no longer hides today's failed start. The verdict then says the log is
  from an earlier run. 1077 now reads *not started since Windows booted*, and codes Windows
  set read *Windows recorded ...* rather than *Windows stopped it*.
- **`listener.enabled: false` made `ripcord service`, `restart` and `remove` refuse the
  configuration.** `service` now reports and exits 0 with its own verdict, `restart` refuses
  and names the way out, and `remove` works. `install` is refused before anything changes.
- **`ripcord service` shows how old the snapshot is**, or that it was never written, and ends
  by naming `ripcord status` when the peer would read this host as offline or stale.
- **The test failover's cleanup destroyed the replica, not the test copy.** It named the
  replicated VM, and the adapter destroyed whatever VM carried that name. Test VMs are now
  destroyed by their own name, and the adapter refuses any VM that is not a test replica. A
  create that fails after making the copy is cleaned up by listing the test VMs before and
  after it. Unverified on hardware.
- **The created test VM was read from a reference that carries key properties only**, so its
  name came back empty and the copy was left behind; it is re-read. The test network is set by
  the switch's name rather than its GUID and read back, and a copy that is not on the declared
  test switch is destroyed without being started. Unverified on hardware.
- **A planned failover prepared while the guest was still shutting down**, which Hyper-V
  refuses. Step 1 now waits for the VM to read Off, up to ten minutes, and the restore after a
  failed step is retried fifteen seconds apart instead of back to back. A shutdown that does
  not finish hands the operator the way on or back rather than saying nothing was done.
- **The listener service could not use its own certificate's private key**: machine keys are
  readable by SYSTEM and Administrators only. `service install` now grants the service account
  read access to the key file, `ripcord service` shows it, `remove` revokes it, and the
  listener logs that refusal as its own rather than as a caller with no certificate.
  Unverified on hardware.
- **A second `update`, or a `rollback`, failed with a bare access-denied** while the listener
  still ran the binary the first update set aside. Both now refuse first and name
  `ripcord service restart`.
- A VM name holding a backslash or a quote no longer breaks the Hyper-V lookup; the listener
  reads the snapshot without blocking the replace `ripcord status` makes.
- **Re-running `ripcord init` over the sample lost the comments explaining the file.** An
  indented comment closing a section was attached to the next one: the `# snapshot_path:`
  explanation left `listener` and was dropped with `vms`, and `replication`'s commented keys
  landed under `vms`. The commented-out `alerting` to `diagnostics` examples were dropped with
  `storage`. Indented comments now stay in their section and are written back after a
  rewritten one, and the comment block after the last section stays at the end.
- **`ripcord init` wrote a file every command refused when a dropped VM was acknowledged in
  `checks`.** The acknowledgement now goes with the VM and is named before the yes. One it
  cannot take out makes `n` the default. After a re-run carrying an enabled listener, the
  closing lines say to restart the service.
- `ripcord service` reads the service once per report, heads its last line *Note* rather than
  *Why it is not running* when the state is not stopped, and says to use an elevated console
  on access denied. The result lines of `install`, `remove` and `restart`, and the refusal of
  an unknown `service` word, fit 75 columns.

- **The pending replication size was never read on a real host.** The relationship was passed
  to `GetReplicationStatisticsEx` as an object where Hyper-V expects its text form, and every
  `status` logged a type mismatch for each VM. It is now serialised the way Hyper-V's own
  samples do, and the statistics are read whether they come back as an object or as text; a
  non-zero return or an unexpected shape is logged. Unverified on hardware.

- **Fencing and the test failover's network isolation passed their settings the same way.**
  `ModifySystemSettings` and `ModifyResourceSettings` now receive the settings' text form too.
  Both failed loudly rather than silently; unverified on hardware.

- **`ripcord init` explains what P1/P2 and the domain-controller answer change**, once, above
  the first VM: P1 fails over first and `check` makes sure the DR host can start every P1 at
  once; the domain-controller answer only adds `check`'s USN rollback reminder, so a domain
  controller should be P1. The written `ripcord.yaml` no longer claims a P1 running on both
  hosts is special — split brain halts mutation for a VM of either priority.

- **`ripcord init` pre-answers the VM question with the file's VMs as list numbers**, in its
  order, instead of a line of names that ran off the console. Enter reproduces the selection,
  names are still accepted, and a VM name containing a comma now survives being picked.

- **`ripcord init` offered VMs this host does not have** — the sample's `VM-DC-01`,
  `VM-LEGACY-01` and `VM-BACKUP-01` among them — and kept them on Enter. It now offers only
  what Hyper-V reports, never a test-failover copy, and names each VM of the previous file it
  drops. An acknowledgement in `checks` naming a dropped VM is listed before the write.
- **`ripcord init` crashed on a host with no VM.**

- **`service install` with a `snapshot_path` on a drive this host lacks is refused before
  anything changes**, and says when it is the old `D:\Ripcord\state.json` default. It used to
  create the service and the firewall rule, then fail granting access to the snapshot folder.
- **The listener service died before it answered Windows, so `sc start` failed with 1053
  and nothing anywhere said why.** It opened `listener.log` beside the binary before the
  handshake, and the service account cannot write to `C:\Program Files\Ripcord`. The log is
  now opened after the handshake, in a `logs` folder `service install` grants the account
  modify access to — that folder only, never the install folder or the binary. When the file
  still cannot be opened, the service reports why in the Application event log and stops.
- **The listener service's own diagnostics and its served/refused connection lines were
  lost.** The first went to `ripcord.log` in the install folder, which the service cannot
  write; the second to a console a service does not have. Both now go to the day's
  `logs\listener-YYYY-MM-DD.log`, after a start line naming the version and the configuration.
- **`service install` whose start fails says to run `ripcord service`**, and `ripcord service`
  now says whether the service account can write its logs folder.
- **The listener was granted access to the snapshot *file*, which survived one write.**
  `ripcord status` rewrites the snapshot by moving a new file over the old one, and a move
  carries the new file's access list with it — so the entry set at deployment was gone after
  the first publish and the peer stopped being served. Access is granted on the folder now,
  inheritable, and the revoke reaches what the grant reached.
- **`ripcord update` now writes down the release it looked up.** It asked the feed and threw
  the answer away, so a `--dry-run` paid for the network call and left `status` and `check`
  unable to mention the release — until somebody also ran `check-update`, for a fact the tool
  already had. Both commands write it now, including when the plan refuses because
  `updates.install` is off.

### Added

- **`ripcord rollback`** — puts back the binary the last update set aside. It fetches nothing
  and verifies nothing: these hosts are meant to have no outbound access, so a retreat that
  needs the network fails on the day it is needed, and the bytes being restored were running
  here before the update. One generation back, an exchange rather than a replacement (a
  running binary can be renamed and never deleted), and neither `updates.check` nor
  `updates.install` gates it. See `docs/commands/rollback.md`.

- **Colour in the rendered blocks** — severities, the replication health column, `STALE`, an
  unreachable peer's reason, the section headings and the `init` interview. It is switched on
  only where the console has been asked and said yes: redirected output, the listener service,
  `NO_COLOR` and `--no-color` all get exactly the text they got before. Nothing is said in
  colour alone, and the fixed 75-column layout is unchanged — the renderers emit markers and
  the escapes are substituted once, on the way out.

- **`ripcord init`** — an interview that writes this host's `ripcord.yaml`. It reads the host's
  switches and VMs and offers them rather than expecting them from memory, refuses any answer
  the file would not take, and what it produces **validates**: `ripcord status` works straight
  afterwards. Running it again is an edit, not a restart — every question arrives answered with
  what the file says, every section it does not ask about is carried across as the original
  lines with its comments, and the previous file is kept as `ripcord.yaml.1`.
- **A missing configuration is no longer reported as a broken one.** A host with a binary and
  no `ripcord.yaml` was told `cannot read '<path>': Could not find file '<path>'`, with the
  path in it three times and no next step. It now names the path once and the command that
  creates one.
- **A diagnostic log**, `ripcord.log` beside the binary, on by default and configurable under
  `diagnostics` in `ripcord.yaml`. Every command records what it ran and what it exited with;
  every failure the console only had room for one sentence about records the exception behind
  it — type, message and stack. It is written to be sent: URLs are cut down to their host and
  anything whose key names a password or a token is removed. It refuses nothing and rotates
  once, because a log that stops a command has become the outage. See `docs/diagnostics.md`.

### Fixed

- **`ripcord status` failed outright when one optional column could not be read.** The pending
  replication size comes from `GetReplicationStatisticsEx`, whose embedded-instance parameter
  was written blind against the MOF; a host that refuses it was left reporting nothing at all
  about a pair that was replicating perfectly well. The column now degrades to unknown — as it
  already did for a VM with no relationship — and the CIM error goes to the log.

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
- A service that stops because its own verb failed now reports that code to Windows, and
  writes what happened to `logs\listener-YYYY-MM-DD.log`. It reported 0 whatever it decided, so
  Windows could not tell a configuration that will never load from an operator stopping the
  service on purpose, and a service has no console, so the reason went nowhere at all. A .NET
  service can only set the Win32 exit code, so Ripcord's code N is carried as `0x2000000N` —
  the bit Windows reserves for applications, so 2 is never mistaken for "file not found".
  Appended, with a banner at each start naming the version and the configuration.
- `service install` has two more steps: create `logs` beside the binary and grant
  `NT SERVICE\ripcord` modify access to it, and register the `ripcord` source in the
  Application event log. `service remove` undoes both and leaves the logs in place.
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
