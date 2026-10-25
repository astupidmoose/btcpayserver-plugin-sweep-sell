#!/usr/bin/env bash
set -euo pipefail

# Builds the installable plugin package with BTCPay Server's plugin packer.
# Usage: scripts/pack.sh [output directory, default "dist" in the repository root]
# Writes <output>/BTCPayServer.Plugins.SweepSell/<version>/ with the .btcpay package, its manifest
# (.btcpay.json) and SHA256SUMS.

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUTPUT="$(realpath -m "${1:-$ROOT/dist}")"
cd "$ROOT"
source plugin-env.sh

PUBLISH="$(mktemp -d)"
trap 'rm -rf "$PUBLISH"' EXIT

dotnet publish "src/$PROJECT" -c Release -o "$PUBLISH"
dotnet run --project submodules/btcpayserver/BTCPayServer.PluginPacker -c Release -- "$PUBLISH" "$PROJECT" "$OUTPUT"
