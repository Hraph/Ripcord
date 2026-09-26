# Releasing, and updating the pair

Two separate things live here: how a release is cut, and how the two hosts are moved onto it.
The second one is the one that can leave you unable to fail over, so it comes first.

A host that has no Ripcord on it yet is a different operation: `install.ps1`, documented in the
[README](../README.md#installation). It verifies the same signature this document tells you to
check by hand, and it refuses a host that already has a binary — that host wants `ripcord
update`, below.

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

There are two ways to do it. `ripcord update` verifies the signature itself and is the shorter
path; the copy by hand is still supported and is what a host with no outbound access does.

### With `ripcord update`, on each host in turn

1. `ripcord status`, then `ripcord check` — start from a healthy pair. If you do not, you will
   not be able to tell afterwards which problem you caused.
2. `ripcord update --dry-run` — read the plan and the consequence it prints.
3. `Stop-Service ripcord-publish, ripcord`, so the file is not held open by either service.
4. `ripcord update`, and answer `y`. It downloads, verifies the signature against the
   key compiled into the running binary, and refuses without touching anything if it does not
   verify.
5. `Start-Service ripcord, ripcord-publish`, then `ripcord version` — the new binary runs
   from here, not from the command that installed it.
6. Move to the other host and repeat.

### By hand, on each host in turn

1. `ripcord status` — confirm the pair is healthy and the peer is answering. Do not start from
   a degraded pair; you will not be able to tell afterwards which problem you caused.
2. `ripcord check` — no criticals. Same reason.
3. Verify the checksum of the new binary against the `.sha256` published beside it, and the
   signature against `ripcord.exe.sig` — the commands are under
   [The release signing key](#the-release-signing-key).
4. Stop both services, so the file is not in use:
   `Stop-Service ripcord-publish, ripcord`
5. Replace `ripcord.exe`. The configuration lives beside the binary and is not touched —
   updating Ripcord is replacing one file.
6. `Start-Service ripcord, ripcord-publish`
7. `ripcord version` — confirm the new version and commit hash. `ripcord service` — its
   `version` row is the build the listener process runs, and must be the same.
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

1. Update `CHANGELOG.md`: move `Unreleased` into `## <version> — <date>`. Nothing enforces
   this; the release page simply says so when it is skipped.
2. Commit and push it to `main`.
3. Run the workflow again with `dry_run` **off**.

**No version number is edited by hand anywhere else.** `Directory.Build.props` says `0.0.0`
and stays there: it is what a working copy reports, not a release. The released version comes
from the tags and the commits, is passed to the publish as `-p:Version=`, and the workflow then
runs the binary it just built and refuses to sign one that does not report it.

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
material, and the changelog stays hand-written.

**It does not gate the release.** A version the changelog describes has its section on the
release page; one it does not describe is released anyway, with "no changelog entry was written
for this version" where the section would have been. Said rather than left blank, because
somebody about to put that binary on a host is owed the sentence.

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

- **No Authenticode signing.** It needs a paid certificate. The detached signature below and the
  SHA-256 stand in, and the gap is stated in the release notes rather than left for someone to
  discover.
- **No unattended update.** `ripcord update` exists and is documented above, but it is never a
  scheduled task: it is off unless the configuration says otherwise, and it replaces the binary
  only after somebody answers `y`. Updating one host makes the pair unfailoverable until
  the other follows, which is not a thing to discover from a log the next morning.
- **No update of the peer from here.** Ripcord drives only the host it is run on (decision D49).
  The command names the host to run next; it does not reach across.

## The release signing key

Every release carries `ripcord.exe.sig`, a detached ECDSA P-256 signature over the binary. It is
what `ripcord update` checks before it installs anything, and a release published without it
cannot be installed — the workflow fails rather than warns.

- **Private half**: `.claude/release-signing-key.pkcs8.pem`, PKCS#8. Gitignored twice over, by
  the `.claude/` rule and by the `*.pem` rule. Keep an offline copy; it is not recoverable.
- **The same bytes** are the repository secret `RIPCORD_SIGNING_KEY`. The workflow writes it to
  a temporary file rather than passing it as an argument, signs, verifies what it just wrote,
  and deletes the file in the same step.
- **Public half, twice**: the `ReleaseSigningKey` constant in
  `src/Ripcord.Host.Windows/Program.cs`, and the same PEM block in `install.ps1`. Two copies
  because they answer at different moments — the binary verifies its own replacement, and the
  installer has no binary yet to verify the first one with. Neither is read from
  `ripcord.yaml`, for the same reason the repository is addressed by numeric id: an attacker
  who can edit the configuration must not be able to change what the host accepts as genuine.
  `tests/install/Install.Tests.ps1` asserts the two copies match, so they cannot drift
  silently.

Signing by hand, to check a build or to sign one the workflow could not:

```
openssl dgst -sha256 -sign .claude/release-signing-key.pkcs8.pem \
  -out ripcord.exe.sig ripcord.exe

openssl pkey -in .claude/release-signing-key.pkcs8.pem -pubout -out pub.pem
openssl dgst -sha256 -verify pub.pem -signature ripcord.exe.sig ripcord.exe
```

**Rotating the key is a manual distribution, once.** A release signed with a new key can only be
installed by a binary that already carries its public half, so the changeover is: generate, edit
**both** constants — `Program.cs` and `install.ps1`, which CI will fail on if only one moves —
cut a release, and copy that one binary to both hosts by hand. Every host still on the old build
will refuse the new releases until it is replaced, which is the pinning working and the reason
to keep the private half safe rather than plan on rotating it.

