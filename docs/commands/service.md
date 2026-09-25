# `ripcord service`

The listener service: what it is doing, and the three things that change it.

```
ripcord service [status]            is it running, and if not, why
ripcord service install [--dry-run]
ripcord service remove  [--dry-run]
ripcord service restart [--dry-run]
```

Bare, it changes nothing. `ripcord service status` is the same report under the word an
operator types by habit — one report, two spellings. `install`, `remove` and `restart` are words rather than flags,
because all three mutate a host that may be running a domain controller, and a word is harder
to type by accident than a flag next to the one you meant.

## `ripcord service` (or `ripcord service status`)

On a healthy host:

```
RIPCORD LISTENER

  ON THIS HOST
    service    running      start mode Auto
    command    "C:\Program Files\Ripcord\ripcord.exe" serve
    firewall   inbound TCP 7443 from 192.0.2.11
    snapshot   C:\Program Files\Ripcord\state.json
               written by 'ripcord status', served to the peer
               readable by NT SERVICE\ripcord
    logs       writable by NT SERVICE\ripcord
               C:\Program Files\Ripcord\logs
    log        listener-2026-09-25.log

  LAST 3 LINES OF THE LOG
    08:00:00.000Z service: ==== ripcord 0.4.1+32aac02 listener starting
        configuration C:\Program Files\Ripcord\ripcord.yaml
    08:00:00.000Z serve: running: serve

  It matches the configuration.
```

On a listener that stopped:

```
  ON THIS HOST
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
| `last exit` | only when it is not running: the code Windows recorded, and what it means. |
| `firewall`, `snapshot`, `logs` | what the configuration needs, and whether the host has it. Left out when `ripcord.yaml` does not load. |
| `log` | the day's `logs\listener-YYYY-MM-DD.log` (UTC date) beside the service's binary, or yesterday's when today has none, and its last 20 lines. |

The service is shown even when `ripcord.yaml` does not load — the likeliest reason it stopped.
The configuration's errors follow, and the exit code is then 2.

## What the snapshot is

`state.json` is this host's own state — its VMs, their replication, its build — written by
`ripcord status` (and by `check`, `failover` and `fence`) and served read-only by the listener
to the peer over mutual TLS. It is how the other host sees this one: it holds no secret, and
nothing reads it back on this host.

It defaults to `state.json` beside `ripcord.yaml`, which is the install folder chosen at
install — `C:\Program Files\Ripcord\state.json` on a default install. An explicit
`listener.snapshot_path` is kept exactly as written. A path on a drive this host does not have
refuses `install` before anything changes:

```
  Cannot be installed as configured:
    listener.snapshot_path is D:\Ripcord\state.json, the old default, and
    this host has no D: volume. Remove the line: the snapshot then sits
    beside ripcord.yaml.
```

`D:\Ripcord\state.json` was the default up to 0.4. `ripcord service` shows the same message
in place of its last line.

When something is missing, the last line names the command that would show it rather than
printing a plan: a plan printed by a command that changes nothing reads like one that is about
to.

## `install` — a reconciliation, not an installer

It compares the host with the configuration and applies only the difference. Re-running it on a
correct host does nothing. A moved binary, a changed port or a changed peer address becomes an
update rather than a teardown. `remove` is the same list read backwards — and it leaves the
snapshot file alone, because an uninstaller that deletes data is one people are afraid to run.

Six steps at most, in this order: create the service, open the port to the peer only, grant
the service account read access to the folder holding the snapshot, create `logs` beside the
binary and grant the service account **modify** access to it, register the `ripcord` source in
the Application event log, **and start or repoint the service last** — after the rule that lets
the peer in and the access it needs to the file it serves and the folder it logs to.

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

Nothing happens until the node name is typed in full.

## When it does not start

`service install` or `restart` whose start step fails ends with *Run 'ripcord service' to see
why it did not start*. That report ends with one line on the likely cause, and what to run:

| It says | Meaning | It suggests |
|---|---|---|
| its start mode is Disabled | Windows will not start it | `sc.exe config ripcord start= auto` |
| it stopped with exit N | the listener logged its own exit code after its last start | `ripcord check` for 2 and 3 |
| it stopped on an unhandled error | the error and its stack are in the log | — |
| it stopped cleanly | stopped by an operator, at shutdown, or the listener is disabled | `ripcord service restart` |
| NT SERVICE\ripcord cannot write its logs folder | it cannot open its log, so it stops at once | `ripcord service install --dry-run` |
| Windows recorded exit N | the log says nothing; Windows kept Ripcord's code | as for the log |
| the process ended without reporting to Windows | 1067: a crash | the event log |
| it started, then stopped without writing why | a start line and nothing after it | the event log |
| no log today or yesterday | it died before it could write one, or stopped earlier | the event log |

The log is believed before Windows: it says which run it is about. The event log commands it
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
Ripcord — 2 is a missing binary, 1067 a crash, 1069 an account that could not log on.

## `restart` — after every configuration edit

The listener reads `ripcord.yaml` **once, when it starts**. Editing the file changes nothing
until this has run. `status` and `check` are commands rather than the service, so they re-read
the file every time — the two can disagree until the listener is restarted.

A service that is not running is started rather than restarted. This is the one mutating verb
with no typed confirmation, which is a stated exception recorded as decision D75: it is over in
a second, it changes nothing that outlives it, and it is typed several times an evening while
a configuration is being got right. A confirmation asked for that would become the reflex the
failover confirmations must never be.

## Exit codes

Bare `ripcord service` (and `service status`): **0** once the report is printed, whatever the
service is doing; **2** when `ripcord.yaml` cannot be used — the service is still shown above
the errors; **3** when the host could not be inspected at all.

For `install`, `remove` and `restart`:
**2** when the configuration cannot be deployed on this host — a snapshot path on a missing
drive — and nothing was asked or changed. **4** when the confirmation is declined — nothing was
changed. **3** when a step failed before
anything was applied. **5** when a step failed with one behind it: the host is between two
states and the output says where it stopped. Re-running resumes from there.
