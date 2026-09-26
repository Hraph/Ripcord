# `ripcord service`

The two Ripcord services — the listener that serves this host's snapshot to the other one, and
the publisher that keeps that snapshot current — what they are doing, and the things that
change them.

```
ripcord service [status]            is it running, and if not, why
ripcord service install [--dry-run]
ripcord service remove  [--dry-run]
ripcord service restart [--dry-run]
ripcord service start   [--dry-run]
ripcord service stop    [--dry-run]
```

Bare, it changes nothing. `ripcord service status` is the same report under the word an
operator types by habit — one report, two spellings. `install`, `remove`, `restart`, `start`
and `stop` are words rather than flags, because each mutates a host that may be running a
domain controller, and a word is harder to type by accident than a flag next to the one you
meant.

## `ripcord service` (or `ripcord service status`)

On a healthy host:

```
RIPCORD SERVICES

  LISTENER
    service    running      start mode Auto
    command    "C:\Program Files\Ripcord\ripcord.exe" serve
    version    0.8.0+def5678
    firewall   inbound TCP 7443 from 192.0.2.11
    config     readable by NT SERVICE\ripcord
    snapshot   C:\Program Files\Ripcord\state\state.json
               written by ripcord-publish, served to the peer
               written 12s ago
               readable by NT SERVICE\ripcord
    key        readable by NT SERVICE\ripcord
               0123456789ABCDEF0123456789ABCDEF01234567
    logs       writable by NT SERVICE\ripcord
               C:\Program Files\Ripcord\logs

  THIS HOST'S CERTIFICATE
    0123456789ABCDEF0123456789ABCDEF01234567  in ripcord.yaml
      CN=HV-DR-01, expires 2029-09-01
    On the other host, run:
      ripcord pair HV-DR-01:0123456789ABCDEF0123456789ABCDEF01234567
    log        listener-2026-09-25.log

  LAST 3 LINES OF THE LOG
    08:00:00.000Z service: ==== ripcord 0.8.0+def5678 listener starting
        configuration C:\Program Files\Ripcord\ripcord.yaml
    08:00:00.000Z serve: running: serve

  PUBLISHER
    service    running      start mode Auto
    command    "C:\Program Files\Ripcord\ripcord.exe" publish
    version    0.8.0+def5678
    config     readable by NT SERVICE\ripcord-publish
    hyper-v    member of Administrateurs Hyper-V
    bitlocker  readable by it
    snapshot   writable by NT SERVICE\ripcord-publish
    logs       writable by it alone
               C:\Program Files\Ripcord\logs\publish
    log        publish-2026-09-25.log
    ...

  It matches the configuration.
```

On a listener that stopped:

```
  LISTENER
    service    STOPPED      start mode Auto
    command    "C:\Program Files\Ripcord\ripcord.exe" serve
    last exit  0x20000002, Ripcord exit 2: the configuration did not load
    ...
  LAST 4 LINES OF THE LOG
    ...
    08:00:00.000Z serve: exit 2: InvalidConfiguration

  Why it is not running:
    it stopped with exit 2: the configuration did not load
  Then run:
    ripcord check
```

Installed and running are two facts, not one: a registered service that is stopped serves
nothing, and the other host then reports the pair offline — which reads as a network fault
rather than as a service somebody has to start. The command line is printed as Windows has it
registered, because a second copy of the binary in another directory is how a pair ends up
running two versions.

| Row | What it says |
|---|---|
| `service` | running, `STOPPED`, starting, stopping, `PAUSED`, or `state unknown` when Windows could not be read — never guessed as stopped. Then the start mode. |
| `command` | what Windows runs. The service always reads `ripcord.yaml` beside that binary. |
| `version` | only when it is running: the build the **process** runs, as it recorded itself in `logs\listener-process.txt` when it started. After `ripcord update` the binary on disk is the new one while the process is still the old one; when the service runs this `ripcord.exe`, the row then adds `NOT the build of this ripcord.exe` and the report ends with `ripcord service restart`. Not when the service runs another copy: a restart would not change its build. `unknown` when there is no record, or the record's process id is not the one Windows gives for the service — a record left by an earlier run is never believed. |
| `last exit` | only when it is not running: the code Windows recorded, and what it means. |
| `firewall`, `snapshot`, `logs` | what the configuration needs, and whether the host has it. Left out when `ripcord.yaml` does not load. |
| `key` | whether the service account can read the private key of `listener.local_certificate_thumbprint`, or that no key was found for it in `LocalMachine\My`. Machine keys are readable by SYSTEM and Administrators only. |
| `snapshot`, third line | how long ago `state.json` was written, `STALE` past `peer.offline_after_sec`, or `NOT written yet`. When missing or stale, the report ends with why and the one command that fixes it, read from the publishing service: `ripcord service install` when it is absent, `ripcord service start` when it is stopped, `ripcord publish` when it runs and still fails — publishing once by hand prints the reason, and so does its log. |
| `THIS HOST'S CERTIFICATE` | each certificate in `LocalMachine\My` with a private key, whose first CN is this host's name — what the other host checks, whatever O, OU or DC follow — and not expired, latest expiry first, marked `in ripcord.yaml` when it is the configured one, or `None of these is the one in ripcord.yaml` after a renewal. The other host must also trust its issuer. Shown even when `ripcord.yaml` does not load. Under it, the one line to run on the other host — `ripcord pair <this host>:<thumbprint>` — which writes both thumbprints there: see [`pair`](pair.md). `NONE` when no certificate qualifies. |
| `PUBLISHER` | the publishing service: its state, command, last exit and version like the listener's, then `hyper-v` (membership), `bitlocker` (its ACE, or why BitLocker is carried from an administrator's read), `snapshot` (modify), `logs` (its own, and whether the listener is kept out), and the last 10 lines of `logs\publish\publish-YYYY-MM-DD.log`. |
| `log` | the day's `logs\listener-YYYY-MM-DD.log` (UTC date) beside the service's binary, or yesterday's when today has none, and its last 20 lines. |

