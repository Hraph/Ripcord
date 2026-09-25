# `ripcord init`

Writes this host's `ripcord.yaml` by asking. Run it again later to change it — every question
arrives already answered with what the file says, and nothing you did not change is lost.

```
ripcord init [--config <path>] [--role primary|dr] [--dry-run]
```

## What it asks

Seven questions, plus two per VM. On a re-run everything has a default and Enter accepts it; on
a first run the role, the other host and each VM's priority must be typed.

| | |
|---|---|
| the role | primary or DR. The pair's two files are mirror images and nothing observable says which way round replication should run. `--role` answers it from the command line. |
| the other host | its name, and the address it answers on — written in full, because it is compared against where a connection came from and it lands in a firewall rule |
| the switch | **chosen from this host's switches**, with what each one reaches |
| which VMs | **chosen from the VMs Hyper-V reports on this host**, with whether each replicates and whether it is running. Answer with numbers from the list or names, separated by commas, or `all` on its own. A number within the list is read as that number first, so a VM named `2019` on a smaller host is still picked by name. `all` is the default on a first run; on a re-run the default is the numbers of the file's VMs still on this host, in the file's order (e.g. `3,1`), and that order is the start order within a priority. A test-failover copy is not offered. On a host with no VM it says so, writes `vms: []` and asks nothing per VM |
| per VM | `P1` or `P2`, and whether it is a domain controller. **P1 fails over first** — a sweep moves every P1 before any P2 and stops at the first VM that fails, and `failover --priority P1` moves that tier alone. `check` makes sure the DR host can start every P1 at once, against its RAM minus the memory reserve (one of the six figures). `check` also reads the pair as failed over only when a P1 runs as primary on the DR host, so at least one VM should be P1. Give P1 to the domain controller and what users cannot work without; P2 to everything that can wait. Answering `y` for a domain controller only makes `check` remind you of the USN rollback risk on every run — it changes no order and adds no guard, so a domain controller should also be P1. Both are explained once, above the first VM |
| six figures | memory reserve, offline-after, frequency, lag multiplier, volume, free-space warning — shown together and accepted in one answer, or unrolled into six prompts |
| update check | whether [`check-update`](check-update.md) may ask GitHub for a newer release. **No** unless the file already said yes: it needs outbound access, which these hosts are meant not to have. `updates.install` is never asked — a re-run keeps it, except when this is answered no, since install cannot be on without it |

An answer the file would not take is refused with the reason and asked again. An address that
is not an address, a peer named as this host, a number not in the list: none of them reaches
the file.

## What it writes

`schema_version`, `node`, `peer`, `replication`, `storage`, `vms` and `updates` — and the result
**validates**. `ripcord status` works straight afterwards. This is the difference from the
template `install.ps1` used to leave behind, which was refused by name until somebody filled
in four blanks.

`node.hostname` is taken from the machine and never asked: a file whose node name is not this
host is refused at startup, so asking would only offer a way to get it wrong.

**The pair channel is not set up here.** `listener` needs two certificate thumbprints that do
not exist yet on a host being configured, so the block is left out — meaning off — and the
closing lines say so. See [`serve`](serve.md). When a re-run carries a `listener` block that
switches it on, the closing lines say `ripcord status, then ripcord service restart` instead:
the listener reads the file only when it starts.

## Running it again

The normal case, and the reason it is safe:

- **every question is pre-answered** from the current file, so Enter through the whole thing
  changes nothing — except for VMs no longer on this host, below. The VM question is
  pre-answered with list numbers, the form it asks for, not with names;
- **every section it does not ask about is carried across untouched** — `listener`,
  `alerting`, `dashboard`, `diagnostics`, `checks`, and any key a later version adds that this
  one has never heard of. Carried as the original lines, comments included, because
  re-serialising through the object model would drop both the comments and every key the model
  does not know. A comment at column 0 belongs to the section below it; an indented one to the
  section it is indented under — the commented `# snapshot_path:` stays in `listener`. The
  indented comments closing a section it rewrites (`replication`'s commented
  `# test_failover_switch:`, say) are written back after it, and the commented-out sections
  after the last one (`# alerting:`, `# diagnostics:`) stay at the end of the file — except the
  `# updates:` example, dropped wherever it sits once `init` writes that section for real. Other
  comments inside a rewritten section are replaced by the ones `init` writes;
- **the previous file is kept** as `ripcord.yaml.1`, named in the output before you say yes;
- **a VM the file names but this host does not have is dropped**, and listed as "No longer on
  this host" above the VM question. It is not offered and not kept. Its entry in
  `unattended_test_failover_vms` goes with it, and so does its entry in
  `checks.acknowledgements` — every command would refuse a file acknowledging a VM it does not
  declare. Each entry taken out is named in amber before the yes. An entry written in flow
  style (`[{ ... }]`) is not recognised: it is listed instead, and the question then defaults
  to `n`, so Enter does not write a file every command refuses. This is what happens over the sample `install.ps1` leaves behind: its `VM-DC-01`, `VM-LEGACY-01` and
  `VM-BACKUP-01` are examples, and only this host's own VMs are offered.

The previous file being kept is why this asks `y/n [y]`, the one prompt where Enter accepts.
Nothing here is irreversible.

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
ask you to type instead. The file's VM names are not offered as a default, since they may be
the sample's examples. Type a name again and what the file said about that VM is kept. The run still completes, and the reason the read failed is in
[the day's diagnostic log](../diagnostics.md), `logs\ripcord-YYYY-MM-DD.log`.
