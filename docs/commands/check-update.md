# `ripcord check-update`

Is a newer release published. Nothing more.

```
ripcord check-update [--config <path>]
```

Off unless `updates.check` says otherwise, and it **refuses rather than skipping** when it is
off: a host somebody believes is checking, and silently is not, is worse than one that plainly
is not. These two machines are meant to have no outbound access at all.

It reports a version and stops. Installing is [`ripcord update`](update.md), behind its own
switch.

## Why this exists beside `update --dry-run`

They overlap, and the difference is worth a sentence because it is not obvious.

`update --dry-run` needs **both** switches: it halts when `updates.install` is off. That is the
default posture for these hosts — allowed to look, not allowed to replace themselves — and on
such a host `update --dry-run` refuses and says nothing about what is published. This command
needs only `updates.check`, so it is the one that answers there.

It also reads nothing but the feed. `update --dry-run` reads Hyper-V and the peer as well, to
say what updating would do to a failover, which costs a WMI timeout on a host that is already
struggling.

Both write the answer down. That part used to be this command's alone, which meant an
`update --dry-run` paid for the network call and threw the result away.

## The repository is addressed by its numeric id

Not by `owner/name`. A rename leaves a permanent redirect that HTTP clients follow, so the
obvious form keeps working — right up to the moment somebody creates a repository under the
abandoned name. The old URL then stops returning an error and starts returning **a different
repository's releases**, successfully. A version read from a stranger's releases is a confident
wrong answer, which is the failure mode this project exists to avoid.

The id is immutable, survives a rename and a change of owner, and a recreated repository under
the old name gets a different one that this binary never sees.

## What it writes down

The answer goes to a file beside the binary, and `status` and `check` print one line from it.
No command makes a network call of its own: a fifteen-second timeout in front of an unplanned
failover, on a host with no outbound access, is exactly what the design exists to avoid.

A release this host already runs is not news. A version that cannot be read is silence rather
than a guess.

## Exit codes

**0** with an answer. **2** when it is switched off. **3** when the feed could not be read, or
the two versions could not be compared — a non-answer exits as one, never as good news.
