# `ripcord rollback`

Puts back the binary the last [`update`](update.md) set aside.

```
ripcord rollback [--config <path>] [--dry-run]
```

```
RIPCORD ROLLBACK

  Running    0.3.0
  Set aside  0.2.1  (kept on 2026-09-14)

  1  move the running binary out of the way
     a running binary can be renamed, never deleted, so nothing here removes one
  2  put the binary set aside back where the running one was
     these are the bytes that were running on this host before the last update
  3  keep the version being left behind as the one set aside
     running this again returns to it, so the retreat is not a one-way door

  WARNING: once this host goes back, a failover spanning both hosts is
           refused until HV-PRIMARY-01 does too
  WARNING: nothing here records why: `ripcord update` will offer the newer
           release again
```

## No network, and that is the point

These two hosts are meant to have no outbound access. A retreat that has to fetch a release is
a retreat that fails on the day it is needed — so this fetches nothing and verifies nothing.

What it puts back is the file `update` set aside: the binary this host was last running. When
that update fetched it, its signature was checked before it was installed — but **nothing here
re-establishes that**, and nothing here can. `<binary>.old` is a filesystem convention, and a
file somebody copied there by hand would be treated exactly the same. The claim this command
makes is the narrow one: these are the bytes that were in place before.

It follows that neither `updates.check` nor `updates.install` gates this command. Those two
switches exist because reaching for a release means reaching off the host. A host forbidden to
fetch anything is the one most likely to need to go back.

## One generation

`update` keeps exactly one binary aside, so this goes back exactly one version. There is no
history, and asking for an arbitrary version is not possible — that would need the network
this command exists to do without.

Running it twice returns to where you started. The sequence is an **exchange**, not a
replacement: every move is a rename, because a running binary can be renamed on Windows and
never deleted. So the version being left behind becomes the one set aside.

## What it does not remember

Nothing records *why* you went back. The newer release is still published, so the next
`ripcord update` will offer it again — the plan says so out loud rather than letting somebody
find out the following week.

## When it refuses

| | |
|---|---|
| nothing set aside — this host has never updated | **2**, and it names `ripcord update` as the only thing that keeps one |
| the binary set aside is still running — the listener, not restarted since the update | **2**, naming `ripcord service restart`: the exchange writes over that file, and Windows will not replace a running binary |
| an earlier exchange did not finish | **2**. The file it left holds the only copy of a version this host was running; move it somewhere safe first |
| the node name is not typed in full | **4**, nothing moved |
| it failed before anything moved | **3**, and the host runs what it was running |
| it failed part way and went back | **3**, same answer, with the reason it could not go on |
| it failed part way and **could not** go back | **5**, and it names the file the old binary is under |

That last row is the one worth reading. The kept binary is **copied** into place before
anything is moved, so everything that fails for want of room or permission fails while the host
is still whole. What remains is a window two renames wide in which the path the service starts
from is empty — and if the second rename fails, the first is undone. When even that fails,
exit 5 says so and names `<binary>.swap`, which is where the version you were running is.

`--dry-run` prints the sequence and moves nothing.

## Afterwards

The binary on disk has changed; the running process has not. **Restart the listener** —
[`ripcord service restart`](service.md) — or the service goes on serving from the version you
just left.
