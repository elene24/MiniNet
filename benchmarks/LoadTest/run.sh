#!/usr/bin/env bash
# Builds both apps in Release, runs each under the same k6 load, and prints the k6 summaries.
# Requires k6 (https://grafana.com/docs/k6/latest/set-up/install-k6/).
set -euo pipefail
cd "$(dirname "$0")"

dotnet publish MiniNetHello -c Release -o out/mininet --nologo -v q
dotnet publish KestrelHello -c Release -o out/kestrel --nologo -v q

run() {
  local name=$1 dll=$2 port=$3
  PORT=$port dotnet "$dll" > /dev/null 2>&1 &
  local pid=$!
  sleep 2
  echo "================ $name ================"
  k6 run -q -e BASE_URL="http://localhost:$port" loadtest.js
  kill "$pid"
  wait "$pid" 2>/dev/null || true
}

run "MiniNet" out/mininet/MiniNetHello.dll 8080
run "ASP.NET Core (Kestrel)" out/kestrel/KestrelHello.dll 8081
