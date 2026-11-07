#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
command -v dotnet >/dev/null || { echo 'Install .NET 10 SDK first'; exit 1; }
command -v ffmpeg >/dev/null || { echo 'Install FFmpeg first'; exit 1; }
cleanup() { kill "${api_pid:-}" "${web_pid:-}" 2>/dev/null || true; }
trap cleanup EXIT INT TERM
dotnet run --project src/Studio.Api --urls http://127.0.0.1:5080 &
api_pid=$!
(cd src/Studio.Web && npm start) &
web_pid=$!
wait -n "$api_pid" "$web_pid"
