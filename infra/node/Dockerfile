# Node.js toolchain for the web and mobile workspaces (development only).
# Keep the version in sync with .node-version.
FROM node:24.22.1-bookworm-slim

# Corepack provides the pnpm version pinned by the root package.json.
ENV COREPACK_HOME=/home/node/.cache/corepack \
    COREPACK_ENABLE_DOWNLOAD_PROMPT=0
RUN corepack enable \
  && mkdir -p /home/node/.cache \
  && chown -R node:node /home/node

USER node
WORKDIR /workspace
