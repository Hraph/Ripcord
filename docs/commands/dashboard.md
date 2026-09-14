# `ripcord dashboard`

The same answer as [`check`](check.md), for somebody who did not type a command.

```
ripcord dashboard [--config <path>]
```

Off unless `dashboard.enabled` says otherwise, and bound to `127.0.0.1` by construction —
there is no address key in the configuration, so nothing in a text file can move the page onto
the network.

## What the page is

One self-contained document: no script, no stylesheet, no image, nothing fetched from
anywhere — so it renders on a host with no outbound access. It refreshes itself with a `<meta>`
tag rather than script, so it still refreshes with scripting switched off.

One verdict in one word — `READY`, `DEGRADED`, `NOT READY` or `UNKNOWN` — then both hosts, then
the findings in the order `check` prints them. A violated critical outranks every other reason
the page might be uncertain.

**One reading per refresh.** The page runs the same `check` the console does, once, and lays
that single answer out — never two reads taken a moment apart that can disagree.

A reading that failed still renders, and names what stopped it.

## What it will not do

Nothing on the page acts: no button, no form, no verb but a read. Every request that is not a
`GET` or `HEAD` of `/` is refused, and no other path exists.

Colour repeats the words, never carries them alone — the page has to survive a badly set KVM,
a colour-blind reader and a monochrome print.
