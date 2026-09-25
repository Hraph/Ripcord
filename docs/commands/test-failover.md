# `ripcord test-failover`

Boot a replica in isolation, watch it come up, then destroy it. The only rehearsal that does
not touch production.

```
ripcord test-failover (--vm <name> | --all) [--dry-run] [--unattended] [--config <path>]
```

## The sequence

1. `ripcord check` first — a critical finding stops it before anything is created.
2. Start the test failover, which creates a test copy of the VM.
3. Attach it to the isolated switch, or to nothing at all if none is configured.
4. Start it, and wait for a heartbeat with a timeout.
5. Report what booted, how long it took, and what did not.
6. Destroy the test copy — **always**, including after a failure or an interruption.

The copy is destroyed by its own name, as Hyper-V returned it, and the adapter refuses to
destroy any VM whose replication mode is not *test replica*: the replicated VM is production. A
create that fails after Hyper-V made the copy returns no name, so the test VMs are listed
before and after it, and whatever appeared is destroyed. When they could not be listed, the
report says a copy may be left behind; the next run lists it as an orphan.

With `test_failover_switch` set, a copy whose adapters are not on that switch is destroyed
without being started: it is isolated, but it would boot with no network, which is not the
rehearsal the configuration describes.

VMs run one at a time, never in parallel: the target has finite memory and a test copy consumes
real RAM. The second is not created before the first is gone.

## What it refuses, and why it refuses more than asked

A test copy is a byte-for-byte copy. On a production switch it is a duplicate hostname, a
duplicate address, and — for the domain controller — a duplicate directory identity. So
isolation is judged by the **kind** of switch, not by its name: an external switch is the only
kind bridged to a physical NIC, and a second one reaches production just as well.

The refusal applies to every VM, not only to domain controllers. A switch whose ports cannot be
enumerated is reported `Unknown` and refused: *cannot be shown to be isolated* is not
*isolated*.

## Orphans

Hyper-V lists test VMs and never mentions them. A copy left behind by an interrupted run is
reported by age, discriminated on its replication mode rather than on a name suffix — a name is
a convention, and conventions are what a half-finished run breaks.

## `--unattended`

Waives the typed confirmation for VMs the configuration authorises by name, one at a time. It
does not make the run easier: it is **stricter**. Every unevaluable finding blocks an
unattended run, where an attended one blocks on six.

## Exit codes

**1** when a VM did not come up. **4** when the confirmation was declined or the run was
interrupted — nothing was changed. **5** when the cleanup itself failed: a test copy is still
there, holding disk, and a human has to remove it.
