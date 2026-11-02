#!/bin/bash
# Names the native frames of a crash report: function, source file and line for each "#NN pc ADDR  .../libX.so" line of
# exit.txt (the decoded tombstone, Diagnostics.java), from symbols found by the libraries' build ids, which the report
# lists under "libraries in the backtraces (build id)".
#   host/symbolize.sh REPORT.zip|exit.txt [--fetch]
# Symbols are looked for in build/symbols/BUILDID/ (a cache, kept), then among the APK's own libraries (build/apk/lib:
# libporthost, libvulkan, libglfw, built with their symbol tables). --fetch downloads the missing ones of .NET's libraries
# (libcoreclr, libclrjit, libSystem.*) from Microsoft's symbol server into the cache (about 130 MB for libcoreclr).
# Reports from before 2026-10-27 have no build ids: a library is then matched to the one cached file of its name.
set -e
cd "$(dirname "$0")/.."
# The NDK's LLVM tools (host/env.sh).
. host/env.sh
NDK="$ANDROID_NDK/toolchains/llvm/prebuilt/windows-x86_64/bin"
INPUT="$1"
FETCH=0
[ "$2" = "--fetch" ] && FETCH=1
[ -n "$INPUT" ] || { echo "usage: host/symbolize.sh REPORT.zip|exit.txt [--fetch]"; exit 1; }
TEXT=$(mktemp)
trap 'rm -f "$TEXT"' EXIT
case "$INPUT" in
  *.zip) unzip -p "$INPUT" exit.txt > "$TEXT" ;;
  *) cp "$INPUT" "$TEXT" ;;
esac
[ -s "$TEXT" ] || { echo "no crash text (exit.txt) in $INPUT"; exit 1; }

declare -A SYM
# The build ids the report names.
while read -r lib id; do
  [ -n "$id" ] || continue
  dir="build/symbols/$id"
  found=$(ls "$dir"/* 2>/dev/null | head -1 || true)
  if [ -z "$found" ]; then
    for so in build/apk/lib/*/"$lib"; do
      [ -f "$so" ] || continue
      if "$NDK/llvm-readelf.exe" -n "$so" 2>/dev/null | grep -qi "Build ID: $id"; then
        found="$so"
      fi
    done
  fi
  if [ -z "$found" ] && [ $FETCH = 1 ] && [[ "$lib" =~ ^lib(coreclr|clrjit|System\.|mscordaccore|mscordbi) ]]; then
    mkdir -p "$dir"
    echo "fetching symbols of $lib ($id)..." >&2
    if curl -sfL --retry 3 -o "$dir/$lib.dbg" "https://msdl.microsoft.com/download/symbols/_.debug/elf-buildid-sym-$id/_.debug"; then
      found="$dir/$lib.dbg"
    else
      rm -f "$dir/$lib.dbg"; rmdir "$dir" 2>/dev/null || true
      echo "  not on the server" >&2
    fi
  fi
  if [ -n "$found" ]; then
    SYM[$lib]="$found"
    echo "$lib  $id  -> $found" >&2
  else
    echo "$lib  $id  (no symbols)" >&2
  fi
done < <(sed -n '/^libraries in the backtraces (build id):/,/^$/p' "$TEXT" | grep '^  ' | awk '{print $1, $2}')

# An older report: by name alone.
if [ ${#SYM[@]} -eq 0 ]; then
  for f in build/symbols/*/*; do
    [ -f "$f" ] || continue
    lib=$(basename "$f" .dbg)
    if [ -z "${SYM[$lib]}" ]; then
      SYM[$lib]="$f"
      echo "$lib  (no build id in the report; the cached $f)" >&2
    fi
  done
fi

while IFS= read -r line; do
  printf '%s\n' "$line"
  if [[ "$line" =~ ^\ +\#[0-9]+\ pc\ ([0-9a-f]+)\ +([^ ]+\.so) ]]; then
    pc="${BASH_REMATCH[1]}"; lib=$(basename "${BASH_REMATCH[2]}")
    sym="${SYM[$lib]}"
    [ -n "$sym" ] || continue
    "$NDK/llvm-addr2line.exe" -f -C -i -e "$sym" "0x$pc" 2>/dev/null | paste - - | while IFS=$'\t' read -r fn where; do
      where="${where//\\//}"
      printf '        -> %s  %s\n' "$fn" "${where##*/src/}"
    done
  fi
done < "$TEXT"
