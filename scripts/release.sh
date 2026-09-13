#!/usr/bin/env bash
#
# Proposes the next version from the conventional commits since the last tag, shows what is in
# it, and creates the tag. It never pushes: pushing the tag is what publishes a binary, and
# that stays a deliberate act by a person.
#
# The changelog is deliberately *not* generated. This project's CHANGELOG.md is written prose
# that says why a change matters and what is still unverified — a list of commit subjects would
# be strictly worse than what is already there. So the commits are shown as the input for
# writing that section, and the tag is refused until the section exists.

set -euo pipefail

usage() {
    cat <<'USAGE'
Usage: scripts/release.sh [--dry-run] [--version X.Y.Z]

  --dry-run          work out the version and show what is in it; create nothing
  --version X.Y.Z    tag this version instead of the one the commits imply

Reads the conventional commits since the last v* tag, derives the next version, checks that
CHANGELOG.md has a section for it, and creates the annotated tag. Push it yourself:

  git push origin v<version>
USAGE
}

dry_run=false
forced_version=""

while [ $# -gt 0 ]; do
    case "$1" in
        --dry-run) dry_run=true ;;
        --version)
            [ $# -ge 2 ] || { echo "release: --version needs X.Y.Z" >&2; exit 2; }
            forced_version="$2"
            shift
            ;;
        -h|--help) usage; exit 0 ;;
        *) echo "release: unexpected argument '$1'" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

cd "$(git rev-parse --show-toplevel)"

# The record separator. Commit bodies contain blank lines, so the commits are read as
# RS-delimited records rather than split on newlines — a footer is part of its own commit.
RS=$'\036'

# feat -> minor, fix and perf -> patch, a `!` or a BREAKING CHANGE footer -> major. Everything
# else releases nothing on its own, which is why a run of docs commits is refused rather than
# tagged as a patch that changed no behaviour.
#
# Reads RS-separated commit records on stdin so it can be tested without a repository.
bump_level() {
    awk '
        BEGIN { RS = "\036"; major = 0; minor = 0; patch = 0 }
        {
            record = $0
            sub(/^\n+/, "", record)
            newline = index(record, "\n")
            subject = (newline > 0) ? substr(record, 1, newline - 1) : record

            if (subject == "") next
            if (subject ~ /^[a-z]+(\([^)]*\))?!:/) { major = 1; next }
            if (record ~ /(^|\n)BREAKING[ -]CHANGE:/) { major = 1; next }
            if (subject ~ /^feat(\([^)]*\))?:/) { minor = 1; next }
            if (subject ~ /^(fix|perf)(\([^)]*\))?:/) { patch = 1; next }
        }
        END {
            if (major) print "major"
            else if (minor) print "minor"
            else if (patch) print "patch"
            else print "none"
        }
    '
}

# Semantic versioning's own rule for initial development: while the major is 0, nothing is
# promised, so a breaking change moves the minor. Bumping 0.4.0 to 1.0.0 by accident would
# announce a stability this has not earned — and 1.0.0 is a decision, not an arithmetic result.
next_version() {
    local base="$1" level="$2"
    local major minor patch

    IFS=. read -r major minor patch <<<"$base"

    case "$level" in
        major)
            if [ "$major" -eq 0 ]; then
                echo "0.$((minor + 1)).0"
            else
                echo "$((major + 1)).0.0"
            fi
            ;;
        minor) echo "$major.$((minor + 1)).0" ;;
        patch) echo "$major.$minor.$((patch + 1))" ;;
        *) echo "$base" ;;
    esac
}

# Sourced by the self-test, which wants the two functions above and none of the work below.
[ "${RELEASE_SH_LIB:-}" = "1" ] && return 0

branch="$(git rev-parse --abbrev-ref HEAD)"

if [ "$branch" != "main" ]; then
    echo "release: on branch '$branch'; releases are cut from main." >&2
    exit 1
fi

# A tag names a commit, not a working tree. Tagging with uncommitted changes produces a binary
# that matches nothing anybody can check out.
if [ -n "$(git status --porcelain)" ]; then
    echo "release: the working tree is not clean; commit or stash first." >&2
    exit 1
fi

last_tag="$(git tag --list 'v[0-9]*' --sort=-v:refname | head -n 1)"

if [ -n "$last_tag" ]; then
    base_version="${last_tag#v}"
    range="$last_tag..HEAD"
else
    base_version="0.0.0"
    range="HEAD"
fi

records="$(git log --format="%s%n%b${RS}" "$range")"

if [ -z "$(git log --format=%H "$range")" ]; then
    echo "release: nothing has been committed since $last_tag." >&2
    exit 1
fi

level="$(printf '%s' "$records" | bump_level)"

if [ -n "$forced_version" ]; then
    version="$forced_version"
else
    version="$(next_version "$base_version" "$level")"
fi

if ! printf '%s' "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then
    echo "release: '$version' is not MAJOR.MINOR.PATCH." >&2
    exit 1
fi

tag="v$version"

if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
    echo "release: $tag already exists." >&2
    exit 1
fi

section() {
    local title="$1" pattern="$2"
    local lines
    lines="$(git log --format='%s' "$range" | grep -E "$pattern" || true)"

    [ -n "$lines" ] || return 0

    printf '\n%s\n' "$title"
    printf '%s\n' "$lines" | sed 's/^/  /'
}

echo "From ${last_tag:-the first commit} to HEAD: $level"
echo "  $base_version -> $version"

section "Breaking" '^[a-z]+(\([^)]*\))?!:'
section "Features" '^feat(\([^)]*\))?:'
section "Fixes" '^(fix|perf)(\([^)]*\))?:'
section "Everything else" '^(docs|test|refactor|chore|ci|build|style)(\([^)]*\))?:'

# Conventional Commits are mandatory in this project, so a subject that does not parse is not a
# style preference — it is a change this script could not weigh, and the version it just
# proposed may be wrong because of it.
unconventional="$(
    git log --format='%s' "$range" \
        | grep -Ev '^(feat|fix|docs|refactor|test|chore|ci|build|perf|style)(\([^)]*\))?!?: ' \
        || true
)"

if [ -n "$unconventional" ]; then
    printf '\nNOT CONVENTIONAL COMMITS — these were not weighed:\n' >&2
    printf '%s\n' "$unconventional" | sed 's/^/  /' >&2
fi

# The gate that keeps the hand-written part honest. CHANGELOG.md says what a release means for
# somebody about to put it on two hosts, including what is still unverified; a generated list of
# subjects cannot say that, so the tag waits until a person has written it.
# The heading carries the date — `## 0.5.0 — 2026-09-13` — so the version is matched as a whole
# word at the start of it rather than as the whole line, and still anchored: `## 0.5.0` must not
# be satisfied by `## 0.5.01`.
if ! grep -Eq "^## ${version//./\\.}([^0-9.]|\$)" CHANGELOG.md; then
    cat >&2 <<EOF

release: CHANGELOG.md has no '## $version' section.

  Move what is under '## Unreleased' into '## $version — <today>', commit it, and run this
  again. The release notes are read by somebody about to update a pair they cannot fail
  over halfway through; the commit subjects do not tell them what they need.
EOF
    exit 1
fi

if $dry_run; then
    printf '\nNothing was created. Re-run without --dry-run to tag %s.\n' "$tag"
    exit 0
fi

git tag -a "$tag" -m "ripcord $version"

cat <<EOF

Created $tag. It is local: nothing is published until you push it.

  git push origin $tag

That fires the release workflow, which builds the whole solution, runs the tests, and attaches
the single-file binary and its SHA-256 to a GitHub release.
EOF
