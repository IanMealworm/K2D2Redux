#!/usr/bin/env bash
#
# Builds K2D2.dll against the EXACT managed assemblies of an installed
# KSP2 Redux runtime, instead of the Unity/ThunderKit editor pipeline.
#
# Why this exists: the editor pipeline in README.md builds against the
# 26w33a-era Unity SDK (Unity 6000.5.8f1), which is what produced the
# released v1.1.0 binary that fails to load on Redux 0.2.8.5.103184
# (Unity 6000.4.1f1) with TypeLoadException on UnityEngine.UIElements.
# PanelRenderer. This script compiles the same sources against the
# runtime DLLs that actually ship with the game, which is the only way to
# guarantee the compiler sees the same API surface the loader will.
#
# It never modifies anything inside the game installation: the managed
# DLLs are copied (read-only source) into Packages/KSP2_x64, which is the
# staging directory this repository already assumes (see publicize.bat and
# the /[Pp]ackages/KSP2_x64 entry in .gitignore).
#
# Usage:
#   Tools/build.sh
#   KSP2="/path/to/Kerbal Space Program 2" Tools/build.sh
#
# Output:
#   Packages/KSP2_x64/           staged reference assemblies (gitignored)
#   Deploy/K2D2/                 deployable mod folder (K2D2.dll + swinfo.json + assets)
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

KSP2="${KSP2:-$HOME/.local/share/Steam/steamapps/common/Kerbal Space Program 2}"
MANAGED="$KSP2/KSP2_x64_Data/Managed"
STAGING="$REPO_ROOT/Packages/KSP2_x64"
OUT_DIR="$REPO_ROOT/Deploy/K2D2"
OBJ_DIR="$REPO_ROOT/Deploy/obj"
OUT_DLL="$OBJ_DIR/K2D2.dll"

