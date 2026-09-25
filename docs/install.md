# Installing

From an elevated PowerShell, on a host that can reach GitHub:

```powershell
irm https://raw.githubusercontent.com/Hraph/Ripcord/main/install.ps1 | iex
```

It fetches the release, checks the SHA-256, and **verifies the detached ECDSA P-256 signature
against a public key written into the script** — the same key the binary carries. A release
that fails either check is not installed and nothing on the host is touched. There is no
switch to skip that, because a switch to skip verification is the switch somebody uses at
3 a.m. Then it puts `ripcord.exe` in `C:\Program Files\Ripcord`, adds that to the machine
`PATH`, and leaves a `ripcord.yaml` beside it — the annotated sample if it could fetch one, a
deliberately incomplete template otherwise. Either way the file names no peer and no VM, and
every command refuses until you fill it in: those refusals are the checklist. An existing
`ripcord.yaml` is never touched.

One thing to be clear about: `irm | iex` runs a script nobody checked. The script verifies what
it installs; nothing verifies the script. On a host that runs a domain controller that is worth
one moment's thought, and the alternative is below.

**These two hosts are meant to have no outbound access at all**, which is the arrangement the
rest of this tool assumes. For that, the download happens somewhere else:

```powershell
# on a machine with network, which never touches the pair
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/Hraph/Ripcord/main/install.ps1))) -Prepare D:\ripcord-release
```

That verifies the release and leaves a folder holding it, the sample configurations, and a copy
of `install.ps1` — the offline host cannot fetch the installer any more than it can fetch the
binary. Copy the folder to each host and, from an elevated PowerShell there:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -FromPath .
```

The checksum and the signature are checked again on the host. A folder that arrived on a USB
stick is not more trusted than a download.

| Switch | |
|---|---|
| `-Role primary\|dr` | Which sample configuration to place. Asked for if omitted. |
| `-Path <dir>` | Somewhere other than `C:\Program Files\Ripcord`. The `logs` folder moves with it. |
| `-Version v0.1.0` | A particular release rather than the latest. |
| `-CheckTask` | Create the scheduled `ripcord check --notify` task (see [alerting](alerting.md)). |
| `-Shortcut` | A Start Menu shortcut to the dashboard page. |
| `-Force` | Reinstall over an existing binary. It never replaces an existing `ripcord.yaml`. |

**The listener reads `ripcord.yaml` once, when the service starts.** Editing the file later
changes nothing until [`ripcord service restart`](commands/service.md) — the running listener goes on serving the
configuration it was started with, and the peer sees no difference. `ripcord status` and
`ripcord check`, being commands rather than the service, read the file every time they run, so
the two can disagree until the service is restarted.

The listener service runs as `NT SERVICE\ripcord` and may write to one place only:
`logs\` beside the binary, which [`ripcord service install`](commands/service.md) creates and
grants it. Nothing else in the install folder is writable by it, the binary least of all.

Installing is not configuring. `node.hostname`, the peer address and both certificate
thumbprints are per-host, and `ripcord status` refuses a file that names another machine — by
name, at startup. The installer ends by saying so, and by naming the three commands to run in
order.

Updating an installed host is [`ripcord update`](commands/update.md), not this script.
