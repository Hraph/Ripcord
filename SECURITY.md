# Security policy

## Reporting a vulnerability

Report it privately, through the repository's **Security → Report a vulnerability** form. Do
not open a public issue, and please do not describe it in a pull request before it is fixed.

A report is more useful than a proof of concept. What helps most: the version
(`ripcord version` prints it), which host the command was run on, and what an attacker would
have to already hold to reach the problem.

## Supported versions

The latest release only. This is a two-host tool that is updated by replacing one file; there
is no branch to backport to.

## What Ripcord assumes

These assumptions decide what counts as a vulnerability. A report that depends on breaking one
of them is a report about the deployment rather than about the tool.

- **`ripcord.yaml` is trusted.** It names the peer, the certificates and the mail relay, so
  whoever can write it can already redirect the pair. It must be readable and writable by
  administrators only. What it must never hold is a secret: the SMTP password is named by
  `password_secret` and read from the environment, and a `password:` key is refused by name.
- **An administrator on either host is out of scope.** They hold Hyper-V; nothing this binary
  does can constrain them. The audit trail records what was known before production moved, and
  it is written append-only — opened to append, never read back, never rewritten. Making it
  *tamper-evident* is a deployment step somebody applies to the file, not something this binary
  enforces: nothing in Ripcord sets that ACL, and the shape it should take is still an open
  question on this project's own list. Do not read the trail as evidence against the
  administrator of the host that wrote it.
- **The network-facing process holds no Hyper-V right.** The listener (`NT SERVICE\ripcord`)
  serves one file and reads nothing else of the host. What reads Hyper-V to write that file is a
  second service, `ripcord-publish`, with no socket at all: a member of Hyper-V Administrators —
  which is control over the VMs, so it is a high-value process — granted modify on `state\` and
  its own log folder only, never beside `ripcord.exe`, and one ACE on the BitLocker namespace
  while the configuration checks BitLocker. `service uninstall` takes all of it back.
- **The two hosts authenticate each other, and nothing else is trusted.** The pair channel is
  mutual TLS with both certificates pinned by thumbprint, restricted to the peer's address by
  both the firewall rule and the tool itself. The peer is trusted to *be* the peer, not to be
  well behaved: what it sends is size-capped, parsed into a fixed schema, and stripped of
  control characters before a human sees it.
- **A release is installed only if it verifies against a key compiled into the binary.**
  `ripcord update` fetches a release and its detached signature, checks the signature with
  ECDSA P-256 over SHA-256 against a public key that is a constant in the host — never a
  configuration key, so editing `ripcord.yaml` cannot change what this host accepts — and
  refuses before anything on the host is touched if the signature is absent, malformed or
  signed by anybody else. The refusal is the feature; there is no mode in which an unverified
  release is installed. The command is **off** unless `updates.install` says otherwise, it is
  never unattended, and it replaces the binary only after the operator has typed the node
  name. The hosts are still expected to have no outbound access at all, and every network
  feature remains off unless the configuration switches it on.

  **What this does not cover**: the signing key lives in the release workflow's secrets, so an
  account that can write to the repository can also sign. That adversary is in the table above,
  and this control does not stop them — it stops a replaced asset and a tampered download. The
  trade is recorded rather than implied.

- **The installer verifies what it installs; nothing verifies the installer.** `install.ps1`
  carries the same public key that is compiled into the binary, because a first install has no
  binary to verify with. It checks the SHA-256 and the detached signature before anything
  lands, and there is no switch to skip that.

  The gap is where the key comes from. Piped straight into a shell —
  `irm ... | iex` — the script and the key it trusts arrive together from the same place, so
  whoever can change one can change both: that install is protected against a swapped release
  asset and a tampered download, and not against the repository itself. Reading the script
  first, or fetching it once with `-Prepare` and copying that folder to the hosts, is what
  turns the embedded key into a trust root that was pinned at a moment somebody chose. The
  offline path is the arrangement these two hosts are meant to run in anyway, and it is the one
  worth using.

  Once a host holds a verified binary, that binary's own key takes over and the installer is out
  of the picture: further releases go through `ripcord update`, and the installer refuses a host
  that already has one rather than quietly reinstalling over it.

## Verifying a release

The published binary is not code-signed. Each release ships `ripcord.exe.sha256`; check it
before putting the file on a host:

```powershell
(Get-FileHash ripcord.exe -Algorithm SHA256).Hash.ToLower()
```

`ripcord version` prints the version and the commit it was built from, which is what tells you
whether the two hosts are running the same binary — a mismatch blocks any sequence that spans
both.
