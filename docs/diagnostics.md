# The diagnostic log

A Hyper-V host is not a machine anybody attaches a debugger to. The console is a KVM at
1024×768, the operator is often not the person who can read a stack trace, and what reaches
whoever can help is a photograph of a screen. So every command writes what it did to a file,
and the file is written to be sent.

```
2026-09-14 06:30:12.345Z status: running: status
2026-09-14 06:30:12.902Z hyper-v: the local Hyper-V state could not be read
    Microsoft.Management.Infrastructure.CimException: type mismatch for parameter …
       at Ripcord.Adapters.Wmi.WmiHypervProvider.ReadPendingBytes(…)
       at Ripcord.Adapters.Wmi.WmiHypervProvider.ReadLocalState(…)
2026-09-14 06:30:12.904Z status: exit 3: LocalAccessFailure
```

The console gets one sentence, because one sentence is what fits. This is where the rest goes.

## Where it is

`ripcord.log`, beside the binary, with `audit.jsonl` and `alert-state.json`. Move it, resize it
or switch it off in `ripcord.yaml`:

```yaml
diagnostics:
  enabled: true
  path: D:\Ripcord\ripcord.log
  max_size_mb: 5
```

Absent, the defaults apply and the log is **on**. That is the opposite of `listener`,
`alerting`, `updates` and `dashboard`, which are all off until the file switches them on — and
deliberate: those open sockets or send mail, and this writes a file.

## What it will not do

**It will not refuse a command.** A size out of range is replaced by the default rather than
reported as an error; a directory that does not exist is created; a path that cannot be written
is given up on in silence. Everywhere else in `ripcord.yaml` a wrong value is an error, because
everywhere else describes the pair and a wrong value moves production to the wrong place. This
describes a log file, and a host that will not start because it cannot explain itself has
turned the explanation into the outage.

**It will not grow without bound.** Past `max_size_mb` the file is moved to `ripcord.log.1`,
replacing whatever was there, and a fresh one is started. Two files, never three: a numbered
series is what fills the volume the log was written to explain, and on these hosts that volume
is the one the VMs live on.

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
