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

# Wait for Keycloak before updating the client (model-init may still be running in parallel)
echo "Waiting for Keycloak..."
until docker exec cortex-keycloak-1 \
  /opt/keycloak/bin/kcadm.sh config credentials \
  --server http://localhost:8080 --realm master \
  --user admin --password admin 2>/dev/null; do
  printf '.'
  sleep 3
done
echo " ready"

# Get the internal UUID of the cortex-app client
CLIENT_UUID=$(docker exec cortex-keycloak-1 \
  /opt/keycloak/bin/kcadm.sh get clients -r cortex --fields id,clientId 2>/dev/null | \
  python3 -c "
import sys, json
clients = json.load(sys.stdin)
print(next(c['id'] for c in clients if c['clientId'] == 'cortex-app'))
")

# Register this session's Codespace URLs as valid redirect targets
docker exec cortex-keycloak-1 \
  /opt/keycloak/bin/kcadm.sh update "clients/${CLIENT_UUID}" -r cortex \
  -s "redirectUris=[\"http://localhost:8080/*\",\"http://localhost:8080/signin-oidc\",\"${APP_URL}/*\",\"${APP_URL}/signin-oidc\"]" \
  -s "webOrigins=[\"http://localhost:8080\",\"${APP_URL}\"]"

echo ""
echo "App:      $APP_URL"
echo "Keycloak: $KC_URL  (admin / admin)"
echo "          Default user: dev / dev"
