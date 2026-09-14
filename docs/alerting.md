# Alerting

Hyper-V Replica surfaces plenty of state and pushes none of it. A resync can run for three
weeks with nobody knowing. This is the delivery half, and nothing else: the trigger is the exit
code [`check`](commands/check.md) already returns, so there is no second detection path and no
second opinion about whether the pair is healthy.

Off by default. A host with no `alerting` block behaves exactly as it did before this existed.

## The scheduled task

```
schtasks /Create /TN "Ripcord check" /SC MINUTE /MO 15 /RL HIGHEST /RU SYSTEM ^
  /TR "\"C:\Program Files\Ripcord\ripcord.exe\" check --notify"
```

## Four rules decide what actually leaves the host

All four exist because an alert nobody reads is worse than no alert at all.

**Transitions, not runs.** A critical finding that was not in the last notification is sent at
once. The same one on the next run is not: a mail every fifteen minutes becomes a filter rule
within a week, and then the criticals go unread too.

**A repeat threshold**, 24 hours by default, after which a still-broken pair is mentioned
again.

**Quiet hours**, in the host's local time. An alert raised inside the window is **held and
delivered when the window ends**, never dropped. One that clears before the window ends is
dropped, because nothing was ever sent to correct. Both hosts need the same time zone, or the
same alert is held on one and delivered on the other.

**Grouping.** Everything wrong with the pair goes in one message, each finding with its meaning
on the day of the failover and the command that fixes it — not a dump of the check output. The
pair recovering is itself a transition, and is notified once.

## What it does not do

Delivery never changes the exit code: a relay that is down must not be reported as a pair that
is broken. A notification that was held, refused or never attempted is printed on stderr beside
the report.

What was sent is remembered in `alert-state.json` beside the binary — written **after** the
transport accepted it, so a relay that was down for a minute does not cost a day's silence.
`check --notify --dry-run` shows what would go where and writes nothing.

## The relay password

Never in `ripcord.yaml`. `password_secret` names an environment variable the service reads it
from, and a `password:` key in the file is refused by name. Credentials on a connection that
never starts TLS are refused too.

On a host running the check as SYSTEM that means a machine-wide variable, readable by
administrators — which is the trade-off. An internal relay that accepts from this subnet
without credentials is the better arrangement where one exists.

A notification names VM names, both host names and the command that fixes each finding. No
thumbprint and no credential is in there, and a webhook must be `https`; the rest is the price
of an alert that says something actionable.
