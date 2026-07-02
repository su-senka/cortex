#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${CODESPACE_NAME:-}" ]]; then
  echo "Error: CODESPACE_NAME is not set. Run this script inside a GitHub Codespace." >&2
  exit 1
fi

APP_URL="https://${CODESPACE_NAME}-8080.${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN}"
KC_URL="https://${CODESPACE_NAME}-8180.${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN}"

# Write .env so docker-compose.codespaces.yml can substitute ${KEYCLOAK_PUBLIC_URL}
printf 'KEYCLOAK_PUBLIC_URL=%s\n' "$KC_URL" > .env

docker compose -f docker-compose.yml -f docker-compose.codespaces.yml up -d --build "$@"

echo ""
echo "App:      $APP_URL"
echo "Keycloak: $KC_URL  (admin / admin)"
echo "          Default user: dev / dev"
