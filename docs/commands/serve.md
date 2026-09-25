# `ripcord serve`

The listener itself. It is what the Windows service runs; it is not normally typed by a person.

```
ripcord serve [--config <path>]
```

## What it does, and what it deliberately cannot do

It serves one thing, in one direction: the snapshot this host has published. There is no verb,
no parameter and no request body — so there is nothing to abuse. It never touches Hyper-V.
Everything it serves came from a file the privileged `ripcord` wrote.

That split is the point (decision D18): the network-facing service runs as
`NT SERVICE\ripcord`, a virtual account whose only privilege is reading one file.

## Who it answers

Mutual TLS, both certificates pinned by thumbprint. Four things are each required
independently: the chain must be trusted, the thumbprint must be the peer's, the subject must
be the peer's, and the connection must come from the peer's address. Any one of them failing
is a refusal with its own reason in the log.

Revocation is not checked — the pair has its own CA and no reachable distribution point.
Pinning is the revocation mechanism: a compromised certificate is retired by changing
`peer_certificate_thumbprint` on the other host.

One connection at a time, with a fifteen-second deadline that starts when a connection
arrives. A caller that connects and says nothing is let go of; a refused or failed connection
never stops the loop.

## Running as a service

`sc start` waits for the service control manager's handshake. When started by the manager the
binary answers it and runs the same verb until `Stop-Service`; started by a person it behaves
like any other command. A verb that returns on its own — the listener disabled in the
configuration, a file that will not load — **stops** the service rather than leaving it
reported as running with nothing behind it, reports the code it decided to Windows, and writes
the reason to `logs\listener-YYYY-MM-DD.log` beside the binary (UTC date, appended, a banner at
each start). A service has no console; without that file the reason would go nowhere.

The file is opened only after the service has answered Windows, so a log it cannot write
never turns into a 1053. If it cannot be opened, the service writes why to the Application
event log under the source `ripcord` and stops. See [`ripcord service`](service.md#when-it-does-not-start).

Install it with [`ripcord service install`](service.md).
