#!/usr/bin/env bash
# Build the locally-patched FeedCord image.
#
# The Dockerfile lives in the FeedCord/ subdirectory, so that -- not the repo
# root -- is the build context. See PATCHES.md.
#
# Usage:
#   ./rebuild.sh                      build the image only
#   ./rebuild.sh /path/to/composedir  build, then restart the compose service
set -euo pipefail

IMAGE="${FEEDCORD_IMAGE:-feedcord:local-gzip}"
SERVICE="${FEEDCORD_SERVICE:-feedcord}"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

docker build -t "$IMAGE" "$REPO/FeedCord"

if [ "$#" -ge 1 ]; then
    # cd first so compose derives the project name and reads .env from there.
    cd "$1"
    docker compose up -d "$SERVICE"
fi
