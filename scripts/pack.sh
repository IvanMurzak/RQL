#!/usr/bin/env bash
# Pack com.IvanMurzak.Rql into <out dir> (default ./packages) and check it; prints the version on the last line.
# Used by ci.yml (every PR; on a chain run at the lock's version) and release.yml.
# Usage: scripts/pack.sh [out-dir [msbuild args, e.g. -p:Version=<v>]]
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
if [ -z "${DOTNET_ROOT:-}" ] && [ -x "$HOME/.dotnet/dotnet" ]; then export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"; fi
proj="$here/../dotnet/Rql.Package/Rql.Package.csproj"
out="${1:-./packages}"
shift || true
version=$(dotnet msbuild "$proj" -getProperty:PackageVersion "$@")
dotnet pack "$proj" -c Release -o "$out" "$@" >&2
"$here/check-package.sh" "$out/com.IvanMurzak.Rql.$version.nupkg" >&2
echo "$version"
