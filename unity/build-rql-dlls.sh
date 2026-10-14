#!/usr/bin/env bash
# Rebuild the RQL libraries (netstandard2.1, Release) and copy them into the Unity package's Plugins folder.
# Run after changing anything under dotnet/Rql.Contracts, Rql.Parser or Rql.Execution.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/.." && pwd)"
dest="$here/com.ivanmurzak.unity.mcp.rql/Plugins"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
for p in Rql.Contracts Rql.Parser Rql.Execution; do
  dotnet build "$root/dotnet/$p" -c Release -f netstandard2.1 -v q -nologo
  cp "$root/dotnet/$p/bin/Release/netstandard2.1/$p.dll" "$dest/"
done
echo "copied to $dest:"; ls -la "$dest"/*.dll
