#!/usr/bin/env bash
set -euo pipefail

docker compose -f docker-compose.yml -f docker-compose.local.yml up -d --build "$@"

echo ""
echo "App:      http://localhost:8080"
echo "Keycloak: http://localhost:8180  (admin / admin)"
echo "          Default user: dev / dev"
