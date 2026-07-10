#!/usr/bin/env bash
# Adds a redistributable shared-library target for the LiteRT-LM C API.
#
# Upstream exposes the C API as a Bazel cc_library (//c:engine); since v0.14.0 it
# also ships its own shared-lib target (cc_binary `litert-lm`, the Python-wheel
# build), but that one fits the wheel, not us: no Windows .def (no exports on MSVC),
# -fvisibility=hidden without the LiteRt* wildcard exports the prebuilt WebGPU
# accelerator resolves via dlsym, and no @loader_path/$ORIGIN rpath setup for our
# side-by-side companion layout. So we keep appending our own
# `cc_binary(linkshared=True)` over //c:engine to produce libLiteRtLm.so /
# LiteRtLm.dll exporting the litert_lm_* C ABI.
#
# Approach mirrors flutter_gemma's downstream patch (MIT) but is trimmed to just
# the shared-lib target and generates the Windows .def from c/engine.h so the
# export list stays in sync with whatever ref we pin.
#
# Build with: --define=litert_link_capi_so=true   (keeps libLiteRt a SEPARATE
# shared lib so the prebuilt WebGPU accelerator can resolve LiteRt* at runtime
# instead of statically linking a second copy of TFLite into libLiteRtLm).
# Windows additionally needs: --define=resolve_symbols_in_exec=false
#
# Usage: patch_c_api.sh <litert_lm_dir>
set -euo pipefail

DIR="${1:?Usage: patch_c_api.sh <litert_lm_dir>}"
BUILD="$DIR/c/BUILD"
HDR="$DIR/c/engine.h"

echo "Patching LiteRT-LM C API in $DIR ..."

# --- Fix flaky upstream dependency download ---
# The `minizip` repo in WORKSPACE pulls zlib 1.3.1 from zlib.net, which
# periodically re-packages its tarballs (changing the sha256) and breaks the
# pinned checksum -> Bazel aborts ("Checksum was ... but wanted 9a93b2b7..."").
# Repoint to the canonical zlib 1.3.1 GitHub release, which serves the exact
# pinned checksum and the same internal layout (zlib-1.3.1/contrib/minizip).
if [ -f "$DIR/WORKSPACE" ] && grep -q "zlib.net" "$DIR/WORKSPACE"; then
  GH_ZLIB='https://github.com/madler/zlib/releases/download/v1.3.1/zlib-1.3.1.tar.gz'
  # -i.bak: portable across GNU sed (Linux) and BSD sed (macOS).
  sed -i.bak \
    -e "s#https://zlib\.net/fossils/zlib-1\.3\.1\.tar\.gz#${GH_ZLIB}#g" \
    -e "s#https://zlib\.net/zlib-1\.3\.1\.tar\.gz#${GH_ZLIB}#g" \
    "$DIR/WORKSPACE"
  rm -f "$DIR/WORKSPACE.bak"
  echo "  OK: repointed zlib.net -> GitHub release in WORKSPACE (same checksum)."
fi

# Idempotence guard: look for OUR target name, not just "linkshared" — v0.14.0's
# c/BUILD ships upstream's own cc_binary(linkshared = 1) target, which must not
# make us skip appending ours.
if grep -q "libLiteRtLm.dylib" "$BUILD"; then
  echo "  SKIP: c/BUILD already has our shared-lib target."
  exit 0
fi

# --- Linux: dynamic export list (wildcards keep internal symbols visible too,
#     which the WebGPU accelerator plugin needs via dlsym(RTLD_DEFAULT)). ---
cat > "$DIR/c/dynamic_list.lds" << 'LDSEOF'
{
  LiteRt*;
  litert_lm_*;
};
LDSEOF

# --- Windows: .def whitelist. No wildcards in .def, so enumerate every
#     litert_lm_* function declared right after LITERT_LM_C_API_EXPORT. ---
{
  echo 'LIBRARY "LiteRtLm"'
  echo 'EXPORTS'
  awk '
    /LITERT_LM_C_API_EXPORT/ { look = 3; next }
    look > 0 {
      look--
      if (match($0, /litert_lm_[A-Za-z0-9_]+[[:space:]]*\(/)) {
        name = substr($0, RSTART, RLENGTH)
        gsub(/[[:space:](]/, "", name)
        print "  " name
        look = 0
      }
    }
  ' "$HDR" | sort -u
} > "$DIR/c/windows_exports.def"

defcount=$(($(wc -l < "$DIR/c/windows_exports.def") - 2))
echo "  Generated windows_exports.def with $defcount exported functions."

cat >> "$BUILD" << 'BUILDEOF'

# Added by LiteRtLmSharp: redistributable shared library exposing the C API.
# Output file is named libLiteRtLm.dylib regardless of platform (Bazel target
# label); the CI renames it to libLiteRtLm.so / LiteRtLm.dll afterwards.
cc_binary(
    name = "libLiteRtLm.dylib",
    linkshared = True,
    win_def_file = "windows_exports.def",
    linkopts = select({
        # @loader_path rpath: companion dylibs (libLiteRt etc.) resolve from the same
        # directory as libLiteRtLm.dylib without install_name_tool patching.
        "@platforms//os:macos": ["-Wl,-exported_symbol,_LiteRt*", "-Wl,-exported_symbol,_litert_lm_*", "-Wl,-rpath,@loader_path"],
        "@platforms//os:ios": ["-Wl,-exported_symbol,_LiteRt*", "-Wl,-exported_symbol,_litert_lm_*"],
        # Linux: besides the dynamic-list exports, isolate our statically-linked internals
        # (abseil/llguidance/...) from the prebuilt libGemmaModelConstraintProvider.so the
        # engine links against (DT_NEEDED, upstream constrained_decoding/BUILD) — that
        # prebuilt embeds its own copies of the same libraries, and cross-library symbol
        # binding between the two corrupted memory: SIGSEGV in decode whenever tools +
        # constrained decoding were enabled (repro 2026-06-12; matches LiteRT-LM#2149).
        # Google's official wheel binary (which passes the same test) links with
        # -Bsymbolic/--gc-sections; --exclude-libs,ALL additionally drops archive symbols
        # from our dynamic table so the provider cannot bind into them. rpath $ORIGIN lets
        # DT_NEEDED companions resolve from our own directory without preloading.
        "@platforms//os:linux": [
            "-Wl,--dynamic-list=$(location :dynamic_list.lds)",
            "-Wl,--exclude-libs,ALL",
            "-Wl,-Bsymbolic",
            "-Wl,--gc-sections",
            "-Wl,-rpath,$$ORIGIN",
        ],
        # Android keeps the previous minimal linkopts on purpose: its sampler/accelerator
        # setup is different (LiteRt C API static-linked + patchelf'd samplers, see
        # build-native.yml) and is validated as-is. If #1859-style tool crashes surface
        # there, mirror the Linux isolation flags as a follow-up.
        "@platforms//os:android": ["-Wl,--dynamic-list=$(location :dynamic_list.lds)"],
        "//conditions:default": [],
    }),
    additional_linker_inputs = select({
        "@platforms//os:linux": [":dynamic_list.lds"],
        "@platforms//os:android": [":dynamic_list.lds"],
        "//conditions:default": [],
    }),
    visibility = ["//visibility:public"],
    deps = [":engine"],
)

exports_files(["dynamic_list.lds", "windows_exports.def"])
BUILDEOF

echo "  OK: appended cc_binary(linkshared=True) //c:libLiteRtLm.dylib to c/BUILD."
