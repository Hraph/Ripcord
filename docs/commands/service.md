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
    snapshot   readable by NT SERVICE\ripcord

  It matches the configuration.
```

Installed and running are two facts, not one: a registered service that is stopped serves
nothing, and the other host then reports the pair offline — which reads as a network fault
rather than as a service somebody has to start. The command line is printed with it, because a
second copy of the binary in another directory is how a pair ends up running two versions.

When something is missing, the last line names the command that would show it rather than
printing a plan: a plan printed by a command that changes nothing reads like one that is about
to.

## `install` — a reconciliation, not an installer

It compares the host with the configuration and applies only the difference. Re-running it on a
correct host does nothing. A moved binary, a changed port or a changed peer address becomes an
update rather than a teardown. `remove` is the same list read backwards — and it leaves the
snapshot file alone, because an uninstaller that deletes data is one people are afraid to run.

Four steps at most, in this order: create or repoint the service, open the port to the peer
only, grant the service account read access to the folder holding the snapshot, **and start the
service last** — after the rule that lets the peer in and the access it needs to the file it
serves.

The folder, not the file: `ripcord status` rewrites the snapshot by moving a new file over the
old one, and a move brings the new file's access list with it, so an entry set on the file
itself would survive exactly one write. The folder is created if it is not there yet — on a
fresh host nothing has written a snapshot, and `icacls` cannot grant access to a path that does
not exist.

Nothing happens until the node name is typed in full.

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

**4** when the confirmation is declined — nothing was changed. **3** when a step failed before
anything was applied. **5** when a step failed with one behind it: the host is between two
states and the output says where it stopped. Re-running resumes from there.
