#!/usr/bin/env bash
# Regenerates every snapshot from the current template. Review the result with git diff before committing.
set -euo pipefail
cd "$(dirname "$0")/.."
snapshots=tests/Erkmangelis.Templates.Tests/Snapshots
rm -rf "$snapshots"
# With no snapshots every test fails and writes the full output as *.received.
dotnet test tests/Erkmangelis.Templates.Tests >/dev/null 2>&1 || true
shopt -s nullglob
for received in "$snapshots"/*.received; do
  rm -rf "${received%.received}.verified"
  mv "$received" "${received%.received}.verified"
done
dotnet test tests/Erkmangelis.Templates.Tests
