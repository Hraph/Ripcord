# `ripcord pair`

Sets both certificate thumbprints in `ripcord.yaml` from one line, so nobody edits them by
hand.

```
ripcord pair <host>:<thumbprint> [--local <thumbprint>] [--config <path>] [--dry-run]
```

## How the two hosts are paired

The pair channel is mutual TLS: each host presents its own certificate and accepts only the
other's, both pinned by thumbprint. Each host can read its own certificate and nobody can read
the other's for it, so one value has to cross from one console to the other — by a person,
because that is the step that establishes the trust.

1. On one host, `ripcord service`. Under `THIS HOST'S CERTIFICATE` it prints the line to run
   on the other host:

   ```
     THIS HOST'S CERTIFICATE
       A1B2C3D4E5F60718293A4B5C6D7E8F9012345678  in ripcord.yaml
         CN=HV-DR-01, expires 2027-09-01
       On the other host, run:
         ripcord pair HV-DR-01:A1B2C3D4E5F60718293A4B5C6D7E8F9012345678
   ```

2. On the other host, run that line. It finds its own certificate in `LocalMachine\My`, sets
   `listener.local_certificate_thumbprint` to it and `listener.peer_certificate_thumbprint` to
   the one pasted, and prints the line to carry back.
3. On the first host, run the line it printed.
4. On both, `ripcord service install`, then `ripcord service restart`. Install grants the
   service account read access to the new certificate's private key, and takes it back from
   the old one; restart makes the listener read the file, which it does only when it starts.
   Restart right after install: between the two, the running listener still uses the old key.

Running it again with the same line changes nothing.

## What it writes

The two keys under `listener`, in place, and nothing else: every other line, comment and line
ending stays, a comment at the end of either key's line too. A key missing or commented out is
added under `listener:`, at the section's indentation. The samples' comment about the
placeholders goes with them. A file with no `listener` section gets one at the end,
`enabled: true`. A section that is there without `enabled: true` keeps it, and the output says
the pair channel stays off until it is set. The previous file is kept as
`ripcord.yaml.1`, so the change is undone by moving it back — which is why it asks `y/n`
rather than the node name.

## What it refuses

| | |
|---|---|
| a key from this host | pasted on the host it came from — run it on the other one |
| a key naming another host | than the `peer.hostname` in `ripcord.yaml` |
| the samples' placeholders | they are not a certificate |
| no certificate here | none for `CN=<this host>` with a private key that has not expired |
| several certificates here | none of them configured: choose with `--local <thumbprint>`, as `ripcord service` lists them |
| no `ripcord.yaml` | run `ripcord init` first |
| a `ripcord.yaml` that does not load | its error is printed: fix it first |
| `listener` on one line | flow style (`listener: { ... }`) is left to be edited by hand |

A bare thumbprint, without the host name, is accepted too, in any case, with spaces and with
the invisible mark the Windows certificate dialog copies in front of it; only the name check is
lost.

The file is read back once written. If it does not hold the two thumbprints, the command says
so and names the kept copy to move back.

## Exit codes

**0** written, already set, or `--dry-run`. **2** no key given or a bad option. **4** refused
or declined — nothing was changed. **3** the file could not be written; the output says where
the previous one is. **5** written, but it does not read back with the two thumbprints: move
`ripcord.yaml.1` back.
