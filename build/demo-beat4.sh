#!/usr/bin/env bash
set -euo pipefail

# Beat 4 demo (EVD-03): runs only the Demo=beat4 tests, then prints the transcript path.
# Requires Docker (Testcontainers Postgres).

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

dotnet test tests/Olive.Tests --filter "Demo=beat4"

transcript="$root/TestResults/beat4-transcript.md"
if [ ! -s "$transcript" ]; then
  echo "beat 4 transcript was not written: $transcript" >&2
  exit 1
fi
echo "Transcript: $transcript"
