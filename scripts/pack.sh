#!/usr/bin/env bash
# Pack com.IvanMurzak.Rql into <out dir> (default ./packages) and check it; prints the version on the last line.
# Used by ci.yml (every PR) and release.yml. Usage: scripts/pack.sh [out-dir]
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
proj="$here/../dotnet/Rql.Package/Rql.Package.csproj"
out="${1:-./packages}"
version=$(dotnet msbuild "$proj" -getProperty:PackageVersion)
dotnet pack "$proj" -c Release -o "$out" >&2
"$here/check-package.sh" "$out/com.IvanMurzak.Rql.$version.nupkg" >&2
echo "$version"
