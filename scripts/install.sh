#!/usr/bin/env bash
set -euo pipefail

yes_flag=""
for arg in "$@"; do
  case "$arg" in
    -y|--yes) yes_flag="-y" ;;
  esac
done

dotnet_url="https://dotnet.microsoft.com/download/dotnet/10"
if ! command -v dotnet >/dev/null 2>&1 \
  || ! dotnet --list-sdks 2>/dev/null | awk -F. '$1 >= 10 { found = 1 } END { exit !found }'; then
  echo "ContextManager requires the .NET 10 SDK or later. Install it from $dotnet_url and re-run this installer." >&2
  exit 1
fi

if dotnet tool list -g | awk 'NR > 2 { print tolower($1) }' | grep -qx "contextmanager"; then
  dotnet tool update -g ContextManager
else
  dotnet tool install -g ContextManager
fi

tool="$HOME/.dotnet/tools/context-manager"
if [ -n "$yes_flag" ]; then
  exec "$tool" setup -y
else
  exec "$tool" setup < /dev/tty
fi
