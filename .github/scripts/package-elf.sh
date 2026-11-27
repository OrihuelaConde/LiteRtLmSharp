#!/usr/bin/env bash
# Packages one official LiteRT-LM ELF library (Linux or Android) under our library name and inspects it:
# architecture, dependencies, C API export surface and, for Android, the embedded GPU sampler and the
# 16 KB page alignment Google Play requires. Any regression fails the native release before packaging.
#
# Usage: package-elf.sh <liblitert-lm.so> <output dir> <linux|android> <X86-64|AArch64>
# Env:   MIN_EXPORTS (minimum litert_lm_* export count)
set -euo pipefail

src=${1:?source library}
out=${2:?output directory}
kind=${3:?linux or android}
machine=${4:?expected ELF machine (X86-64 or AArch64)}
: "${MIN_EXPORTS:?}"

mkdir -p "$out"
lib="$out/libLiteRtLm.so"
cp "$src" "$lib"

# Capture each readelf output once, then test the text: under pipefail a `grep -q` that exits early
# turns the producing pipeline into a failure even when it matched.
header=$(readelf -h "$lib")
if ! grep -Eq "Machine:.*${machine}" <<< "$header"; then
  echo "::error::$src is not a ${machine} library:"
  grep 'Machine:' <<< "$header"
  exit 1
fi
echo "OK: ${machine} ELF."

deps=$(readelf -d "$lib" | grep NEEDED || true)
echo "--- dependencies ---"
echo "$deps"
if grep -q 'libLiteRt' <<< "$deps"; then
  echo "::error::$src expects a separate libLiteRt companion, not the monolithic layout this workflow packages"
  exit 1
fi
if [ "$kind" = linux ]; then
  # The v0.16.0 zip linked the Vulkan loader as a hard dependency; the v0.18.0 and v0.20.0 libraries
  # load without it, and the docs, the CHANGELOG and model-tests.yml (which no longer installs
  # libvulkan1) rely on that.
  # A build that brings the dependency back fails here instead of on a consumer without the loader.
  if grep -q 'libvulkan\.so' <<< "$deps"; then
    echo "::error::$(basename "$out"): the library DT_NEEDs the Vulkan loader (libvulkan.so.1), which the docs say it does not need. Update the docs and model-tests.yml before packaging it."
    exit 1
  fi
  echo "OK: no hard dependency on the Vulkan loader."
fi

bash "$(dirname "$0")/assert-elf-exports.sh" "$lib" "$MIN_EXPORTS"

if [ "$kind" = android ]; then
  # The OpenCL TopK sampler must be EMBEDDED, because we ship no companion library. In the v0.20.0
  # sources the factory tries the statically linked sampler first and falls back to dlopen of
  # libLiteRtTopKOpenClSampler.so. Without the embedded copy GPU sampling falls back to CPU (or fails,
  # as the self-built v0.15.0 set did, LiteRT-LM#3135). The x86_64 build targets the emulator, where
  # the docs say to use the CPU backend, so a missing sampler there only warns.
  # From C API 1.0.0 the library exports only litert_lm_* symbols, so the sampler's entry points are no
  # longer in the dynamic symbol table. Their names don't help either: the factory holds them for its
  # dynamic-load path, so the v0.20.0 Linux libraries carry them too, without the sampler. The check then
  # looks for three error messages of the sampler's own source (top_k_opencl_sampler.cc), which are in
  # both v0.20.0 Android libraries and in neither Linux one. They show that the sampler code is linked in,
  # not that it works on a device; only a GPU run on a device shows that.
  defined=$(readelf --dyn-syms -W "$lib" | awk '$7 != "UND" {sub(/@.*/, "", $8); print $8}')
  if grep -qx LiteRtTopKOpenClSampler_Create <<< "$defined"; then
    echo "OK: the OpenCL TopK sampler's entry points are exported."
  else
    lib_strings=$(strings -a "$lib")
    missing=""
    for mark in "Failed to enqueue barrier for logits tensor" "No valid sampling event created before downloading." "Failed to clEnqueueReadBuffer - "; do
      grep -qF -- "$mark" <<< "$lib_strings" || missing+=" '$mark'"
    done
    if [ -z "$missing" ]; then
      echo "OK: the OpenCL TopK sampler's code is linked in (its own error messages are in the binary)."
    elif [ "$machine" = AArch64 ]; then
      echo "::error::$src does not embed the OpenCL TopK sampler: no export, and the binary lacks the sampler's messages$missing"
      exit 1
    else
      echo "::warning::$src does not embed the OpenCL TopK sampler: no export, and the binary lacks the sampler's messages$missing"
    fi
  fi
  # Google Play's 16 KB page-size requirement: every PT_LOAD segment aligned to 0x4000.
  bad=$(readelf -lW "$lib" | awk '$1 == "LOAD" && $NF != "0x4000" && $NF != "0x10000"' || true)
  if [ -n "$bad" ]; then
    echo "::error::$src has PT_LOAD segments not aligned to 16 KB:"
    echo "$bad"
    exit 1
  fi
  echo "OK: 16 KB page alignment."
fi
ls -la "$out"
