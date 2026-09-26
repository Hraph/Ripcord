# Commands

One page per verb. Each says what the command answers, what it refuses, and what its exit code
means — in that order, because that is the order they matter in at three in the morning.

| Command | What it answers | Changes anything |
|---|---|---|
| [`status`](status.md) | what both hosts are doing right now | no |
| [`check`](check.md) | would a failover work, if it had to happen now | no |
| [`service`](service.md) | is the listener running, and if not, why (`service status` is the same) | only with `install`, `uninstall`, `restart`, `start`, `stop` |
| [`pair`](pair.md) | set both certificate thumbprints from the line `service` prints on the other host | yes |
| [`serve`](serve.md) | — it *is* the listener | no |
| [`publish`](publish.md) | publish this host's snapshot once; as the `ripcord-publish` service, every 15 s | no |
| [`dashboard`](dashboard.md) | the same answer as `check`, in a browser | no |
| [`test-failover`](test-failover.md) | would this VM actually boot on the other host | yes, and undoes it |
| [`failover`](failover.md) | move a VM to the other host | yes |
| [`failback`](failback.md) | move it home again | yes |
| [`fence`](fence.md) | stop a returning host from starting its old copies | yes |
| [`update`](update.md) | install a newer release on this host | yes |
| [`check-update`](check-update.md) | is a newer release published | no |
| [`version`](version.md) | which binary is this | no |

Every command takes `--config <path>`; without it the configuration is `ripcord.yaml` beside
the binary. Every mutating command takes `--dry-run`, and every one of them prints the plan and
stops when given it.

## Exit codes

They are the same everywhere, and a scheduled task can act on them.

| Code | Meaning |
|---|---|
| 0 | success — **including an unreachable peer**, which is a degraded state and not an error |
| 1 | at least one critical rule is violated (`check`), or a test failover did not come up |
| 2 | invalid invocation, or invalid or missing configuration |
| 3 | local access failure — WMI, privileges, a timeout |
| 4 | refused, or interrupted — **nothing was changed** |
| 5 | a mutating operation left the host between two states — **a human has to look** |

The last two are the ones that matter after a failure. 4 says the host is where it was; 5 says
it is not, and names what to do about it. Re-running is safe either way: every mutating command
re-derives where it is from what the hosts report, never from a stored position.

## Starting from nothing

A host with a binary and no `ripcord.yaml` says so, and names the one command that produces
one: [`ripcord init`](init.md) interviews you and writes a file that validates. Run it again
whenever something changes — it re-asks with the current answers filled in and keeps everything
it did not ask about.

## Colour

Rendered blocks carry colour when the console says it understands escape sequences, and never
otherwise. Nothing is said in colour alone — `Critical`, `STALE` and `CRITICAL` are the words
that carry the meaning, and colour only makes them easier to find. So the block reads
identically without it, which is what it is:

- redirected to a file — `ripcord status > state.txt`;
- written by the listener service into `logs\listener\listener-YYYY-MM-DD.log`;
- on a console whose `SetConsoleMode` refuses virtual-terminal processing;
- with `NO_COLOR` set to anything, or `--no-color` on the command line.

## When one line is not enough

Every command writes what it ran, what it exited with, and the full exception behind any
failure to `logs\ripcord-YYYY-MM-DD.log` beside the binary, one file a day kept for 30 days.
The console has room for one sentence; that file
has the type, the stack and the CIM error, and it is written to be sent — secrets are taken out
on the way in. See [the diagnostic log](../diagnostics.md).

## Two rules that shape all of this

**Read-only by default.** Nothing mutates without an explicit answer, and the answer matches the
stakes. What moves production VMs — `failover`, `failback`, `fence` — takes **the node name
typed in full**. Everything else that changes something — `service install`, `uninstall`, `stop`,
`test-failover`, `update`, `rollback` — asks **`y/n [n]`**: it is undone by running it again or
touches only a test VM, and Enter declines. Keeping the typed name for the few commands that
need it keeps it from becoming a reflex. There is no `--force` anywhere, and no flag that skips
a verification.

**Never silent.** A degraded state is printed, not swallowed. A rule that could not be
evaluated is listed as unevaluated, never as satisfied: a reassuring false negative is the
worst thing this tool can produce.
