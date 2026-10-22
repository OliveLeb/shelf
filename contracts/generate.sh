#!/usr/bin/env bash
# Regenerates the API contract: contracts/openapi.json (written by the Shelf.Api build)
# and the Bruno collection in contracts/bruno/ (rebuilt from scratch from the OpenAPI file).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

dotnet build "$root/apps/backend/Shelf.Api" --nologo -v quiet
rm -rf "$root/contracts/bruno"
npx --yes @usebruno/cli@4.2.1 import openapi \
  --source "$root/contracts/openapi.json" \
  --output "$root/contracts/bruno" \
  --collection-name Shelf
