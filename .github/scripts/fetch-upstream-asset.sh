#!/usr/bin/env bash
# Downloads one asset of the upstream LiteRT-LM release $UPSTREAM_TAG into <dir> and verifies it
# against the sha256 digest GitHub records for that asset. Fails when the digest is missing (an
# unverifiable asset is not packaged) or does not match.
#
# Usage: fetch-upstream-asset.sh <asset-name> <dir>
# Env:   UPSTREAM_REPO (owner/name), UPSTREAM_TAG (release tag), GH_TOKEN (for gh).
set -euo pipefail

asset="${1:?asset name}"
dir="${2:?destination dir}"
: "${UPSTREAM_REPO:?}" "${UPSTREAM_TAG:?}"

mkdir -p "$dir"
expected=$(gh api "repos/${UPSTREAM_REPO}/releases/tags/${UPSTREAM_TAG}" \
  --jq ".assets[] | select(.name == \"${asset}\") | .digest // empty")
if [ -z "$expected" ]; then
  echo "::error::upstream release ${UPSTREAM_TAG} has no asset '${asset}' or GitHub recorded no digest for it"
  exit 1
fi
expected="${expected#sha256:}"

for attempt in 1 2 3; do
  if gh release download "$UPSTREAM_TAG" --repo "$UPSTREAM_REPO" --pattern "$asset" --dir "$dir" --clobber; then
    break
  fi
  echo "download attempt $attempt failed; retrying in 20s..."
  [ "$attempt" = "3" ] && exit 1
  sleep 20
done

actual=$(sha256sum "$dir/$asset" | awk '{print $1}')
if [ "$actual" != "$expected" ]; then
  echo "::error::sha256 mismatch for ${asset}: expected ${expected}, got ${actual}"
  exit 1
fi
echo "OK: ${asset} ($(stat -c %s "$dir/$asset" 2>/dev/null || stat -f %z "$dir/$asset") bytes) sha256=${actual}"
