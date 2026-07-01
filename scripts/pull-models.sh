#!/usr/bin/env bash
# Run this ONCE on a machine with internet access before going offline.
# It pulls both Ollama models into the named volume so the app works air-gapped.
#
# Usage:
#   chmod +x scripts/pull-models.sh
#   ./scripts/pull-models.sh

set -euo pipefail

echo "Starting Ollama service..."
docker compose up -d ollama

echo "Waiting for Ollama to be ready..."
for i in $(seq 1 30); do
  docker compose exec ollama ollama list > /dev/null 2>&1 && break
  [ "$i" -eq 30 ] && { echo "Timed out waiting for Ollama."; exit 1; }
  sleep 3
done

echo "Pulling nomic-embed-text..."
docker compose exec ollama ollama pull nomic-embed-text

echo "Pulling qwen2.5:7b-instruct-q4_K_M..."
docker compose exec ollama ollama pull qwen2.5:7b-instruct-q4_K_M

echo ""
echo "Done. Both models are stored in the 'ollama-models' Docker volume."
echo "To deploy offline: export the volume and import it on the target server."
echo ""
echo "  Export: docker run --rm -v cortex_ollama-models:/data -v \$(pwd):/backup alpine tar czf /backup/ollama-models.tar.gz -C /data ."
echo "  Import: docker run --rm -v cortex_ollama-models:/data -v \$(pwd):/backup alpine tar xzf /backup/ollama-models.tar.gz -C /data"