log()  { printf '\033[1;34m[build]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31m[error]\033[0m %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# 1. Sanity-check the runtime we are about to build against
# ---------------------------------------------------------------------------
[ -d "$MANAGED" ] || die "Managed assemblies not found at: $MANAGED
Set KSP2=/path/to/Kerbal Space Program 2 and re-run."

for required in \
    Assembly-CSharp.dll \
    ReduxLib.dll \
    SpaceWarp2.dll \
    SpaceWarp2.UI.dll \
    UitkForKsp2.dll \
    UnityEngine.UIElementsModule.dll \
    UnityEngine.CoreModule.dll \
    Unity.Scripting.dll
do
    [ -f "$MANAGED/$required" ] || die "Missing required runtime assembly: $MANAGED/$required"
done

# The 0.2.9.0.104521 runtime is exactly 220 managed assemblies. A partial set makes every monodis
# answer below read like a missing member, so refuse anything that is not the full set.
DLL_COUNT="$(find -L "$MANAGED" -maxdepth 1 -name '*.dll' | wc -l)"
[ "$DLL_COUNT" = "220" ] || die "Expected the 220-assembly 0.2.9.0.104521 runtime, found $DLL_COUNT DLLs at:
  $MANAGED
Point KSP2= at the KSP 2 Redux 0.2.9.0.104521 install."

# Cache the type/method dumps once. (Captured into variables rather than piped straight into
# `grep -q`: grep -q exits on first match, which SIGPIPEs monodis, and with `pipefail` enabled that
# reads as a failed pipeline - which would invert these checks.)
UIE_TYPEDEFS="$(monodis --typedef "$MANAGED/UnityEngine.UIElementsModule.dll" 2>/dev/null || true)"
UITK_METHODS="$(MONO_PATH="$MANAGED" monodis --method "$MANAGED/UitkForKsp2.dll" 2>/dev/null || true)"

# The runtime must be the exact 0.2.9.0.104521 generation: UnityEngine.UIElementsModule.dll
# defines PanelRenderer, and BOTH UitkForKsp2.API.Window.Create overloads return it. The
# superseded 0.2.8.5 runtime is the inverse (no PanelRenderer; Create returns UIDocument), so a
# wrong $KSP2 is refused here instead of producing a plausible DLL.
[ "$(grep -c 'failed to parse' <<< "$UITK_METHODS" || true)" = "0" ] || die "monodis --method could not resolve UitkForKsp2.dll's signatures.
Pass MONO_PATH as a per-command prefix (never exported) and re-run."
[ "$(grep -cE '^[0-9]+: UnityEngine\.UIElements\.PanelRenderer \(' <<< "$UIE_TYPEDEFS" || true)" = "1" ] \
    || die "UnityEngine.UIElementsModule.dll does not define exactly one UnityEngine.UIElements.PanelRenderer.
This is not the Redux 0.2.9.0.104521 runtime this build targets."
[ "$(grep -c 'PanelRenderer Create' <<< "$UITK_METHODS" || true)" = "2" ] \
    || die "UitkForKsp2.dll does not expose both Window.Create(...) -> PanelRenderer overloads.
This is not the Redux 0.2.9.0.104521 runtime this build targets."
if grep -q 'UIDocument Create' <<< "$UITK_METHODS"; then
    die "UitkForKsp2.dll exposes Window.Create(...) -> UIDocument: the superseded 0.2.8.5 shape.
This build targets Redux 0.2.9.0.104521, where that overload returns PanelRenderer."
fi

GAME_VERSION="$(strings "$MANAGED/../globalgamemanagers" 2>/dev/null \
    | grep -m1 -E '^6000\.[0-9]+\.[0-9]+' || true)"
log "Runtime: ${GAME_VERSION:-unknown Unity version}  ($MANAGED)"

# ---------------------------------------------------------------------------
# 2. Stage every managed DLL as a reference (copy only, never modify source)
# ---------------------------------------------------------------------------
mkdir -p "$STAGING" "$OBJ_DIR" "$OUT_DIR"
log "Staging managed assemblies into Packages/KSP2_x64 ..."
cp -f "$MANAGED"/*.dll "$STAGING"/
log "Staged $(find "$STAGING" -maxdepth 1 -name '*.dll' | wc -l) assemblies."

# ---------------------------------------------------------------------------
# 3. Locate a C# compiler
# ---------------------------------------------------------------------------
# Preference order:
#   1. dotnet (if the user installed the SDK) - fastest, newest Roslyn
#   2. the Roslyn csc.exe bundled with Proton/wine-mono, run under mono
# The project uses no C# language features newer than C# 9, so Roslyn 3.9 is
# sufficient; -langversion pins that assumption instead of leaving it implicit.
find_csc() {
    if command -v dotnet >/dev/null 2>&1; then
        local sdk_dll
        sdk_dll="$(find /usr/share/dotnet /usr/lib/dotnet "$HOME/.dotnet" \
            -path '*/Roslyn/bincore/csc.dll' 2>/dev/null | sort | tail -1 || true)"
        if [ -n "$sdk_dll" ]; then
            echo "dotnet:$sdk_dll"
            return 0
        fi
    fi
    command -v mono >/dev/null 2>&1 || return 1
    local proton_csc
    proton_csc="$(find "$HOME/.local/share/Steam/steamapps/common" \
        -path '*wine/mono*Roslyn/csc.exe' 2>/dev/null | sort | tail -1 || true)"
    [ -n "$proton_csc" ] || return 1
    echo "mono:$proton_csc"
}

CSC_INFO="$(find_csc || true)"
[ -n "$CSC_INFO" ] || die "No C# compiler found. Install the .NET SDK, or the
Mono/Proton toolchain that ships Roslyn csc.exe."
CSC_KIND="${CSC_INFO%%:*}"
CSC_PATH="${CSC_INFO#*:}"

run_csc() {
    case "$CSC_KIND" in
        dotnet) dotnet "$CSC_PATH" "$@" ;;
        mono)   mono "$CSC_PATH" "$@" ;;
    esac
}
log "Compiler: $CSC_KIND ($CSC_PATH)"

# ---------------------------------------------------------------------------
# 4. Build the reference response file
# ---------------------------------------------------------------------------
RSP="$OBJ_DIR/csc.rsp"
: > "$RSP"

