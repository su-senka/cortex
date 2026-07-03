#!/usr/bin/env bash
# Generates a self-signed TLS certificate for the production nginx proxy.
# Good for LAN/intranet use; for public deployments use real certs (e.g. certbot).
#
# Usage: ./scripts/gen-certs.sh [hostname]   (default: cortex.local)
set -euo pipefail

HOST="${1:-cortex.local}"
CERT_DIR="$(cd "$(dirname "$0")/.." && pwd)/nginx/certs"

mkdir -p "$CERT_DIR"

openssl req -x509 -newkey rsa:4096 -sha256 -days 825 -nodes \
  -keyout "$CERT_DIR/cortex.key" \
  -out "$CERT_DIR/cortex.crt" \
  -subj "/CN=${HOST}" \
  -addext "subjectAltName=DNS:${HOST},DNS:localhost,IP:127.0.0.1"

echo "Wrote $CERT_DIR/cortex.crt and cortex.key (CN=${HOST}, valid 825 days)."
