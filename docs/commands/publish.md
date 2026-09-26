# `ripcord publish`

Publishes this host's snapshot — the file the listener serves to the other host — once.

```
ripcord publish [--config <path>]
```

## Why it exists

The other host sees this one through `state\state.json`, and only as fresh as its last write.
The listener that serves it faces the network and never reads Hyper-V (D18), so something else
has to write it. `ripcord status` and `check` do, when an administrator runs them; between two
runs the peer's view ages until it reads `STALE`.

`service install` therefore installs a second service, **`ripcord-publish`**, which runs this
command every **15 seconds**. By hand, it publishes once and says where — the way to see why the
service does not.

## The publishing service

| | |
|---|---|
| account | `NT SERVICE\ripcord-publish`, a virtual account: no password, never used to sign in; across the network it acts as the computer's own account, and the publisher opens no socket |
| Hyper-V | member of **Hyper-V Administrators** (found by its SID, `S-1-5-32-578`, so the French "Administrateurs Hyper-V" too): reading the VMs' replication needs it. The listener is never a member |
| BitLocker | one ACE on `root\cimv2\Security\MicrosoftVolumeEncryption`, enable and execute methods only — and only while `storage.check_bitlocker_autounlock` is on |
| files | read on the files of the install folder (`ripcord.yaml`), **modify on `state\` only** — never beside `ripcord.exe` — and modify on `logs\publish\`, which the listener is kept out of |
| socket | none |

When BitLocker still cannot be read — the provider may insist on an administrator whatever the
namespace allows — the last state an administrator's `status` or `check` read is carried
forward **with its date**, rather than published as unknown. The other host's `check` then says
"as read <date> UTC".

## Its log

`logs\publish\publish-YYYY-MM-DD.log` (UTC date): a start line with the build and the
configuration, the first result, every change between published and not (a failure with its
full reason, then how long it lasted), a change in what the read could not see, **one line an
hour** whatever the state — *240 snapshot(s) published in the last hour, the last at 08:59:47
UTC* — and the stop. A failure that comes back every fifteen seconds is written once an hour,
with how many times it was held back. `ripcord service` shows its last ten lines.

## Exit codes

**0** published, or the listener is disabled — nothing would be served. **2** the
configuration cannot be used. **3** this host could not be read, or the file could not be
written; the reason is printed.
