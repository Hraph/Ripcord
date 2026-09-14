# `ripcord failover`

Move production to the other host. The reason this project exists, and the command everything
else is built to keep honest.

```
ripcord failover --scenario planned|unplanned (--vm <name> | --all | --priority P1) [--dry-run]
```

## It drives only the host it is run on

Each invocation performs the steps the plan assigns to **this** machine, then stops, names the
other host and prints the exact command to type there. That is a deliberate refusal to build a
channel that mutates the peer: in an unplanned failover the primary is dead by definition, so a
cross-host execution path is unavailable in precisely the case the tool exists for.

Where the sequence has got to is re-derived from what both hosts report, never from a stored
position. Running it twice is safe; running it on the wrong host is refused rather than obeyed.

## `--scenario planned` — six steps, two hosts

Shut the VM down, prepare the failover, start it on the replica, reverse replication, start the
VM, and verify it came up on the expected switch. The last one is not a formality: a VM that
boots with no network has failed over into an outage.

## `--scenario unplanned` — three steps, all here

Nothing is asked of the host that is gone. Replication is not reversed, because reversing needs
a primary that is there to accept the new direction. The version gate does not apply either:
the plan does not span two hosts, and refusing a disaster failover for want of a version string
would fail at the one thing the tool is for.

It ends by naming the host to fence and the command to type there. See [`fence`](fence.md) —
that step is easy to leave out and expensive to forget.

## What stops it

A **split brain** — a P1 VM positively running on both hosts — halts every mutating command.
Silence is not a claim: a host that could not be read is not a second claimant.

A **version mismatch** between the two binaries refuses any sequence spanning both. The
sequences are encoded in the binary, and half a sequence executed by each version is the error
class that cannot be recovered from at three in the morning.

**Critical findings** block, scoped to the VM being moved — a permanently unreadable fact about
a different VM must not block the domain controller for ever.

## Sweeps

`--all` and `--priority P1` move VMs in failover order. The per-VM `failover:` policy decides
what a sweep may pick up: `auto` is swept, `manual` is skipped but may be named, `never` is
refused even when named. An unknown name refuses the **whole** sweep rather than failing over
fewer machines than were asked for.

## If a step fails

What this invocation did is undone, and only that. A shutdown that succeeded before a failed
prepare puts the VM back on. If the undo itself fails, the command says so unmissably and names
the command to type by hand — that is exit code 5, and it is the answer nobody may walk away
from.

The audit trail records what could not be established **before** the first mutation, because
afterwards the host may not be writable.
