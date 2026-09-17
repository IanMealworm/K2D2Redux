#!/usr/bin/env bash
#
# Builds the temporary K2-D2 UI diagnostic mod (Tools/diag/DiagPlugin.cs) into
# Deploy/K2D2Diag/K2D2Diag.dll, ready to copy to $KSP2/Mods/K2D2Diag/.
#
# It is NOT part of K2D2.dll and is never shipped. It exists so a single game launch can
# answer questions about the BUILT asset bundle that no amount of editor-side checking can
# answer (the Phase-2 gap: the project instantiated the UXML fine, the shipped bundle did not).
#
# The compiler is the same one Tools/build.sh picks (Roslyn csc from the .NET SDK or
# from Proton/wine-mono), and the reference set is the same staged/managed runtime DLLs, so
# this assembly binds against exactly what the game loads.
#
# USAGE
#   Tools/build-diag.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

OBJ_DIR="$REPO_ROOT/Deploy/obj"
SRC="$REPO_ROOT/Tools/diag/DiagPlugin.cs"
OUT_DIR="$REPO_ROOT/Deploy/K2D2Diag"
OUT_DLL="$OUT_DIR/K2D2Diag.dll"

log()  { printf '\033[1;34m[diag]\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31m[diag]\033[0m %s\n' "$*" >&2; exit 1; }

[ -f "$SRC" ] || fail "missing $SRC"
mkdir -p "$OBJ_DIR" "$OUT_DIR"

# --- compiler (same detection as Tools/build.sh) --------------------------------
find_csc() {
    if command -v dotnet >/dev/null 2>&1; then
        local sdk_csc
        sdk_csc="$(find "${DOTNET_ROOT:-/usr/share/dotnet}" "$HOME/.dotnet" \
            -path '*/Roslyn/bincore/csc.dll' 2>/dev/null | sort | tail -1 || true)"
        if [ -n "$sdk_csc" ]; then echo "dotnet:$sdk_csc"; return 0; fi
    fi
    local proton_csc
    proton_csc="$(find "$HOME/.local/share/Steam/steamapps/common" \
        -path '*wine/mono*Roslyn/csc.exe' 2>/dev/null | sort | tail -1 || true)"
    [ -n "$proton_csc" ] || return 1
    echo "mono:$proton_csc"
}

CSC_INFO="$(find_csc || true)"
[ -n "$CSC_INFO" ] || fail "No C# compiler found (see Tools/build.sh for the
toolchain this repo expects)."
CSC_KIND="${CSC_INFO%%:*}"
CSC_PATH="${CSC_INFO#*:}"

run_csc() {
    case "$CSC_KIND" in
        dotnet) dotnet "$CSC_PATH" "$@" ;;
        mono)   mono "$CSC_PATH" "$@" ;;
    esac
}

log "Compiler: $CSC_KIND ($CSC_PATH)"

# --- managed reference set ---------------------------------------------------------------
MANAGED_DIR="${K2D2_MANAGED:-$REPO_ROOT/Packages/KSP2_x64}"
if [ ! -f "$MANAGED_DIR/ReduxLib.dll" ]; then
    MANAGED_DIR="$HOME/.local/share/Steam/steamapps/common/Kerbal Space Program 2/KSP2_x64_Data/Managed"
fi
[ -f "$MANAGED_DIR/UitkForKsp2.dll" ] || fail "no UitkForKsp2.dll under $MANAGED_DIR"
log "Managed references: $MANAGED_DIR"

RSP="$OBJ_DIR/csc-diag.rsp"
: > "$RSP"
{
    echo "-target:library"
    echo "-nostdlib+"
    echo "-langversion:9.0"
    echo "-optimize+"
    echo "-nologo"
    echo "-nowarn:0618,0612,0672,0169,0649,0414"
    echo "-out:\"$OUT_DLL\""
} >> "$RSP"

# The mod references the whole installed runtime set (as build.sh does): it uses
# UnityEngine, UnityEngine.UIElements, UnityEngine.TextCoreTextEngineModule, UitkForKsp2 and
# ReduxLib, and compiling against the complete set keeps the binding identical to K2D2.dll's.
for dll in "$MANAGED_DIR"/*.dll; do
    echo "-r:\"$dll\"" >> "$RSP"
done
echo "\"$SRC\"" >> "$RSP"

log "Compiling $SRC ..."
set +e
COMPILE_LOG="$OBJ_DIR/compile-diag.log"
run_csc "@$RSP" > "$COMPILE_LOG" 2>&1
COMPILE_STATUS=$?
set -e

if [ $COMPILE_STATUS -ne 0 ]; then
    cat "$COMPILE_LOG" >&2
    fail "diagnostic mod compilation failed (see $COMPILE_LOG)."
fi
grep -E 'warning' "$COMPILE_LOG" || true
[ -f "$OUT_DLL" ] || fail "compiler reported success but $OUT_DLL does not exist."

# --- payload: swinfo.json next to the DLL (SpaceWarp2 mod descriptor) --------------------
cat > "$OUT_DIR/swinfo.json" <<'EOF'
{
  "spec": "2.0",
  "mod_id": "K2D2Diag",
  "name": "K2-D2 UI Diagnostic",
  "author": "local",
  "description": "Temporary diagnostic probe for AssetBundle/UI Toolkit font compatibility. Remove after use.",
  "source": "",
  "version": "1.0.0",
  "version_check": "",
  "ksp2_version": { "min": "*", "max": "*" },
  "dependencies": [
    { "id": "SpaceWarp2", "version": { "min": "2.0.0", "max": "*" } }
  ],
  "conflicts": [],
  "main_assembly": "K2D2Diag.dll"
}
EOF

mkdir -p "$OUT_DIR/variants"
log "Wrote $OUT_DLL ($(stat -c %s "$OUT_DLL") bytes)"
log "Payload: $OUT_DIR (swinfo.json + K2D2Diag.dll + variants/)"
log ""
log "Deploy with:"
log "  cp -f \"$OUT_DLL\" \"\$KSP2/Mods/K2D2Diag/\""
