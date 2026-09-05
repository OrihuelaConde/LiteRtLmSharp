#!/usr/bin/env bash
# Asserts an ELF shared library exposes the LiteRT-LM C API: at least <min> defined litert_lm_*
# dynamic symbols, including the engine entry points the binding cannot live without.
# Usage: assert-elf-exports.sh <lib.so> <min-count>
set -euo pipefail

lib="${1:?library}"
min="${2:?minimum export count}"

defined=$(readelf --dyn-syms -W "$lib" | awk '$7 != "UND" && $8 ~ /^litert_lm_/ {print $8}' | sort -u)
count=$(printf '%s\n' "$defined" | grep -c . || true)
echo "exported litert_lm_* symbols: $count"
if [ "$count" -lt "$min" ]; then
  echo "::error::$lib exports $count litert_lm_* symbols, expected at least $min"
  exit 1
fi
for sym in litert_lm_engine_create litert_lm_conversation_create litert_lm_conversation_send_message_stream litert_lm_stream_chunk_get_error; do
  printf '%s\n' "$defined" | grep -qx "$sym" || { echo "::error::$lib lacks $sym"; exit 1; }
done
echo "OK: C API export surface present."
