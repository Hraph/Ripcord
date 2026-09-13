# Releasing, and updating the pair

Two separate things live here: how a release is cut, and how the two hosts are moved onto it.
The second one is the one that can leave you unable to fail over, so it comes first.

## Updating the pair

**The pair must not be left on two versions.** The failover sequences are encoded in the binary
and they span both hosts, so a half-updated pair would execute half a sequence written by each
version. A planned failover and a `failback` refuse outright on a mismatch rather than warning —
this is the error class that cannot be recovered from at 3 a.m., so it is prevented rather than
reported.

`failover --scenario unplanned` is the exception, and deliberately: its whole plan runs on the
host it is typed on, so there is no half for the other binary to execute. A disaster failover is
never refused for want of a version string.

The consequence is blunt and worth stating plainly: **between the first host and the second, the
pair cannot be moved electively.** Update when nothing is wrong, never during an incident, and do
not stop halfway.

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

Nothing here fires on a merge. A release is started by hand, from the Actions tab, and there is
no path by which merging something produces a binary.

Run the **release** workflow from the Actions tab, on `main`. It has two inputs:

| Input | Default | What it does |
|---|---|---|
| `version` | blank | Blank means work it out from the commits. Fill it in to overrule that. |
| `dry_run` | **true** | Build everything, publish nothing. |

Leave `dry_run` on for the first run. It reads the conventional commits since the last `v*`
tag, works out the version, writes what is in it into the run summary, compiles the whole
solution, runs the tests, builds the binary and assembles the release notes — then stops,
having created no tag and published nothing. The binary and the notes are attached to the run
as an artefact, so what would have shipped can be read before it does.

The version rule: `feat` moves the minor, `fix` and `perf` the patch, a `!` in the type or a
`BREAKING CHANGE:` footer the major — except while the major is still 0, where a breaking
change moves the minor instead. Reaching 1.0.0 is a decision rather than an arithmetic result,
so it takes an explicit `version` input. A range with nothing but `docs`, `test`, `refactor`,
`chore`, `ci` or `build` in it is refused: there is no behaviour to release.

Then:

1. Update `CHANGELOG.md`: move `Unreleased` into `## <version> — <date>`.
2. Commit and push it to `main`.
3. Run the workflow again with `dry_run` **off**.

The tag is created at the end, by the release itself, pointing at the commit that was built.
The two can therefore never name different commits — and there is no window in which a tag
exists for a build that failed.

### Cutting a tag by hand

Pushing a `v*` tag still works and takes the same path from the build onwards. It is the escape
hatch, not the usual route.

### Why the changelog is not generated

`CHANGELOG.md` is read by somebody about to update a pair they cannot fail over halfway through.
It says what a release means for them and what in it is still unverified — neither of which a
list of commit subjects can say. So the workflow puts the commits in the run summary as the raw
material, and **refuses to release a version the changelog does not describe**. That check runs
before anything is compiled, on both routes in.

### Why the tag is not pushed from a job

A tag pushed with `GITHUB_TOKEN` does not trigger another workflow. A "tag" job feeding a
"release" job would therefore sit there having published nothing, with both jobs green. The tag
is created by `gh release create --target` instead, as part of the release.

The workflow builds the **whole solution** — not only the Linux filter, because
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
