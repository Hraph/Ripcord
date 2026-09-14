# `ripcord failback`

The planned sequence, pointed home.

```
ripcord failback (--vm <name> | --all | --priority P1) [--dry-run] [--config <path>]
```

Same six steps as a planned failover, same refusals, same undo — with the direction reversed
exactly once. Reversing twice was the bug this command was split out over, and a test pins it.

`--all` and `--priority P1` work here as they do for [`failover`](failover.md); the scope rules
are identical.

## When it is not the command you want

After an **unplanned** failover the pair is not protected: replication was never reversed, so
there is nothing to fail back *from*. Re-establishing it is `reprotect`, which **is not
built** — see the designed-but-unbuilt list in [`TRACKING.md`](../TRACKING.md). Until it
exists, that step is done by hand, and `failback` is refused by the precondition that notices
the pair is unprotected.

## Exit codes

As [`failover`](failover.md): **4** refused or interrupted with nothing changed, **5** left
between two states with the manual command named.
