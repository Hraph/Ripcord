# Releasing, and updating the pair

Two separate things live here: how a release is cut, and how the two hosts are moved onto it.
The second one is the one that can leave you unable to fail over, so it comes first.

## Updating the pair

**The pair must not be left on two versions.** The failover sequences are encoded in the binary
and they span both hosts, so a half-updated pair would execute half a sequence written by each
version. `failover`, `failback` and `reprotect` refuse outright on a mismatch rather than
warning — this is the error class that cannot be recovered from at 3 a.m., so it is prevented
rather than reported.

The consequence is blunt and worth stating plainly: **between the first host and the second, the
pair cannot be failed over.** Update when nothing is wrong, never during an incident, and do not
stop halfway.

Order, on each host in turn:

1. `ripcord status` — confirm the pair is healthy and the peer is answering. Do not start from
   a degraded pair; you will not be able to tell afterwards which problem you caused.
2. `ripcord check` — no criticals. Same reason.
3. Verify the checksum of the new binary against the `.sha256` published beside it.
4. Stop the listener service, so the file is not in use:
   `Stop-Service ripcord-listener`
5. Replace `ripcord.exe`. The configuration lives beside the binary and is not touched —
   updating Ripcord is replacing one file.
6. `Start-Service ripcord-listener`
7. `ripcord version` — confirm the new version and commit hash.
8. Move to the other host and repeat from step 3.

Then, on either host:

9. `ripcord status` — it shows both sides' versions. They must match. If they do not, one host
   did not take the update and the pair is currently unfailoverable.

## Cutting a release

The version comes from the tag and nothing else publishes a binary. There is no path by which
merging something produces a release.

1. Update `CHANGELOG.md`: move `Unreleased` to the new version, with the date.
2. Commit it.
3. Tag: `git tag v0.4.0`
4. Push the tag: `git push origin v0.4.0`

The `release` workflow then builds the **whole solution** — not only the Linux filter, because
the WMI adapter ships in this binary and nothing else checks that it compiles — runs the tests,
publishes a single self-contained `win-x64` executable, and attaches it to a GitHub release with
its SHA-256.

To produce the same binary locally:

```
dotnet publish src/Ripcord.Host.Windows/Ripcord.Host.Windows.csproj \
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

### What is deliberately not here

- **No Authenticode signing.** It needs a paid certificate. The SHA-256 stands in until then,
  and the gap is stated in the release notes rather than left for someone to discover.
- **No auto-update**, permanently. A binary that updated itself, with Hyper-V privileges, on both
  hosts, from the internet, is a supply chain that bypasses every control the rest of this tool
  is built around. These hosts should have no outbound access; fully offline operation must stay
  possible.
- **No `check-update`.** The specification's opt-in version check is a new command rather than
  release plumbing, and belongs to a later milestone.
