# `ripcord check`

One question: **if it goes down now, does it hold?**

```
ripcord check [--config <path>] [--notify [--dry-run]]
```

## What a finding says

Three things, always: what was observed, what it means **on the day of the failover**, and the
command that fixes it — never executed. The second is the one that matters. "Incorrect vSwitch"
helps nobody; "this VM would boot with no network on the target" gets somebody out of bed.

Twenty-two rules, in four severities. Criticals set the exit code; warnings never do — a
scheduled task that failed on a warning would be switched off within a week, and then the
criticals would go unread too.

## Three things are deliberate

**A rule whose data is missing is listed under `NOT CHECKED`, never treated as satisfied.** Its
count is in the headline, because the exit code cannot carry it: 1 means *a critical rule is
violated*, and overloading it would make `check` unusable as the gate the other commands
depend on.

**Permanent criticals are acknowledged, with a mandatory expiry.** An acknowledgement names a
rule and optionally a VM, carries a reason and a date, and is still printed — accepted, not
hidden. Once the date passes, the finding counts again and says why it came back. Three rules
can never be acknowledged, because each means the service would not come back at all:
`startup-ram-exceeds-target`, `p1-startup-ram-sum-exceeds-target`, `vhdx-outside-relationship`.

**The operating mode is derived from what is observed, never from the configuration.** While
the pair runs on the disaster recovery side, "replication direction inverted" is true by
definition, so `FAILED OVER` suppresses that one rule and the header says so. A *partial*
inversion — some VMs primary on one host, some on the other — is still critical.

## `--notify`

The scheduled task's form of the same command. The trigger is the exit code it already
returns, so there is no second detection path and no second opinion about whether the pair is
healthy. See [alerting](../alerting.md) for what is sent, when, and what stays quiet.

Delivery never changes the exit code: a relay that is down is not a pair that is broken.

## Exit codes

**1** when a critical rule is violated — this is the one command whose exit code reports on the
infrastructure rather than on the tool. **0** otherwise, **2** for a bad configuration, **3**
when the host cannot be read.
