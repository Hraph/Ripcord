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

## Ripcord does not update itself

By design, and permanently. A binary that updated itself, with Hyper-V privileges, on both hosts
of a disaster recovery pair, from the internet, is a supply chain that bypasses every control the
rest of this tool is built around — and these hosts should have no outbound access at all.
Fully offline operation stays possible.

The full procedure is in [`docs/RELEASING.md`](RELEASING.md).
