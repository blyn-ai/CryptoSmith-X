#!/bin/sh
# Runs on the VPS as the forced command of the CI deploy key (authorized_keys
# command="..."), so a leaked key can do exactly this and nothing else.
# CI pipes its ephemeral GITHUB_TOKEN on stdin so the pull works while the GHCR
# packages are private; a manual run just times the read out and pulls anonymously.
set -eu
cd /opt/cryptosmithx

TOKEN=$(timeout 3 head -n1 2>/dev/null || true)
if [ -n "$TOKEN" ]; then
    printf '%s\n' "$TOKEN" | docker login ghcr.io -u github-actions --password-stdin >/dev/null
fi

curl -fsSL https://raw.githubusercontent.com/blyn-ai/CryptoSmith-X/main/deploy/docker-compose.prod.yml \
  -o docker-compose.yml
docker compose pull -q
docker compose up -d --remove-orphans
docker logout ghcr.io >/dev/null 2>&1 || true
echo "--- stack:"
docker ps -a --filter name=cryptosmithx --format '{{.Names}}  {{.Status}}'
