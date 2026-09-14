# `ripcord fence`

The command that is easy to leave out and expensive to forget.

```
ripcord fence [--dry-run] [--config <path>]
```

## What it is for

After an unplanned failover, the original primary still holds a copy of every VM that moved,
each set to start itself. Both hosts sit on the same external switch on the same subnet. So
restoring that host's power boots the old domain controller beside the failed-over one, with
the same identity and the same address.

`fence` records each VM's `AutomaticStartAction`, sets it to `Nothing`, and names any copy it
could not confirm switched off. **Run it on the returning host, the moment it is reachable,
before anything else.** The unplanned failover prints that instruction before the operator
leaves the screen.

A copy already running there halts the fence rather than being fenced: that is a divergence
under way rather than a future boot, and a start action does not stop it.

The recorded previous value is kept, so the change is reversible once the pair is whole again.

## Exit codes

**4** when the confirmation is declined. **5** when some VMs were fenced and others could not
be confirmed — a human has to look at the rest.
