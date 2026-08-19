#!/usr/bin/env bash
#
# Parity check: does the Unity C# simulation produce the same numbers as the
# Three.js JavaScript one?
#
# Compiles the Pancing.Sim sources with the Roslyn compiler that ships inside
# the Unity editor, so this needs no .NET SDK — if you can open the project in
# Unity Hub, you can run this.
#
#   bash shared/parity/run.sh
#
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
SIM="$ROOT/unity/Pancing/Assets/Pancing/Sim"
BUILD="$HERE/.build"

# --- locate the compiler inside Unity --------------------------------------
# Unity moved Roslyn between versions — 6000.3 ships it as DotNetSdkRoslyn/,
# 6000.5 as a full DotNetSdk/sdk/*/Roslyn/bincore/. Rather than pin a layout,
# walk the installed editors newest-first and take the first one that has all
# three pieces we need.
UNITY_ROOT="${UNITY_ROOT:-/c/Program Files/Unity/Hub/Editor}"

find_csc() {  # $1 = Editor/Data
  local d="$1" c
  for c in "$d/DotNetSdkRoslyn/csc.dll" \
           "$d"/DotNetSdk/sdk/*/Roslyn/bincore/csc.dll; do
    [ -f "$c" ] && { echo "$c"; return 0; }
  done
  return 1
}

# Newest editor first. Read line-by-line, never word-split: these paths live
# under "Program Files" and the space will eat them otherwise.
EDITOR=""; CSC=""; DOTNET=""; FW=""
BUILD_PROBE="$(mktemp)"
{
  [ -n "${UNITY_EDITOR:-}" ] && printf '%s\n' "$UNITY_EDITOR"
  ls -1d "$UNITY_ROOT"/*/Editor/Data 2>/dev/null | sort -Vr
} | while IFS= read -r cand; do
  [ -d "$cand" ] || continue
  c="$(find_csc "$cand")" || continue
  d="$cand/NetCoreRuntime/dotnet.exe"
  fw="$(ls -1d "$cand"/NetCoreRuntime/shared/Microsoft.NETCore.App/* 2>/dev/null | sort -V | tail -1)"
  [ -f "$d" ] && [ -n "$fw" ] && [ -d "$fw" ] || continue
  printf '%s\n%s\n%s\n%s\n' "$cand" "$c" "$d" "$fw"
  break
done > "$BUILD_PROBE"

if [ -s "$BUILD_PROBE" ]; then
  { read -r EDITOR; read -r CSC; read -r DOTNET; read -r FW; } < "$BUILD_PROBE"
fi
rm -f "$BUILD_PROBE"

if [ -z "$EDITOR" ]; then
  echo "parity: no Unity editor with a usable C# compiler found under $UNITY_ROOT" >&2
  echo "        set UNITY_EDITOR=/path/to/Editor/Data and retry" >&2
  exit 2
fi
FW_VER="$(basename "$FW")"

echo "parity: using Unity $(basename "$(dirname "$(dirname "$EDITOR")")"), runtime $FW_VER"

# --- compile ----------------------------------------------------------------
rm -rf "$BUILD"; mkdir -p "$BUILD"

REFS=()
for dll in System.Runtime System.Private.CoreLib System.Console System.Collections System.Linq System.Runtime.Extensions netstandard; do
  [ -f "$FW/$dll.dll" ] && REFS+=("-r:$(cygpath -w "$FW/$dll.dll")")
done

SOURCES=()
while IFS= read -r s; do SOURCES+=("$(cygpath -w "$s")"); done < <(find "$SIM" -name '*.cs')
SOURCES+=("$(cygpath -w "$HERE/DumpCs.cs")")

"$DOTNET" "$(cygpath -w "$CSC")" \
  -nologo -nostdlib -noconfig -optimize+ -langversion:9.0 \
  -target:exe -out:"$(cygpath -w "$BUILD/parity.dll")" \
  "${REFS[@]}" "${SOURCES[@]}" || { echo "parity: compile failed" >&2; exit 1; }

cat > "$BUILD/parity.runtimeconfig.json" <<JSON
{
  "runtimeOptions": {
    "tfm": "net6.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "$FW_VER" }
  }
}
JSON

# --- run both sides ---------------------------------------------------------
node "$HERE/dump_js.mjs" > "$BUILD/js.txt"   || { echo "parity: JS dump failed" >&2; exit 1; }
"$DOTNET" "$(cygpath -w "$BUILD/parity.dll")" "$(cygpath -w "$ROOT")" > "$BUILD/cs.txt" || { echo "parity: C# dump failed" >&2; exit 1; }

# Normalise line endings before comparing; the two runtimes disagree about \r
# and that is not a simulation difference.
sed -i 's/\r$//' "$BUILD/js.txt" "$BUILD/cs.txt"

# --- compare ----------------------------------------------------------------
if diff -u "$BUILD/js.txt" "$BUILD/cs.txt" > "$BUILD/diff.txt"; then
  echo "parity: OK — $(grep -vc '^#' "$BUILD/js.txt") observations identical to 12 decimal places"
  exit 0
fi

echo "parity: MISMATCH" >&2
head -60 "$BUILD/diff.txt" >&2
echo "..." >&2
echo "full diff: $BUILD/diff.txt" >&2
exit 1
