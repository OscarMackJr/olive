#!/usr/bin/env bash
set -euo pipefail

# Identity review demo (D-18): runs only the Demo=identity tests, then prints the transcript path.
# Requires Docker (Testcontainers Postgres). Invoke as: bash build/demo-identity-review.sh

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

dotnet test tests/Olive.Tests --filter "Demo=identity"

transcript="$root/TestResults/identity-review-transcript.md"
if [ ! -s "$transcript" ]; then
  echo "identity review transcript was not written: $transcript" >&2
  exit 1
fi
echo "Transcript: $transcript"
