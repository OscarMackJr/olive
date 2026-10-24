#!/usr/bin/env bash
set -euo pipefail

# Beat 2 demo (EVD-02): runs only the Demo=beat2 tests, then prints the transcript path.
# Requires Docker (Testcontainers Postgres).

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

dotnet test tests/Olive.Tests --filter "Demo=beat2"

transcript="$root/TestResults/beat2-transcript.md"
if [ ! -s "$transcript" ]; then
  echo "beat 2 transcript was not written: $transcript" >&2
  exit 1
fi
echo "Transcript: $transcript"
