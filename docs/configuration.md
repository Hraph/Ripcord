# `ripcord.yaml`

One file per host, beside the binary. The two are mirror images: the `node` and `peer` blocks
are swapped, and copying one across without swapping them is refused at startup, by name.

Every command validates the whole file before doing anything, and reports **every** error at
once — failing on the first one means re-running once per typo, which is not what anyone wants
at three in the morning.

The shipped samples in [`config/`](../config) carry a comment on every key. This page is the
reference; they are the starting point.

## Where it comes from

[`ripcord init`](commands/init.md) writes one by asking, reading this host's switches and VMs
to offer them rather than expecting them from memory. Run it again to change anything: it
re-asks with the current answers in place, keeps every section it does not ask about, and
leaves the previous file as `ripcord.yaml.1`.

## Required

```yaml
schema_version: 1

node:
  hostname: HV-REPLICA-01        # must be this machine's name
  host_memory_reserve_gb: 4      # left for the host itself in the capacity arithmetic

peer:
  hostname: HV-PRIMARY-01        # the other host; must not be this one
  address: 192.0.2.11            # an IP address written in full
  offline_after_sec: 120         # how long silence means offline. Not a socket timeout

replication:
  expected_role: replica         # primary | replica — which side this host normally is
  expected_switch_name: vSwitch-PROD
  expected_frequency_sec: 30
  lag_warning_multiplier: 3
  health_warning_after_sec: 300  # optional, default 5 minutes

storage:
  data_volume: "D:"
  free_space_warning_gb: 200
  check_bitlocker_autounlock: true

vms:
  - name: VM-DC-01
    priority: P1                 # P1 | P2 — failover order
    is_domain_controller: true
    expected_startup_ram_mb: 2048
```

`peer.address` must parse as an address **and** render back to what was typed: `192.0.2` parses
as `192.0.0.2`, and a typo that becomes a different valid address would open a firewall rule to
a host nobody named.

`expected_role` cannot be observed — the two files are mirror images and nothing in Hyper-V
says which way round the pair is meant to be. It is the baseline that "the direction is
inverted" is judged against; the operating mode itself is always derived from what is observed.

## Per-VM

| Key | Effect |
|---|---|
| `priority` | `P1` comes back first. A sweep moves P1 before P2 |
| `is_domain_controller` | USN rollback guard: reported by `check`, and it shapes what a test failover refuses |
| `has_passthrough_disk` | a pass-through disk is not replicated by Hyper-V Replica |
| `expected_startup_ram_mb` | compared against what the target reports; a drift is a warning |
| `guest_os_support_ends` | nothing in WMI knows when patches stop; this date is the only source |
| `failover` | `auto` (default), `manual` — skipped by sweeps, taken when named — or `never`, refused even when named |

## Optional blocks

**`listener`** — the pair channel. Remove it, or set `enabled: false`, and the node degrades to
the local-only view rather than failing. Needs both certificate thumbprints; the same
thumbprint on both ends is refused, because it would authenticate a host to itself.
`snapshot_path` is the snapshot `ripcord status` writes and the listener serves to the peer; it
defaults to `state.json` beside `ripcord.yaml` and is best left out. A path with no folder in
it is refused, and one on a drive the host lacks is refused by `service install`.

**`checks.acknowledgements`** — findings seen and accepted. Every entry needs a reason and an
expiry: without one, an acknowledgement is a rule deleted by the back door.

**`alerting`** — see [alerting](alerting.md). Off by default.

**`updates`** — `check` and `install`, two switches. See
[`check-update`](commands/check-update.md) and [`update`](commands/update.md).

**`dashboard`** — see [`dashboard`](commands/dashboard.md). Off by default, no address key.

**`diagnostics`** — see [the diagnostic log](diagnostics.md). **On** by default, unlike every
other optional block, and the only section that refuses nothing it is given. `path` names a
folder (an old value naming a `.log` file is read as its folder); the listener service ignores
it and always writes to `logs\` beside the binary.

## After an edit

The listener reads this file **once, when it starts**: run
[`ripcord service restart`](commands/service.md). `status` and `check` re-read it every time,
so the two can disagree until the service is restarted.
