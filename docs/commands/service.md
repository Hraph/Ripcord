# `ripcord service`

The listener service: what it is doing, and the three things that change it.

```
ripcord service                     what is installed, running, and from where
ripcord service install [--dry-run]
ripcord service remove  [--dry-run]
ripcord service restart [--dry-run]
```

Bare, it changes nothing. `install`, `remove` and `restart` are words rather than flags,
because all three mutate a host that may be running a domain controller, and a word is harder
to type by accident than a flag next to the one you meant.

## `ripcord service`

```
RIPCORD LISTENER

  ON THIS HOST
    service    running     C:\Program Files\Ripcord\ripcord.exe serve
    firewall   inbound TCP 7443 from 192.0.2.11
    snapshot   C:\Program Files\Ripcord\state.json
               written by 'ripcord status', served to the peer
               readable by NT SERVICE\ripcord
    logs       writable by NT SERVICE\ripcord
               C:\Program Files\Ripcord\logs

  It matches the configuration.
```

Installed and running are two facts, not one: a registered service that is stopped serves
nothing, and the other host then reports the pair offline — which reads as a network fault
rather than as a service somebody has to start. The command line is printed with it, because a
second copy of the binary in another directory is how a pair ends up running two versions.

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
why it did not start*. Then, in order:

1. `ripcord service` — installed, running, and whether the service account can write its logs.
2. The day's log, `logs\listener-YYYY-MM-DD.log` (UTC date), beside the binary.
3. The listener's own report when it could not open that file:

   ```powershell
   Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='ripcord'} -MaxEvents 5 | Format-List TimeCreated,Message
   ```

4. A crash before it logged anything at all:

   ```powershell
   Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='.NET Runtime'; Id=1026} -MaxEvents 3 | Format-List TimeCreated,Message
   ```

`sc.exe query ripcord` shows the code the service stopped with as `WIN32_EXIT_CODE`. `0x2000000N`
is Ripcord's own exit code N (see [exit codes](README.md#exit-codes)): `0x20000003`
is a local access failure, `0x20000002` a configuration that will not load. Any other value
was set by Windows, not by Ripcord — 2 is a missing binary, 1067 a crash.

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

**2** when the configuration cannot be deployed on this host — a snapshot path on a missing
drive — and nothing was asked or changed. **4** when the confirmation is declined — nothing was
changed. **3** when a step failed before
anything was applied. **5** when a step failed with one behind it: the host is between two
states and the output says where it stopped. Re-running resumes from there.
