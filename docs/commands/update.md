# `ripcord update`

Install a newer release on this host.

```
ripcord update [--config <path>] [--dry-run]
```

Off unless `updates.install` says so — a **separate switch** from `updates.check`, because
permission to look is not permission to replace the binary this host runs its failovers with.

## What it does, in order

Download the release and the detached signature beside it. **Verify that signature against a
public key compiled into the running binary**, and stop there if it does not verify: nothing is
moved and the host is where it was. Only then set the running binary aside — keeping it — and
put the new one in its place. If that last move fails, the old one goes back.

The key is compiled in rather than read from `ripcord.yaml`, so an attacker who can edit the
configuration cannot change what this host accepts as genuine. A release with no signature, one
that does not verify, or a host carrying no key at all are all refusals: there is no mode in
which an unverified release is installed.

The new version runs from the next service start, not from the command that installed it.

**Restart the listener after updating.** Until then it runs the binary set aside as
`ripcord.exe.old`, and Windows renames a running binary but will not delete or replace it.
The next `update` or `rollback` finds it still running and refuses before anything is fetched
or moved, naming `ripcord service restart`.
`ripcord service` shows which build the listener process runs, in its `version` row, and
ends with `ripcord service restart` while that is not the build installed.

## The consequence it prints every time

Updating one host makes the pair disagree, and a failover spanning both is refused while it
does. The command prints that above the prompt, in every pair state, and names the host to run
next. It warns rather than refuses: the operator may be updating *because* of what went wrong,
and what they must not do is find out afterwards.

`ripcord status` shows both builds, so the skew is visible without being refused by it first.

## The trust this rests on

The signing key lives in the release workflow's secret. That stops a replaced asset and a
tampered download; it does **not** stop an account with write access to the repository. That
boundary is stated in [`SECURITY.md`](../../SECURITY.md) rather than implied.

## Exit codes

**3** when the download failed, or a move failed before anything had been set aside — either
way the host is untouched. **4** when the signature did not verify, or a move failed after the
set-aside and the rollback put the old binary back. **5** when the binary was set aside, a
later move failed, *and* the rollback failed too — the message names the file to rename by
hand.

## Looking without installing

`update --dry-run` halts when `updates.install` is off, so on a host allowed to look and not to
install it says nothing about what is published. [`check-update`](check-update.md) is the one
that answers there — it needs only `updates.check` and reads nothing but the feed.

Either of them writes the published version down, and `status` and `check` print one line from
that file. Neither of those two ever looks for itself: a fifteen-second timeout in front of an
unplanned failover is what that design avoids.

## Going back

The binary this replaces is kept beside the new one, and
[`ripcord rollback`](rollback.md) puts it back — without a network, because these hosts have
none. One generation: the copy kept is discarded at the start of the *next* update, which is
why that step comes before anything is moved.