{
    echo "-target:library"
    echo "-unsafe"
    echo "-nostdlib+"
    echo "-langversion:9.0"
    echo "-optimize+"
    echo "-nologo"
    # CS0618: deprecated UxmlTraits/base Init members are intentionally used by
    # K2UI's custom controls against this old UI Toolkit; CS0649/0169/0414 are
    # serialization-shaped warnings that do not affect a mod assembly.
    echo "-nowarn:0618,0612,0672,0169,0649,0414"
    echo "-out:\"$OUT_DLL\""
} >> "$RSP"

# Reference every staged DLL: the asmdef's precompiledReferences list is a
# 26w33a-era superset that no longer matches this runtime (it names e.g.
# Countly.Core.dll, iL2CPP-ish extras and Unity.AI.* that are not installed).
# Compiling against the complete installed set is both simpler and stricter:
# any 26w33a-only API then fails to resolve at compile time.
for dll in "$STAGING"/*.dll; do
    echo "-r:\"$dll\"" >> "$RSP"
done

# Only the mod's own code; the editor-only assembly is not part of the runtime
# mod and is skipped (it references UnityEditor, which the game does not ship).
while IFS= read -r src; do
    echo "\"$src\"" >> "$RSP"
done < <(find Assets/K2D2/Code -name '*.cs' | LC_ALL=C sort)  # locale-pinned: deterministic RSP -> reproducible build identity

log "Compiling $(find Assets/K2D2/Code -name '*.cs' | wc -l) source files ..."
set +e
COMPILE_LOG="$OBJ_DIR/compile.log"
run_csc "@$RSP" > "$COMPILE_LOG" 2>&1
COMPILE_STATUS=$?
set -e

if [ $COMPILE_STATUS -ne 0 ]; then
    cat "$COMPILE_LOG" >&2
    die "Compilation failed (see $COMPILE_LOG)."
fi
grep -E 'warning' "$COMPILE_LOG" || true
[ -f "$OUT_DLL" ] || die "Compiler reported success but $OUT_DLL does not exist."

# ---------------------------------------------------------------------------
# 5. Verify the built assembly against the runtime we compiled against
# ---------------------------------------------------------------------------
log "Verifying $OUT_DLL ..."
FAILED=0

TYPEREFS="$(monodis --typeref "$OUT_DLL" 2>/dev/null || true)"
[ -n "$TYPEREFS" ] || die "monodis could not read $OUT_DLL."

# 5a. The built assembly's window-API generation (INVERTED at 0.2.9.0.104521). The superseded gate
#     failed a build for carrying a PanelRenderer reference; here UitkForKsp2.API.Window.Create
#     RETURNS PanelRenderer, so a build bound to this pin's window API must carry it, and any
#     UIDocument reference means csc compiled against a superseded UitkForKsp2.dll.
MEMBERREFS="$(MONO_PATH="$MANAGED" monodis --memberref "$OUT_DLL" 2>/dev/null || true)"
[ -n "$MEMBERREFS" ] || die "monodis could not read $OUT_DLL (--memberref)."
BROKEN_ROWS="$(grep -c 'BROKEN CLASS' <<< "$MEMBERREFS" || true)"
if [ "$BROKEN_ROWS" != "0" ]; then
    warn "monodis --memberref printed $BROKEN_ROWS BROKEN CLASS row(s): its type rows did not resolve,"
    warn "so the window-API generation check below cannot be trusted."
    FAILED=1
fi
if ! grep -q 'PanelRenderer' <<< "$TYPEREFS"; then
    warn "No PanelRenderer typeref in the built assembly - it does not bind this pin's Window.Create."
    FAILED=1
else
    log "PanelRenderer typeref present - bound to the 0.2.9.0.104521 window API."
fi
if grep -q 'UIDocument' <<< "$TYPEREFS$MEMBERREFS"; then
    grep -n 'UIDocument' <<< "$TYPEREFS" >&2 || true
    grep -n 'UIDocument' <<< "$MEMBERREFS" >&2 || true
    warn "Found a UIDocument reference - the built assembly is bound to the superseded 0.2.8.5 window API."
    FAILED=1
fi

# 5b. Preserve must resolve to UnityEngine.CoreModule, not Unity.Scripting
#     (the assembly Unity.Scripting does not export PreserveAttribute).
if grep -q 'PreserveAttribute' <<< "$TYPEREFS"; then
    # monodis --typeref prints "<index>: [AssemblyScope]Namespace.Type"; the assembly scope is what
    # matters here, so strip the index and keep the bracketed name.
    PRESERVE_SCOPE="$(grep -m1 'PreserveAttribute' <<< "$TYPEREFS" \
        | sed -n 's/^[0-9]*: *\[\([^]]*\)\].*/\1/p')"
    log "PreserveAttribute typeref scope: [$PRESERVE_SCOPE]"
    case "$PRESERVE_SCOPE" in
        UnityEngine.CoreModule) : ;;
        *) warn "PreserveAttribute is bound to [$PRESERVE_SCOPE]; expected UnityEngine.CoreModule."
           FAILED=1 ;;
    esac
