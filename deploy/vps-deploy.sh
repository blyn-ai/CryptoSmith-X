#!/bin/sh
# Runs on the VPS as the forced command of the CI deploy key (authorized_keys
# command="..."), so a leaked key can do exactly this and nothing else.
# Refreshes the compose file from main, pulls images, converges the stack.
set -eu
cd /opt/cryptosmithx
curl -fsSL https://raw.githubusercontent.com/blyn-ai/CryptoSmith-X/main/deploy/docker-compose.prod.yml \
  -o docker-compose.yml
docker compose pull -q
docker compose up -d --remove-orphans
echo "--- stack:"
docker ps -a --filter name=cryptosmithx --format '{{.Names}}  {{.Status}}'
