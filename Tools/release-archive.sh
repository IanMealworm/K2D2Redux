#!/usr/bin/env bash
#
# Assemble the release archive and prove it ships exactly what was gated.
#
# The claim this script exists to make, in bytes rather than in prose:
#
#   every file inside Deploy/release/K2D2-<version>.zip
#     is byte-identical to the same file in Deploy/K2D2/
#   and there is nothing in the archive that is not in the payload
#   and there is nothing in the payload that is not in the archive
#
# It is deliberately a GATE, not a packer: a mismatch exits 1, so a release cannot be cut from a
# stale or polluted staging tree. Ported from CommLinesRedux/Tools/release-archive.sh, which
# proved the shape. Two hazards it closes:
#
#   * *.meta - Unity sidecar files that must never reach the player. Tools/build.sh
#     excludes them and asserts 0 in the payload; this script refuses to ship one as well, so a
#     regression in either place is caught rather than shipped.
#   * a stale payload - the archive is assembled FROM the gated tree on the spot, never from a
#     remembered copy, which is why the archive must not be rebuilt after the final deploy.
#
# NON-DETERMINISM, stated honestly: Roslyn (without -deterministic) writes a fresh PE TimeDateStamp
# and MVID per compile, so K2D2.dll's plain md5 is BUILD-SCOPED. This script's byte-identity claim
# holds within one build; it cannot be re-derived by rebuilding. Every other payload file here
# (the bundle, icon.png, swinfo.json) IS stable, so only the DLL needs a canonical digest to be
# compared across builds.
#
# Usage:
#   Tools/release-archive.sh                 # assemble + verify
#
# Output:
#   Deploy/release/K2D2-<version>.zip                the release payload (top folder K2D2/)
#   Deploy/release/notices/{LICENSE.md,NOTICE.md}    the notices, beside the zip
#   Deploy/release/K2D2-<version>.zip.sha256         digest of the zip itself
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

PAYLOAD="$REPO_ROOT/Deploy/K2D2"
RELEASE="$REPO_ROOT/Deploy/release"
VERSION="$(python3 -c 'import json,sys; print(json.load(open("Deploy/K2D2/swinfo.json"))["version"])')"
ZIP="$RELEASE/K2D2-$VERSION.zip"

[ -d "$PAYLOAD" ] || { printf 'no payload at %s - run Tools/build.sh first.\n' "$PAYLOAD" >&2; exit 1; }

# --- 1. the payload gate, re-checked here so the archive cannot be cut from a polluted tree ----
# Same two checks as Tools/build.sh section 6b (0 *.meta, and every path one the
# UI-bundle build is known to emit), re-run at archive time rather than trusted, because the
# payload directory can be edited between a build and a release cut.
META_COUNT="$(find "$PAYLOAD" -name '*.meta' | wc -l)"
if [ "$META_COUNT" != "0" ]; then
    printf 'FAIL: %s *.meta file(s) in the payload:\n' "$META_COUNT" >&2
    find "$PAYLOAD" -name '*.meta' >&2
    exit 1
fi

for rel in K2D2.dll swinfo.json assets/images/icon.png assets/bundles/k2d2_ui.bundle; do
    [ -f "$PAYLOAD/$rel" ] || { printf 'FAIL: required payload file is missing: %s\n' "$rel" >&2; exit 1; }
done

ACTUAL_PAYLOAD="$(cd "$PAYLOAD" && find . -type f | sed 's|^\./||' | LC_ALL=C sort)"
ALLOW_RE='^assets/images/icon\.png$|^assets/bundles/([0-9a-f]{32}\.bundle(\.manifest)?|[A-Za-z0-9_]+(\.bundle)?(\.manifest)?)$'
STRAY="$(printf '%s\n' "$ACTUAL_PAYLOAD" | grep -Ev '^K2D2\.dll$|^swinfo\.json$' | grep -Ev "$ALLOW_RE" || true)"
if [ -n "$STRAY" ]; then
    printf 'FAIL: payload holds path(s) the release does not expect:\n%s\n' "$STRAY" >&2
    exit 1
fi
printf 'payload: %s files, 0 *.meta, version %s\n' "$(printf '%s\n' "$ACTUAL_PAYLOAD" | wc -l)" "$VERSION"

# --- 2. assemble ---------------------------------------------------------------------------------
mkdir -p "$RELEASE/notices"
rm -f "$ZIP"
( cd "$REPO_ROOT/Deploy" && zip -q -X -r "$ZIP" "K2D2" )
cp -f LICENSE.md "$RELEASE/notices/LICENSE.md"
cp -f NOTICE.md  "$RELEASE/notices/NOTICE.md"
printf 'archive: %s (%s bytes)\n' "$ZIP" "$(stat -c%s "$ZIP")"

# --- 3. extract to a clean scratch path and compare every file -----------------------------------
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
unzip -q "$ZIP" -d "$scratch"

fail=0
while IFS= read -r rel; do
    if ! cmp -s "$PAYLOAD/$rel" "$scratch/K2D2/$rel"; then
        printf 'FAIL: %s differs between payload and archive.\n' "$rel" >&2
        fail=1
    else
        printf 'IDENTICAL  %s  %s\n' "$(md5sum "$PAYLOAD/$rel" | cut -d' ' -f1)" "$rel"
    fi
done < <(cd "$PAYLOAD" && find . -type f | sed 's|^\./||' | LC_ALL=C sort)

while IFS= read -r rel; do
    if [ ! -f "$PAYLOAD/$rel" ]; then
        printf 'FAIL: %s is in the archive but not in the payload.\n' "$rel" >&2
        fail=1
    fi
done < <(cd "$scratch/K2D2" && find . -type f | sed 's|^\./||' | LC_ALL=C sort)

archived=$(cd "$scratch/K2D2" && find . -type f | wc -l)
payload_files=$(cd "$PAYLOAD" && find . -type f | wc -l)
[ "$archived" = "$payload_files" ] || { printf 'FAIL: %s archived files vs %s payload files.\n' \
    "$archived" "$payload_files" >&2; fail=1; }

if [ "$fail" != "0" ]; then
    printf '\nFAIL: the archive is not byte-identical to the gated payload.\n' >&2
    exit 1
fi

# --- 4. the zip's own digest, and the notices that ship beside it ---------------------------------
( cd "$RELEASE" && sha256sum "$(basename "$ZIP")" > "$(basename "$ZIP").sha256" )
printf '\nAll %s files byte-identical (cmp) between Deploy/K2D2/ and the archive.\n' \
    "$payload_files"
printf 'notices beside the zip: %s\n' "$(cd "$RELEASE/notices" && ls -1 | tr '\n' ' ')"
printf 'zip digest: %s\n' "$(cat "$ZIP.sha256")"
printf 'archive is PAYLOAD-ONLY: the notices are not inside it, so the archive stays byte-identical to the deployed tree.\n'
