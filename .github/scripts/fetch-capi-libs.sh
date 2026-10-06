#!/usr/bin/env bash
# Lays out Google's official LiteRT-LM C API libraries under <dest>/lib/<platform>/, the layout of the
# upstream litert_lm_c_api zip, from either source:
#   zip   litert_lm_c_api-$CAPI_VERSION.zip, a release asset of $UPSTREAM_TAG, verified against the sha256
#         digest GitHub records for it (fetch-upstream-asset.sh).
#   pypi  the litert-lm-api wheels on PyPI. Each wheel is a ctypes wrapper around the same C API library
#         (all 212 litert_lm_* functions of the v0.18.0 headers are exported), and each file is verified
#         against the sha256 digest PyPI publishes for it. Upstream shipped the zip only with v0.16.0 and
#         paused it while it renames functions (LiteRT-LM#3569); the wheels ship with every release.
# The packaging steps read the same paths whichever source filled them.
#
# Usage: fetch-capi-libs.sh <zip|pypi> <dest> <platform>...
#   platforms: linux_x86_64 linux_arm64 android_arm64 android_x86_64 windows_x86_64 macos_arm64
# Env:   zip:  UPSTREAM_REPO, UPSTREAM_TAG, GH_TOKEN, CAPI_ZIP
#        pypi: UPSTREAM_TAG (or PYPI_VERSION, defaults to the tag without its leading "v")
set -euo pipefail

source=${1:?source (zip or pypi)}
dest=${2:?destination directory}
shift 2

# The first interpreter that actually runs (on Windows, "python3" can be a Store alias stub).
py=""
for candidate in python3 python; do
  if "$candidate" -c "import sys" >/dev/null 2>&1; then py=$candidate; break; fi
done
[ -n "$py" ] || { echo "::error::no Python interpreter found"; exit 1; }
helpers=$(dirname "$0")

# Path of a platform's library inside the zip layout.
lib_path() {
  case "$1" in
    windows_x86_64) echo "lib/windows_x86_64/bin/litert-lm.dll" ;;
    macos_arm64)    echo "lib/macos_arm64/liblitert-lm.dylib" ;;
    *)              echo "lib/$1/liblitert-lm.so" ;;
  esac
}

# Wheel file name pattern per platform. The tag versions (manylinux_2_27, android_23, macosx_12_0) follow
# upstream's toolchains, so only the architecture part is pinned.
wheel_pattern() {
  case "$1" in
    linux_x86_64)   echo '-manylinux_[0-9]+_[0-9]+_x86_64[.]whl$' ;;
    linux_arm64)    echo '-manylinux_[0-9]+_[0-9]+_aarch64[.]whl$' ;;
    android_arm64)  echo '-android_[0-9]+_arm64_v8a[.]whl$' ;;
    android_x86_64) echo '-android_[0-9]+_x86_64[.]whl$' ;;
    windows_x86_64) echo '-win_amd64[.]whl$' ;;
    macos_arm64)    echo '-macosx_[0-9]+_[0-9]+_arm64[.]whl$' ;;
    *) echo "::error::unknown platform '$1'" >&2; return 1 ;;
  esac
}

mkdir -p "$dest"
case "$source" in
  zip)
    : "${CAPI_ZIP:?}"
    bash "$helpers/fetch-upstream-asset.sh" "$CAPI_ZIP" upstream
    for plat in "$@"; do
      p=$(lib_path "$plat")
      mkdir -p "$dest/$(dirname "$p")"
      "$py" "$helpers/zip-member.py" extract "upstream/$CAPI_ZIP" "$p" "$dest/$p"
    done
    ;;
  pypi)
    : "${UPSTREAM_TAG:?}"
    version=${PYPI_VERSION:-${UPSTREAM_TAG#v}}
    curl -fsSL --retry 3 -o pypi.json "https://pypi.org/pypi/litert-lm-api/${version}/json"
    mkdir -p wheels
    for plat in "$@"; do
      pattern=$(wheel_pattern "$plat")
      line=$("$py" "$helpers/zip-member.py" pypi-file pypi.json "$pattern")
      if [ -z "$line" ]; then
        echo "::error::litert-lm-api ${version} has no single wheel for ${plat} (pattern ${pattern})"
        exit 1
      fi
      read -r name url sha <<< "$line"
      for attempt in 1 2 3; do
        if curl -fsSL -o "wheels/$name" "$url"; then break; fi
        echo "download attempt $attempt failed; retrying in 20s..."
        [ "$attempt" = "3" ] && exit 1
        sleep 20
      done
      actual=$(sha256sum "wheels/$name" | awk '{print $1}')
      if [ "$actual" != "$sha" ]; then
        echo "::error::sha256 mismatch for ${name}: expected ${sha}, got ${actual}"
        exit 1
      fi
      echo "OK: ${name} sha256=${actual}"
      p=$(lib_path "$plat")
      mkdir -p "$dest/$(dirname "$p")"
      "$py" "$helpers/zip-member.py" extract "wheels/$name" "litert_lm/$(basename "$p")" "$dest/$p"
    done
    ;;
  *)
    echo "::error::unknown source '$source' (expected zip or pypi)"
    exit 1
    ;;
esac
find "$dest/lib" -type f -exec ls -la {} \;
