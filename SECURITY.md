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
  is append-only by an ACL set at install time.
- **The two hosts authenticate each other, and nothing else is trusted.** The pair channel is
  mutual TLS with both certificates pinned by thumbprint, restricted to the peer's address by
  both the firewall rule and the tool itself. The peer is trusted to *be* the peer, not to be
  well behaved: what it sends is size-capped, parsed into a fixed schema, and stripped of
  control characters before a human sees it.
- **Nothing is downloaded and nothing is installed.** `ripcord check-update` reports that a
  newer version exists; fetching and installing it is a human act. The hosts are expected to
  have no outbound access at all, and every network feature is off unless the configuration
  switches it on.

## Verifying a release

The published binary is not code-signed. Each release ships `ripcord.exe.sha256`; check it
before putting the file on a host:

```powershell
(Get-FileHash ripcord.exe -Algorithm SHA256).Hash.ToLower()
```

`ripcord version` prints the version and the commit it was built from, which is what tells you
whether the two hosts are running the same binary — a mismatch blocks any sequence that spans
both.
