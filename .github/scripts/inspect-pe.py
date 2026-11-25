"""Inspects the win-x64 LiteRtLm.dll before packaging.

Asserts: x64 machine, at least <min> litert_lm_* exports, no import of a separate LiteRt companion
(the official library is monolithic) and no dependency on the VC++ redistributable (the official
build links the CRT statically, which is what the docs promise consumers).

Usage: inspect-pe.py <LiteRtLm.dll> <min-export-count>
"""
import sys

import pefile

path, min_exports = sys.argv[1], int(sys.argv[2])
pe = pefile.PE(path, fast_load=True)
pe.parse_data_directories(directories=[
    pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"],
    pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"],
])

machine = pefile.MACHINE_TYPE.get(pe.FILE_HEADER.Machine, hex(pe.FILE_HEADER.Machine))
print(f"machine: {machine}")
if machine != "IMAGE_FILE_MACHINE_AMD64":
    sys.exit(f"::error::{path} is not an x64 image ({machine})")

exports = sorted(
    e.name.decode() for e in getattr(pe, "DIRECTORY_ENTRY_EXPORT", None).symbols if e.name
) if hasattr(pe, "DIRECTORY_ENTRY_EXPORT") else []
capi = [n for n in exports if n.startswith("litert_lm_")]
print(f"exports: {len(exports)} total, {len(capi)} litert_lm_*")
if len(capi) < min_exports:
    sys.exit(f"::error::{path} exports {len(capi)} litert_lm_* functions, expected at least {min_exports}")
REQUIRED = (
    "litert_lm_engine_create", "litert_lm_conversation_create",
    "litert_lm_conversation_send_message_stream", "litert_lm_stream_chunk_get_error",
    # Session API. The same names are in .github/scripts/assert-elf-exports.sh and in
    # LiteRtLmSharp.Tests/SessionAbiTests.cs (SessionImports); change all three together.
    "litert_lm_engine_create_session", "litert_lm_session_delete",
    "litert_lm_session_cancel_process", "litert_lm_session_save_checkpoint",
    "litert_lm_session_rewind_to_checkpoint", "litert_lm_session_rewind_to_step",
    "litert_lm_session_run_prefill", "litert_lm_session_run_decode",
    "litert_lm_session_run_decode_async", "litert_lm_session_run_text_scoring",
    "litert_lm_session_config_set_apply_prompt_template", "litert_lm_responses_delete",
    "litert_lm_responses_get_num_candidates", "litert_lm_responses_get_response_text_at",
    "litert_lm_responses_get_score_at", "litert_lm_responses_get_token_length_at",
)
for required in REQUIRED:
    if required not in capi:
        sys.exit(f"::error::{path} lacks {required}")

imports = sorted(e.dll.decode().lower() for e in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []))
print("imports: " + ", ".join(imports))
companions = [d for d in imports if d.startswith("liblitert") or d.startswith("litert")]
if companions:
    sys.exit(f"::error::{path} imports separate LiteRt companions {companions}; expected the monolithic official build")
crt = [d for d in imports if d.startswith(("vcruntime", "msvcp"))]
if crt:
    sys.exit(f"::error::{path} depends on the VC++ redistributable ({crt}); the official build was expected to link the CRT statically")
print("OK: x64, C API export surface present, monolithic, static CRT.")
