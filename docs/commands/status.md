# `ripcord status`

What both hosts are doing, right now. It describes; it does not judge — that is
[`check`](check.md).

```
ripcord status [--config <path>]
```

## What it shows

Two sections, `LOCAL` and `PEER`, each with one line per VM: role, replication state, health,
lag, and the volume still to send. Fixed at 75 columns, no colour, because the real reading
conditions are a 1024×768 KVM during an incident.

```
RIPCORD STATUS                                      2026-09-12 14:00:00 UTC
ripcord 0.2.1+abc123def456 on both hosts

LOCAL   HV-REPLICA-01                                             REACHABLE
  VM                   ROLE     STATE            HEALTH       LAG   PENDING
  -------------------------------------------------------------------------
  VM-DC-01             Replica  Resynchronizing  Critical   6h00m     16 GB
  VM-LEGACY-01         Replica  Replicating      Warning    9m00s    256 MB
  VM-BACKUP-01         None     Disabled         Unknown        -         -

PEER    HV-PRIMARY-01                                               OFFLINE
  Unreachable since: 2026-09-12 13:41:02 UTC
```

The version line carries **both** builds when the peer has published one. A failover spanning
two hosts is refused outright on a mismatch, so the skew belongs on the page that describes the
pair rather than in the error message of the command somebody typed under pressure. A peer that
published no build says nothing: *cannot be compared* is not *the same*.

## Where the peer's half comes from

Not from Hyper-V. Each host publishes a snapshot of its own state to a file — `state.json`
beside `ripcord.yaml` unless `listener.snapshot_path` says otherwise — and the listener serves
that file to the other host over mutual TLS ([`serve`](serve.md)). So `status` reads the
peer's *published* view, with its age, and marks it `STALE` past `peer.offline_after_sec`.

A host with the listener switched off degrades to the local half rather than failing.

## Why the other host cannot read this one

On the other host this one shows `SILENT`, which reads as a network fault. Often the cause is
here, where only this side can see it, so a `LISTENER` block closes the page whenever the
configuration wants a listener and this host is not running one:

```
LISTENER  NOT RUNNING
  The listener service is stopped, so HV-DR-01 cannot read this host.
  Next: ripcord service
```

| | |
|---|---|
| `NOT INSTALLED` | the configuration wants a listener and no service exists. Next: `ripcord service install --dry-run` |
| `NOT RUNNING` | the service is stopped, stopping or paused. Next: `ripcord service`, which says why |
| `UNKNOWN` | Windows would not describe the service — the reason is printed, in the words `ripcord service` uses. Never read as stopped |

A running or starting service adds nothing, and neither does a listener switched off in
`ripcord.yaml`: that is a choice, and the `PEER` section already says no channel is configured.
The block never changes the exit code: it is a fact about this host's listener, not a failed
read.

When it exits **3**, the console says which read failed and the day's `logs\ripcord-YYYY-MM-DD.log` says why — the
exception, its type and its stack. See [the diagnostic log](../diagnostics.md).

## Exit codes

Always **0** on a successful read, including when the peer is unreachable — a scheduled
`ripcord status` must not alert because the other host is down. **2** for a bad invocation or
configuration, **3** when the local host cannot be read.
