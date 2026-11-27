#!/usr/bin/env bash
# Asserts an ELF shared library exposes the LiteRT-LM C API: at least <min> defined litert_lm_*
# dynamic symbols, including the engine, conversation and session entry points the binding imports.
# Usage: assert-elf-exports.sh <lib.so> <min-count>
set -euo pipefail

lib="${1:?library}"
min="${2:?minimum export count}"

# From C API 1.0.0 (LiteRT-LM v0.19.0) the symbols are versioned: readelf prints them as
# name@@LITERT_LM_C_API_1.0, so the version suffix is dropped before comparing names.
defined=$(readelf --dyn-syms -W "$lib" | awk '$7 != "UND" && $8 ~ /^litert_lm_/ {sub(/@.*/, "", $8); print $8}' | sort -u)
count=$(printf '%s\n' "$defined" | grep -c . || true)
echo "exported litert_lm_* symbols: $count"
if [ "$count" -lt "$min" ]; then
  echo "::error::$lib exports $count litert_lm_* symbols, expected at least $min"
  exit 1
fi
required="litert_lm_get_api_version litert_lm_engine_create litert_lm_conversation_create litert_lm_conversation_send_message_stream litert_lm_stream_chunk_get_error"
# Session API. The same names are in .github/scripts/inspect-pe.py, in SESSION_EXPORTS of
# .github/workflows/native-release.yml and in LiteRtLmSharp.Tests/SessionAbiTests.cs (SessionImports);
# change all four together.
required="$required litert_lm_engine_create_session litert_lm_session_delete litert_lm_session_cancel_process litert_lm_session_save_checkpoint litert_lm_session_rewind_to_checkpoint litert_lm_session_rewind_to_step litert_lm_session_run_prefill litert_lm_session_run_decode"
required="$required litert_lm_session_run_decode_async litert_lm_session_run_text_scoring litert_lm_session_config_set_apply_prompt_template litert_lm_responses_delete litert_lm_responses_get_num_candidates litert_lm_responses_get_response_text_at litert_lm_responses_get_score_at litert_lm_responses_get_token_length_at"
# Model sync entry points. The same names are in .github/scripts/inspect-pe.py, in the Apple steps of
# .github/workflows/native-release.yml and in LiteRtLmSharp.Tests/ModelSyncAbiTests.cs (ModelSyncImports).
required="$required litert_lm_model_sync_update litert_lm_model_sync_plan_create_from_file"
for sym in $required; do
  printf '%s\n' "$defined" | grep -qx "$sym" || { echo "::error::$lib lacks $sym"; exit 1; }
done
echo "OK: C API export surface present."