The service is shown even when `ripcord.yaml` does not load — the likeliest reason it stopped.
The configuration's errors follow, and the exit code is then 2.

The heading above the last line is *Why it is not running* only when the service is stopped.
When it is starting, stopping, paused or its state could not be read, it is *Note*: an
unreadable state is never presented as a stopped one. Access denied means the console is not
elevated.

With `listener.enabled: false`, the report still shows the service and exits 0. `install`
refuses (exit 2), `restart` refuses (exit 4) and `remove` works: switching the listener off
leaves a way to take off a service installed while it was on.

## What the snapshot is

`state.json` is this host's own state — its VMs, their replication, its build — written every
15 seconds by the publishing service ([`publish`](publish.md)), and by `ripcord status`,
`check`, `failover` and `fence`, and served read-only by the listener to the peer over mutual
TLS. It is how the other host sees this one: it holds no secret, and
nothing reads it back on this host.

It is always `state\state.json` beside `ripcord.yaml` —
`C:\Program Files\Ripcord\state\state.json` on a default install — in a folder of its own, so
whatever writes it is never granted anything beside `ripcord.exe`. `listener.snapshot_path` is
no longer read: a file that still sets it loads, and every command says the line is ignored.

When something is missing, the last line names the command that would show it rather than
printing a plan: a plan printed by a command that changes nothing reads like one that is about
to.

## `install` — a reconciliation, not an installer

It compares the host with the configuration and applies only the difference. Re-running it on a
correct host does nothing. A moved binary, a changed port or a changed peer address becomes an
update rather than a teardown. `remove` is the same list read backwards — and it leaves the
snapshot file alone, because an uninstaller that deletes data is one people are afraid to run.

