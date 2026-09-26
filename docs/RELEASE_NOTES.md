## Before you update

**Update both hosts, one after the other, and finish.** Ripcord's failover sequences are encoded
in the binary and they span two hosts, so a pair running two versions would execute half a
sequence written by each. A planned failover and a `failback` therefore **refuse** on a version
mismatch rather than warning — which means a pair left half-updated cannot be moved electively.
The window between the two hosts is the one time the tool will not work.

The one exception is `failover --scenario unplanned`, which runs entirely on the host it is
typed on. There is no half for the other binary to execute, and refusing a disaster failover for
want of a version string the dead host never published would fail at the one thing this tool
exists for.

Do it when nothing is on fire, not during an incident.

## Verify what you are installing

There is no Authenticode signature yet; it needs a paid certificate. Until then the SHA-256
checksum published beside the binary is the only way to tell that the file on the host is the
file that was built. Check it before replacing anything.

```powershell
(Get-FileHash ripcord.exe -Algorithm SHA256).Hash.ToLower()
```

## Updating

**After updating a host, run `ripcord service install --dry-run`, then
`ripcord service install`.** A release can need something new on the host; install applies it,
and restarts a Ripcord service still running the build from before the update. Then the other
host, the same way.

`ripcord update` installs a newer release on the host it is run on. It is **off** unless
`updates.install` says so, it asks `y/n` with Enter declining, and it refuses any release
whose detached signature does not verify against the key compiled into the running binary.
Copying the `.exe` by hand still works and is still supported; fully offline operation stays
possible.

This reverses what earlier versions of this file said. The objection was sound and has not gone
away: a binary that replaces itself, with Hyper-V privileges, on both hosts of a disaster
recovery pair, from the internet, is a supply chain reaching past the controls the rest of the
tool is built around. What changed is that the path now has a trust root that is not the release
page — the signature is checked against a pinned key, and an unverifiable release is refused
before anything is touched. What has **not** changed is that the signing key lives in the
release workflow, so an account with write access to the repository can still sign; see
[`SECURITY.md`](../SECURITY.md).

**Update one host at a time, and update both.** While the two differ, a failover spanning them
is refused — the sequences are encoded in the binary and one executed half by each version is
the error nobody recovers from at 3 a.m. The command says which host to run next.

The full procedure is in [`docs/RELEASING.md`](RELEASING.md).
