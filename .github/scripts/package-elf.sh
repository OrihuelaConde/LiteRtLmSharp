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
  # The v0.16.0 zip linked the Vulkan loader as a hard dependency; the v0.18.0 libraries load without it,
  # and the docs, the CHANGELOG and model-tests.yml (which no longer installs libvulkan1) rely on that.
  # A build that brings the dependency back fails here instead of on a consumer without the loader.
  if grep -q 'libvulkan\.so' <<< "$deps"; then
    echo "::error::$(basename "$out"): the library DT_NEEDs the Vulkan loader (libvulkan.so.1), which the docs say it does not need. Update the docs and model-tests.yml before packaging it."
    exit 1
  fi
  echo "OK: no hard dependency on the Vulkan loader."
fi

bash "$(dirname "$0")/assert-elf-exports.sh" "$lib" "$MIN_EXPORTS"

if [ "$kind" = android ]; then
  # The OpenCL TopK sampler must be EMBEDDED: the factory dlopens libLiteRtTopKOpenClSampler.so first
  # and falls back to the statically linked copy, and we ship no companion. Without the embedded copy
  # GPU sampling falls back to CPU (or fails, as the self-built v0.15.0 set did, LiteRT-LM#3135). The
  # x86_64 build targets the emulator, which has no OpenCL, so a missing sampler there only warns.
  defined=$(readelf --dyn-syms -W "$lib" | awk '$7 != "UND" {print $8}')
  for sym in LiteRtTopKOpenClSampler_Create LiteRtTopKOpenClSampler_CanHandleInput LiteRtTopKOpenClSampler_SetInferenceFuncAndInputTensors; do
    if ! grep -qx "$sym" <<< "$defined"; then
      if [ "$machine" = AArch64 ]; then
        echo "::error::$src does not embed the OpenCL TopK sampler ($sym missing)"
        exit 1
      fi
      echo "::warning::$src does not embed the OpenCL TopK sampler ($sym missing)"
    fi
  done
  echo "OpenCL TopK sampler check done."
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
