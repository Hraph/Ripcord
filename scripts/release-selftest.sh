#!/usr/bin/env bash
#
# The version rule in release.sh is the one piece of that script that decides something, so it
# is asserted rather than trusted. Run by CI; run it by hand after touching release.sh.

set -euo pipefail

cd "$(dirname "$0")/.."

RELEASE_SH_LIB=1 . scripts/release.sh

RS=$'\036'
failures=0

# Commit records as git produces them: subject, body, record separator.
commits() {
    local record
    for record in "$@"; do
        printf '%s%s' "$record" "$RS"
    done
}

assert_level() {
    local expected="$1"
    shift

    local actual
    actual="$(commits "$@" | bump_level)"

    if [ "$actual" != "$expected" ]; then
        echo "FAIL: expected $expected, got $actual, for: $*" >&2
        failures=$((failures + 1))
    fi
}

assert_version() {
    local base="$1" level="$2" expected="$3"
    local actual
    actual="$(next_version "$base" "$level")"

    if [ "$actual" != "$expected" ]; then
        echo "FAIL: $base + $level should be $expected, got $actual" >&2
        failures=$((failures + 1))
    fi
}

assert_level none
assert_level none 'docs: Describe the failover sequence'
assert_level none 'refactor(domain): Fold two rules into one'
assert_level none 'test(failover): Cover the resume path'

assert_level patch 'fix(cli): Print the failback command'
assert_level patch 'perf(wmi): Read the switches once per host'

assert_level minor 'feat(domain): Add fencing'
assert_level minor 'docs: A note' 'feat(cli): Add the fence verb'

# The strongest commit in the range decides, wherever it sits in the range.
assert_level minor 'feat(domain): Add fencing' 'fix(cli): Correct a message'
assert_level major 'feat!: Rename a configuration key' 'fix(cli): Correct a message'
assert_level major 'refactor(config)!: Drop the legacy block'

# The footer form, which lives in the body rather than the subject.
assert_level major "$(printf 'feat(config): Rename node.hostname\n\nBREAKING CHANGE: an existing ripcord.yaml stops loading.')"
assert_level major "$(printf 'feat(config): Rename node.hostname\n\nBREAKING-CHANGE: an existing ripcord.yaml stops loading.')"

# A body that merely mentions the words is not a footer.
assert_level minor "$(printf 'feat(domain): Add fencing\n\nThis is not a BREAKING CHANGE for anybody.')"

# Scoped and unscoped read the same.
assert_level minor 'feat: Add the fence verb'
assert_level patch 'fix: Correct a message'

# A subject that is not a conventional commit weighs nothing on its own — the script reports it
# separately rather than guessing at what it was.
assert_level none 'Add the fence verb'
assert_level none 'Feat(cli): Add the fence verb'

assert_version 0.4.0 minor 0.5.0
assert_version 0.4.0 patch 0.4.1
assert_version 0.4.2 minor 0.5.0
assert_version 1.2.3 major 2.0.0
assert_version 1.2.3 minor 1.3.0
assert_version 1.2.3 patch 1.2.4
assert_version 0.4.0 none 0.4.0

# Semantic versioning's initial-development rule: while the major is 0, a breaking change moves
# the minor. Reaching 1.0.0 is a decision, not an arithmetic result.
assert_version 0.4.0 major 0.5.0
assert_version 0.0.0 minor 0.1.0

if [ "$failures" -gt 0 ]; then
    echo "$failures assertion(s) failed." >&2
    exit 1
fi

echo "release.sh: version rules hold."
