# `ripcord init`

Writes this host's `ripcord.yaml` by asking. Run it again later to change it — every question
arrives already answered with what the file says, and nothing you did not change is lost.

```
ripcord init [--config <path>] [--role primary|dr] [--dry-run]
```

## What it asks

Six questions, plus two per VM. Everything has a default; Enter accepts it.

| | |
|---|---|
| the role | primary or DR. The pair's two files are mirror images and nothing observable says which way round replication should run. `--role` answers it from the command line. |
| the other host | its name, and the address it answers on — written in full, because it is compared against where a connection came from and it lands in a firewall rule |
| the switch | **chosen from this host's switches**, with what each one reaches |
| which VMs | **chosen from this host's VMs**, with whether each replicates and whether it is running. `all` is the default |
| per VM | `P1` or `P2`, and whether it is a domain controller |
| six figures | memory reserve, offline-after, frequency, lag multiplier, volume, free-space warning — shown together and accepted in one answer, or unrolled into six prompts |

An answer the file would not take is refused with the reason and asked again. An address that
is not an address, a peer named as this host, a number not in the list: none of them reaches
the file.

## What it writes

`schema_version`, `node`, `peer`, `replication`, `storage` and `vms` — and the result
**validates**. `ripcord status` works straight afterwards. This is the difference from the
template `install.ps1` used to leave behind, which was refused by name until somebody filled
in four blanks.

`node.hostname` is taken from the machine and never asked: a file whose node name is not this
host is refused at startup, so asking would only offer a way to get it wrong.

**The pair channel is not set up here.** `listener` needs two certificate thumbprints that do
not exist yet on a host being configured, so the block is left out — meaning off — and the
closing lines say so. See [`serve`](serve.md).

## Running it again

The normal case, and the reason it is safe:

- **every question is pre-answered** from the current file, so Enter through the whole thing
  changes nothing;
- **every section it does not ask about is carried across untouched** — `listener`,
  `alerting`, `dashboard`, `diagnostics`, `checks`, and any key a later version adds that this
  one has never heard of. Carried as the original lines, comments included, because
  re-serialising through the object model would drop both the comments and every key the model
  does not know;
- **the previous file is kept** as `ripcord.yaml.1`, named in the output before you say yes.

That last point is why this asks `[Y/n]` rather than for the node name typed in full, as every
irreversible command does. Nothing here is irreversible.

Fields inside the sections it *does* rewrite are carried too, not just whole sections: a VM's
`failover: manual`, `has_passthrough_disk`, `expected_startup_ram_mb` and
`guest_os_support_ends`, and `replication`'s `test_failover_switch`,
`test_failover_orphan_after_hours` and `unattended_test_failover_vms`. None of them is asked
about and none is invented — whatever the file said comes back.

**The one thing it does not carry** is a key this version has never heard of *inside* a section
it rewrites — `replication.something_added_next_year`. Whole sections survive that; fields
inside a rewritten one do not. The previous file is at `ripcord.yaml.1` either way, which is
what that file is for.

## When it refuses

| | |
|---|---|
| nothing is answering — a pipe, a scheduled task, the service | **4**, nothing written. An interview that waits for ever on a host nobody is sitting at is worse than one that will not start |
| you answer `n` at the end | **4**, nothing written, the previous file where it was |
| the file cannot be written | **3**, with the reason |

`--dry-run` runs the whole interview and prints the file instead of writing it.

If this host's Hyper-V cannot be read, the switch and VM questions have no list to offer and
ask you to type instead. The run still completes, and the reason the read failed is in
[the day's diagnostic log](../diagnostics.md), `logs\ripcord-YYYY-MM-DD.log`.
