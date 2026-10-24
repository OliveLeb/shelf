#!/usr/bin/env bash
# Compare the PR merge result with its base, or the entire main push with its previous tip.
set -euo pipefail

backend=true
frontend=true
base="${BASE_SHA:-}"
if [[ "${EVENT_NAME:-}" != workflow_dispatch && "$base" =~ ^[0-9a-f]{40}$ && ! "$base" =~ ^0+$ ]]; then
  # A PR checkout includes its base parent. Multi-commit pushes may need one extra commit.
  # With checkout credentials disabled, public read-only fetching is sufficient for Shelf.
  if git cat-file -e "$base^{commit}" 2>/dev/null || git fetch --no-tags --depth=1 origin "$base"; then
    changes="$(mktemp)"
    trap 'rm -f "$changes"' EXIT
    git diff --name-only --no-renames -z "$base" HEAD > "$changes"
    backend=false
    frontend=false
    while IFS= read -r -d '' path; do
      case "$path" in
        apps/backend/*|contracts/*|.github/*|global.json|compose*.yml|compose*.yaml|.env.example|.editorconfig|*.props|*.targets|*.sln|*.slnx|*[Nn]u[Gg]et.[Cc]onfig|.config/dotnet-tools.json)
          backend=true
          ;;
      esac
      case "$path" in
        apps/web/*|apps/mobile/*|packages/*|.github/*|package.json|pnpm-lock.yaml|pnpm-workspace.yaml|vite.config.ts|.node-version)
          frontend=true
          ;;
      esac
    done < "$changes"
  fi
fi
# Unknown/new history and manual runs intentionally perform all checks.
{
  echo "backend=$backend"
  echo "frontend=$frontend"
} >> "${GITHUB_OUTPUT:?GITHUB_OUTPUT must be set}"
