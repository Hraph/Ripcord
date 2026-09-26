# The diagnostic log

A Hyper-V host is not a machine anybody attaches a debugger to. The console is a KVM at
1024×768, the operator is often not the person who can read a stack trace, and what reaches
whoever can help is a photograph of a screen. So every command writes what it did to a file,
and the file is written to be sent.

```
logs\ripcord-2026-09-14.log

2026-09-14 06:30:12.345Z status: running: status
2026-09-14 06:30:12.902Z hyper-v: the local Hyper-V state could not be read
    Microsoft.Management.Infrastructure.CimException: Access denied
       at Microsoft.Management.Infrastructure.Internal.Operations.CimSyncEnumeratorBase`1.MoveNext()
       at Ripcord.Adapters.Wmi.WmiHypervProvider.ReadLocalState(…)
2026-09-14 06:30:12.904Z status: exit 3: LocalAccessFailure
```

The console gets one sentence, because one sentence is what fits. This is where the rest goes.

## Where it is

A `logs` folder beside the binary, next to `audit.jsonl` and `alert-state.json`. One file per
day and per writer, named by the UTC date, and appended to:

| File | Written by |
|---|---|
| `logs\ripcord-YYYY-MM-DD.log` | every command run by hand or by the scheduler |
| `logs\listener-YYYY-MM-DD.log` | the listener service: a start line with the version and the configuration it read, what `serve` would have printed on a console, and one line per peer connection served or refused |
| `logs\publish\publish-YYYY-MM-DD.log` | the publishing service: a start line, every change between published and not, one line an hour, the stop; a failure repeated every 15 s is written once an hour. Its own folder, which the listener cannot write |

The date is UTC on both hosts, so a moment is in the same file name on each of them.

Move the commands' folder, resize the files or switch the commands' log off in `ripcord.yaml`:

```yaml
diagnostics:
  enabled: true
  path: D:\Ripcord\logs
  max_size_mb: 5
```

`path` names a **folder**. It used to name a file; a value ending in `.log` is still accepted
and read as the folder holding it, so `D:\Ripcord\ripcord.log` now means `D:\Ripcord`.

**The listener service ignores `path` and `enabled`.** It runs as `NT SERVICE\ripcord`, which
may write to `logs\` beside the binary and nowhere else — [`service install`](commands/service.md)
creates that folder and grants it. A service pointed at a folder it cannot write would have no
log at all. `max_size_mb` applies to both.

[`ripcord service`](commands/service.md) reads the listener's log for you: it shows the day's
file (or yesterday's), its last 20 lines, and what the last run said about how it ended.

Before this layout the log was `ripcord.log` (and `ripcord.log.1`) beside the binary, and the
service wrote `listener.log`. Nothing writes or deletes those any more; delete them by hand.

Absent, the defaults apply and the log is **on**. That is the opposite of `listener`,
`alerting`, `updates` and `dashboard`, which are all off until the file switches them on — and
deliberate: those open sockets or send mail, and this writes a file.

## What it will not do

**It will not refuse a command.** A size out of range is replaced by the default rather than
reported as an error; a directory that does not exist is created; a path that cannot be written
is given up on in silence. Removing old files never fails a command either. Everywhere else in `ripcord.yaml` a wrong value is an error, because
everywhere else describes the pair and a wrong value moves production to the wrong place. This
describes a log file, and a host that will not start because it cannot explain itself has
turned the explanation into the outage.

**It will not grow without bound.** Files are kept for **30 days** — today and the 29 before
it — and older ones are deleted the first time a command or the service writes on a new day.
Only files named like the ones above are ever deleted: the folder may be one the operator
chose, and nothing else in it is touched. The 30 days are fixed, not configurable.

Within a day, past `max_size_mb` the day's file is moved to the same name with `.1` appended,
replacing whatever was there, and a fresh one is started. Two files a day, never three: a
numbered series is what fills the volume the log was written to explain, and on these hosts
that volume is the one the VMs live on. The worst case at the default 5 MB is 30 days × 2
writers × 2 files × 5 MB, about 600 MB; a normal day is a few kilobytes.

**It will not carry a secret.** A URL is cut down to its scheme and host — the half that
explains a failure — and its path, which is the whole of a Teams or Slack webhook's
authentication, is replaced by `[removed]`. So is any value whose key names a password, a
token, an API key or an authorization header. That is non-negotiable rule 7 applied to the one
file that is meant to leave the host.

## What goes in it

| Written by | When |
|---|---|
| every command | on entry, with its arguments, and again with its exit code |
| `hyper-v` | the local host could not be read, or an optional column of it could not |
| `host-system` | RAM, volumes or BitLocker could not be read |
| `certificates` | the certificate store could not be opened |
| `peer` | the other host could not be reached, with the refusal underneath |
| `snapshot` | this host could not publish its own state for the peer to read |

Each of those, on the console, is one line or a degraded column. Here it is the exception:
type, message and stack.

## The audit trail is not this

`audit.jsonl` records what was *decided* before production moved, one JSON object per line,
never rewritten and never rotated — a failover that cannot write it does not start. This file
records what *happened*, is trimmed when it gets large, and never stops anything. They are kept
in separate files, and separate projects, so that rotation can never be pointed at the trail.
