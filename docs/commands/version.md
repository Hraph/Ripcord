# `ripcord version`

```
ripcord version
```

One line: the version and the commit it was built from.

```
ripcord 0.2.1+abc123def456
```

Both halves matter. Two builds at the same version from different commits differ in exactly the
way nobody thinks to check — and a failover spanning two hosts is refused when the two binaries
do not match, on both halves. A build made outside a repository reports `unknown` for the
commit, which is carried rather than hidden: a host that cannot say what it is running is a
fact, not an error.

[`ripcord status`](status.md) shows the peer's build beside this one, which is where a
mismatch is normally noticed.
