#!/usr/bin/env bash
set -euo pipefail

# Usage: vulnerable-gate.sh <report-file> <expected-project-count> [dotnet-exit-code]
# Fails closed: any vulnerable, partial, empty, or unrecognized report exits 1.

if [ "$#" -lt 2 ] || [ "$#" -gt 3 ]; then
  echo "usage: vulnerable-gate.sh <report> <expected-count> [dotnet-exit-code]" >&2
  exit 1
fi

report="$1"
expected="$2"
code="${3:-0}"

if ! [[ "$expected" =~ ^[0-9]+$ ]] || [ "$expected" -le 0 ]; then
  echo "Expected project count must be a positive integer" >&2
  exit 1
fi

if ! [[ "$code" =~ ^[0-9]+$ ]] || [ "$code" -ne 0 ]; then
  echo "dotnet list package failed" >&2
  exit 1
fi

if [ ! -s "$report" ]; then
  echo "Vulnerability report missing or empty" >&2
  exit 1
fi

if grep -q "has the following vulnerable packages" "$report"; then
  echo "Vulnerable packages found" >&2
  exit 1
fi

clean=$(grep -c "has no vulnerable packages" "$report" || true)
if [ "$clean" != "$expected" ]; then
  echo "Vulnerability report incomplete or unrecognized" >&2
  exit 1
fi
