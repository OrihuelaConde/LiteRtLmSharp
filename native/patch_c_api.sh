#!/usr/bin/env bash
# Adds a redistributable shared-library target for the LiteRT-LM C API.
#
# Upstream only exposes the C API as a Bazel cc_library (//c:engine) and builds
# the `litert_lm_main` executable — there is no public shared-lib target yet
# (issue #2154). This patch appends a `cc_binary(linkshared=True)` over //c:engine,
# so we can produce libLiteRtLm.so / LiteRtLm.dll exporting the litert_lm_* C ABI.
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
  sed -i \
    -e "s#https://zlib\.net/fossils/zlib-1\.3\.1\.tar\.gz#${GH_ZLIB}#g" \
    -e "s#https://zlib\.net/zlib-1\.3\.1\.tar\.gz#${GH_ZLIB}#g" \
    "$DIR/WORKSPACE"
  echo "  OK: repointed zlib.net -> GitHub release in WORKSPACE (same checksum)."
fi

if grep -q "linkshared" "$BUILD"; then
  echo "  SKIP: c/BUILD already has a shared-lib target."
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

# Added by LiteLMSharp: redistributable shared library exposing the C API.
# Output file is named libLiteRtLm.dylib regardless of platform (Bazel target
# label); the CI renames it to libLiteRtLm.so / LiteRtLm.dll afterwards.
cc_binary(
    name = "libLiteRtLm.dylib",
    linkshared = True,
    win_def_file = "windows_exports.def",
    linkopts = select({
        "@platforms//os:macos": ["-Wl,-exported_symbol,_LiteRt*", "-Wl,-exported_symbol,_litert_lm_*"],
        "@platforms//os:ios": ["-Wl,-exported_symbol,_LiteRt*", "-Wl,-exported_symbol,_litert_lm_*"],
        "@platforms//os:linux": ["-Wl,--dynamic-list=$(location :dynamic_list.lds)"],
        "//conditions:default": [],
    }),
    additional_linker_inputs = select({
        "@platforms//os:linux": [":dynamic_list.lds"],
        "//conditions:default": [],
    }),
    visibility = ["//visibility:public"],
    deps = [":engine"],
)

exports_files(["dynamic_list.lds", "windows_exports.def"])
BUILDEOF

echo "  OK: appended cc_binary(linkshared=True) //c:libLiteRtLm.dylib to c/BUILD."
