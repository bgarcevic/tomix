#!/bin/sh
# Developer task runner for the common inner loop. Exists because recipes like
# `TOMIX_UPDATE_SNAPSHOTS=1 dotnet test ...` are not valid PowerShell, so the
# documented commands must work for both halves of the audience. Works from
# any directory; anchors on the script location like ./tx.
# Usage: dev.sh <build|test|format|snapshot|docs> [extra args passed through]
set -eu

case "${1:-}" in
  build|test|format|snapshot|docs) ;;
  *)
    echo "usage: $0 <build|test|format|snapshot|docs> [args]" >&2
    echo "  build     dotnet build" >&2
    echo "  test      dotnet test" >&2
    echo "  format    dotnet format (applies fixes; CI verifies with --verify-no-changes)" >&2
    echo "  snapshot  regenerate CommandSurface.approved.txt" >&2
    echo "  docs      strict docs-site build (what CI runs; requires uv)" >&2
    exit 2
    ;;
esac

cd "$(dirname "$0")/.."

task="$1"
shift

case "$task" in
  build)   dotnet build "$@" ;;
  test)    dotnet test "$@" ;;
  format)  dotnet format "$@" ;;
  # Keep the env var, the filter, and the failure message in
  # CommandSurfaceSnapshotTests.cs pointing at this recipe.
  snapshot) TOMIX_UPDATE_SNAPSHOTS=1 dotnet test --filter CommandSurfaceSnapshotTests "$@" ;;
  docs)    uv run zensical build --clean --strict "$@" ;;
esac