Two services: the listener `ripcord` and the publisher `ripcord-publish`. In this order:
create both (a virtual account exists only once its service does), open the port to the peer
only, grant the listener read on the files of the install folder, read on `state\`, **modify**
on `logs\`, read on its certificate's private key, register the `ripcord` event source; grant
the publisher read on the files of the install folder, **modify on `state\`** and on
`logs\publish\` — which stops inheriting from `logs\` so the listener cannot write it — add it
to Hyper-V Administrators and, while `storage.check_bitlocker_autounlock` is on, give it one ACE
on the BitLocker WMI namespace; **then start the listener, then the publisher** (restarted
instead when it was just added to the group: membership reaches a process at its next start).

`remove` takes the publisher down first, and everything it was granted **while its service
still exists** — stopped, the ACE, the group, the folders, then deleted — because a virtual
account's name only resolves as long as its service does. A service deleted by hand leaves its
membership and ACE behind as a bare SID that no name matches; take those off by hand.

The key grant is read, on the key file only. Without it the handshake fails on this host's own
key, and the listener logs the refusal as *this host could not use its own private key* rather
than blaming the caller.

**Both services restart a minute after a crash** (`sc.exe failure … reset= 86400
actions= restart/60000/…`), set right after each is created. Windows repeats the last action
for every crash after the third, so a crash loop is retried each minute until a day passes
without one. Only a crash:
a service that stops itself with an exit code — a configuration it refuses — stays stopped,
rather than refusing the same file again every minute. Deleting a service takes its recovery
with it.

**Access nothing uses any more is taken back**, as the last steps: every machine key file the
service account can read other than the configured certificate's — the ones left by `pair` or a
renewal, however many — and, once a service moved, what either account was granted in the old install folder, its `state`, its `logs` and `logs\publish`.
Only entries of the account's own are touched, never inherited ones, and never recursively
into a folder that still holds the install folder, the logs or the snapshot in use. With the
configured certificate's key not found, no key is touched: which one is current cannot be told.
Nor when the machine keys take longer than the command timeout to list. A snapshot
folder left behind by an older `snapshot_path` elsewhere is not looked for; `ripcord service
remove` run before moving it is what takes that one back.

After `pair` or a renewal, a listener still running reads the old key on every connection: once
`install` has taken that key back, it serves nothing until `ripcord service restart`. Run the
restart right after the install.

The logs folder is the only place the service account may write. Modify rather than write,
because pruning an old log deletes it; on that folder only, never on the install folder or the
binary. The event source is where the listener reports a start it cannot log to its file: a
virtual account cannot write to the event log under a source nobody registered. `remove`
revokes the folder access and removes the source, and leaves the logs where they are.

The folder, not the file: `ripcord status` rewrites the snapshot by moving a new file over the
old one, and a move brings the new file's access list with it, so an entry set on the file
itself would survive exactly one write. The folder is created if it is not there yet — on a
fresh host nothing has written a snapshot, and `icacls` cannot grant access to a path that does
not exist.

Nothing happens until `y` is answered; Enter declines.

## When it does not start

`service install` or `restart` whose start step fails ends with *Run 'ripcord service' to see
why it did not start*. `sc start` returns as soon as the process answers Windows, before the
listener has read its configuration, so the step then watches the service for five seconds: a
service that stops in that time fails the step, with the exit code Windows recorded. The
report ends with one line on the likely cause, and what to run:

| It says | Meaning | It suggests |
|---|---|---|
| its start mode is Disabled | Windows will not start it | `sc.exe config ripcord start= auto` |
| the listener is disabled in ripcord.yaml | `listener.enabled: false`: `serve` has nothing to do | `ripcord service remove` |
| NT SERVICE\ripcord cannot write its logs folder | it cannot open its log, so it stops at once | `ripcord service install --dry-run` |
| it has not been started since Windows booted | 1077: nothing has started it yet | `ripcord service restart` |
| Windows recorded 1053, 1069, 2 ... | a code Windows set before the listener could log | the event log |
| Windows recorded exit N | Ripcord's code, and the log does not say it (or says another) | as for the log |
| it stopped with exit N | the listener logged the exit code Windows recorded | `ripcord check` for 2 and 3 |
| it stopped on an unhandled error | the error and its stack are in the log | — |
| it stopped cleanly | stopped by an operator or at shutdown | `ripcord service restart` |
| the process ended without reporting to Windows | 1067: a crash | the event log |
| it started, then stopped without writing why | a start line and nothing after it | the event log |
| no log today or yesterday | it died before it could write one, or stopped earlier | the event log |

Windows keeps the latest stop; the log holds only the last run that got as far as writing its
start line. A start that fails before that leaves the file as an earlier run left it, so the
log is believed only where Windows does not contradict it, and a verdict taken from Windows
says *its log is from an earlier run* when the file said otherwise. The event log commands it
prints — run them in PowerShell:

```powershell
Get-WinEvent -ProviderName ripcord -MaxEvents 5 | fl
Get-WinEvent -ProviderName '.NET Runtime' -MaxEvents 5 | fl
```

The first is the listener's own report when it could not open its log file. The second is a
crash before it logged anything at all (event 1026).

`sc.exe query ripcord` shows the same code as `WIN32_EXIT_CODE`. `0x2000000N` is Ripcord's own
exit code N (see [exit codes](README.md#exit-codes)): `0x20000003` is a local access failure,
`0x20000002` a configuration that will not load. Any other value was set by Windows, not by
Ripcord — 2 is a missing binary, 1053 no answer to the start request, 1067 a crash, 1069 an
account that could not log on, 1077 a service not started since boot.

## `restart` — after every configuration edit

The listener reads `ripcord.yaml` **once, when it starts**. Editing the file changes nothing
until this has run. `status` and `check` are commands rather than the service, so they re-read
the file every time — the two can disagree until the listener is restarted.

A service that is not running is started rather than restarted. This and `start` are the
mutating verbs with no typed confirmation, a stated exception recorded as decision D75: it is over in
a second, it changes nothing that outlives it, and it is typed several times an evening while
a configuration is being got right. A confirmation asked for that would become the reflex the
failover confirmations must never be.

## `start` and `stop`

`start` starts a listener that is installed and not running, with no confirmation, for the
same reason as `restart`. On a running listener it changes nothing and says so — unlike
`restart`, which restarts it.

`stop` leaves the service installed and stops it. It asks **`y/n`**, Enter declining:
unlike a restart it outlives itself, and until somebody starts the listener again the other
host cannot read this one and shows it `SILENT`. The step is done once Windows reports the
service stopped, or fails after 30 seconds. On a stopped listener it changes nothing and asks
nothing. It works with `listener.enabled: false`, where `start` and `restart` refuse.

## Exit codes

Bare `ripcord service` (and `service status`): **0** once the report is printed, whatever the
service is doing; **2** when `ripcord.yaml` cannot be used — the service is still shown above
the errors; **3** when the host could not be inspected at all.

For `install`, `remove`, `restart`, `start` and `stop`:
**2** when the configuration cannot be deployed on this host — a snapshot path on a missing
drive, a disabled listener for `install`, no service installed for `restart`, `start` or
`stop` — and nothing was asked or changed. **4** for `restart` or `start` on a disabled
listener. **4** when the confirmation is declined — nothing was
changed. **3** when a step failed before
anything was applied. **5** when a step failed with one behind it: the host is between two
states and the output says where it stopped. Re-running resumes from there.