else
    log "No PreserveAttribute typeref in the assembly."
fi

# 5c. Every assembly reference must exist in the staged runtime.
MISSING_REFS=""
while IFS= read -r ref; do
    [ -n "$ref" ] || continue
    [ -f "$STAGING/$ref.dll" ] || MISSING_REFS="$MISSING_REFS $ref"
done < <(monodis --assemblyref "$OUT_DLL" 2>/dev/null | sed -n 's/^[[:space:]]*Name=\(.*\)$/\1/p')
if [ -n "$MISSING_REFS" ]; then
    warn "Referenced assemblies not present in the runtime:$MISSING_REFS"
    FAILED=1
else
    log "All assembly references resolve against the staged 0.2.8.5 runtime."
fi

[ $FAILED -eq 0 ] || die "Verification failed - refusing to package this build."

# 5d. Loader pre-flight: force resolution of every type/field/property/method against the same
#     runtime assemblies, which is what the game's loader does when it registers the plugin. This
#     catches anything the typeref greps above do not name (e.g. an interface that only exists in
#     the newer Redux snapshots). Skipped with a warning if the toolchain for it is unavailable;
#     it is an extra safety net, not the primary gate.
PROBE_SRC="$REPO_ROOT/Tools/ApiProbe.cs"
if command -v mono >/dev/null 2>&1 && [ -f "$PROBE_SRC" ]; then
    PROBE_LIB="$OBJ_DIR/probe-lib"
    rm -rf "$PROBE_LIB"; mkdir -p "$PROBE_LIB"

    # Copy the staged runtime, minus everything Mono's own BCL provides - otherwise Mono picks up
    # the game's mscorlib and refuses to run ("your mono runtime and class libraries are out of sync").
    cp -f "$STAGING"/*.dll "$PROBE_LIB"/
    if [ -d /usr/lib/mono/4.5 ]; then
        ls /usr/lib/mono/4.5/*.dll 2>/dev/null | xargs -r -n1 basename | sed 's/\.dll$//' | sort -u > "$OBJ_DIR/bcl-names.txt"
    fi
    if [ -s "$OBJ_DIR/bcl-names.txt" ]; then
        ( cd "$PROBE_LIB"
          for f in *.dll; do
              # Written as an if/else rather than `grep && rm` so a non-match cannot trip `set -e`.
              if grep -qxF "${f%.dll}" "$OBJ_DIR/bcl-names.txt"; then
                  rm -f "$f"
              fi
          done )
    fi
    rm -f "$PROBE_LIB/mscorlib.dll" "$PROBE_LIB/netstandard.dll"
    cp -f "$OUT_DLL" "$PROBE_LIB/K2D2.dll"

    # The probe itself must compile against Mono's BCL, not the game's: dropping -nostdlib lets csc.exe
    # (running under Mono) resolve mscorlib/System from Mono's own 4.5 profile.
    if mono "$CSC_PATH" -nologo -out:"$OBJ_DIR/probe.exe" \
            -r:System.dll -r:System.Core.dll "$PROBE_SRC" > "$OBJ_DIR/probe-build.log" 2>&1; then
        if MONO_PATH="$PROBE_LIB" mono "$OBJ_DIR/probe.exe" "$PROBE_LIB/K2D2.dll"; then
            log "Loader pre-flight passed."
        else
            die "Loader pre-flight failed - the runtime cannot resolve this assembly's type graph."
        fi
    else
        warn "Could not build the loader pre-flight probe; skipping (see $OBJ_DIR/probe-build.log)."
    fi
else
    warn "mono not available - skipping the loader pre-flight probe."
fi

# ---------------------------------------------------------------------------
# 6. Assemble the deployable mod folder
# ---------------------------------------------------------------------------
log "Assembling $OUT_DIR ..."
cp -f "$OUT_DLL" "$OUT_DIR/K2D2.dll"
cp -f Assets/K2D2/swinfo.json "$OUT_DIR/swinfo.json"
rm -rf "$OUT_DIR/assets"
# ============================ READ BEFORE CHANGING THIS STEP =============================
# The payload is re-assembled from the project's own tree under Assets/K2D2/Copied/assets on
# every build, and that directory is cleared first, so a stale file cannot survive into a
# release.
#
# HISTORY - the v1.1 bundle trap, and why this comment changed (v1.2.1, 2026-09-15):
#   Assets/K2D2/Copied/assets/bundles/k2d2_ui.bundle USED to hold the released v1.1.0 prebuilt
#   bundle (46,915,664 bytes, md5 2292171b916c2a5086ccebccef533730) - the one the 0.2.8.5
#   player REJECTS. Every build therefore re-poisoned "$OUT_DIR/assets" with a bundle that
#   cannot load, which is why the whole UI-freeze deploy sequence had to be single-file DLL
#   copies with the live assets deliberately left alone. It is now fixed AT SOURCE: the file
#   IS the validated v1.2 bundle (434,641 bytes, md5 1bc0763d844568b0e6b7104ddbe3f2ab),
#   verified byte-identical across Assets/, Deploy/ and the live install. Do not re-introduce
#   the v1.1 file here - if the bundle must change, rebuild it with Tools/build-ui-bundle.sh
#   and stage the result in Assets/K2D2/Copied/assets as well.
#
# *.meta - a recursive `cp -a` of a Unity tree carries Unity's sidecar .meta files, and the
#   player must never see them. This step uses an explicit exclusion, and the gate below fails
#   the build if one ever reaches the payload.
#
# NEVER rsync/cp -a "$OUT_DIR" onto $KSP2/mods/K2D2/. Deploy by explicit file copies only
# (see Deploy/obj/FINAL-REPORT.md / the mod-dev guide section 59.3).
# =========================================================================================
mkdir -p "$OUT_DIR/assets"
tar -C Assets/K2D2/Copied/assets --exclude='*.meta' -cf - . | tar -C "$OUT_DIR/assets" -xf -

# ---------------------------------------------------------------------------
# 6b. Payload gate - no Unity sidecars, nothing missing, nothing stray
#
# This is CommLinesRedux's gate idea with one deliberate difference: the manifest is DERIVED
# from the source tree rather than hard-coded, because K2D2's Addressables bundles are named by
# content hash (019c5304a6655fc0eaeeefdb57daef78.bundle, ...) and those names change whenever a
# bundle is rebuilt. A literal 100-line manifest would fail on every legitimate rebuild and be
# "fixed" by pasting in the new hashes - i.e. it would train the operator to bypass the gate it
# exists to enforce. The *.meta count is absolute, because 0 is the only correct number.
#
# MEASURED LIMIT OF THE DERIVED COMPARISON, and the hole the allow-list below exists to close:
# comparing payload against source catches a file that is in one and not the other, but by
# construction it CANNOT catch a stray file that was added to the SOURCE tree, because the source
# tree is the authority being trusted. Proven by control run on 2026-09-15: planting
# Assets/K2D2/Copied/assets/bundles/STRAY_CONTROL.txt and rebuilding PASSED the manifest
# comparison and reached the payload. The allow-list is what actually rejects it.
# ---------------------------------------------------------------------------
GATE_FAIL=0

for rel in K2D2.dll swinfo.json assets/images/icon.png assets/bundles/k2d2_ui.bundle; do
    if [ ! -f "$OUT_DIR/$rel" ]; then
        printf '\033[1;31m[error]\033[0m payload gate: required file is missing: %s\n' "$rel" >&2
        GATE_FAIL=1
    fi
done

META_COUNT="$(find "$OUT_DIR" -name '*.meta' | wc -l)"
if [ "$META_COUNT" != "0" ]; then
    printf '\033[1;31m[error]\033[0m payload gate: %s *.meta file(s) in the payload:\n' "$META_COUNT" >&2
    find "$OUT_DIR" -name '*.meta' >&2
    GATE_FAIL=1
fi

EXPECTED_PAYLOAD="$( { printf 'K2D2.dll\nswinfo.json\n'; \
    ( cd Assets/K2D2/Copied/assets && find . -type f ! -name '*.meta' \
        | sed 's|^\./||' | sed 's|^|assets/|' ); } | LC_ALL=C sort )"
ACTUAL_PAYLOAD="$(cd "$OUT_DIR" && find . -type f | sed 's|^\./||' | LC_ALL=C sort)"
if [ "$ACTUAL_PAYLOAD" != "$EXPECTED_PAYLOAD" ]; then
    printf '\033[1;31m[error]\033[0m payload gate: manifest mismatch in %s\n' "$OUT_DIR" >&2
    printf 'only in the source tree (missing from the payload):\n' >&2
    comm -13 <(printf '%s\n' "$ACTUAL_PAYLOAD") <(printf '%s\n' "$EXPECTED_PAYLOAD") >&2
    printf 'only in the payload (stray):\n' >&2
    comm -23 <(printf '%s\n' "$ACTUAL_PAYLOAD") <(printf '%s\n' "$EXPECTED_PAYLOAD") >&2
    GATE_FAIL=1
fi

# The path allow-list. This is the check that actually closes the source-tree hole described
# above: every payload path must be one the UI-bundle build is known to emit - the two top-level
# files, the icon, and under assets/bundles/ either a 32-hex-named Addressables bundle (with its
# optional .manifest) or one of the four stable non-hash names (k2d2_ui.bundle, bundles,
# unifiedraytracing, each optionally with .manifest). It survives a bundle rebuild - hashes may
# change freely - while rejecting anything else, including the STRAY_CONTROL.txt that defeated
# the comparison above. Update it ONLY alongside a real change to what Tools/build-ui-bundle.sh
# emits, never to make a red build go green.
ALLOW_RE='^assets/images/icon\.png$|^assets/bundles/([0-9a-f]{32}\.bundle(\.manifest)?|[A-Za-z0-9_]+(\.bundle)?(\.manifest)?)$'
STRAY="$(printf '%s\n' "$ACTUAL_PAYLOAD" | grep -Ev '^K2D2\.dll$|^swinfo\.json$' | grep -Ev "$ALLOW_RE" || true)"
if [ -n "$STRAY" ]; then
    printf '\033[1;31m[error]\033[0m payload gate: path(s) the release does not expect:\n' >&2
    printf '%s\n' "$STRAY" >&2
    printf 'If one of these is genuinely part of the release, extend ALLOW_RE in this script - consciously,\n' >&2
    printf 'and in the same commit as the change that emits it.\n' >&2
    GATE_FAIL=1
fi

[ "$GATE_FAIL" = "0" ] || { printf '\033[1;31m[error]\033[0m payload gate failed - refusing to call this build deployable.\n' >&2; exit 1; }
log "Payload gate: $(printf '%s\n' "$ACTUAL_PAYLOAD" | wc -l) files, 0 *.meta, source tree and payload agree."

log "Done."
log "DLL:     $OUT_DIR/K2D2.dll  ($(stat -c%s "$OUT_DIR/K2D2.dll") bytes)"
log "Package: $OUT_DIR"
log ""
log "Deploy with:"
log "  cp -f \"$OUT_DIR/K2D2.dll\" \"$KSP2/mods/K2D2/K2D2.dll\""
