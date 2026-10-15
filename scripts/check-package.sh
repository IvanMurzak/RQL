#!/usr/bin/env bash
# Check a com.IvanMurzak.Rql nupkg before it is published: the three RQL assemblies for every target framework, the
# license file, the readme, the netstandard2.1 System.Text.Json dependency, and no test / sample assembly. Usage: scripts/check-package.sh <path.nupkg>
set -euo pipefail
pkg="${1:?nupkg path}"
test -f "$pkg" || { echo "::error::$pkg not found"; exit 1; }
listing=$(unzip -l "$pkg")
fail=0
# Every target framework in the package must carry all three assemblies; netstandard2.1 (Unity) must be there.
tfms=$(grep -oE 'lib/[^/]+/' <<<"$listing" | sort -u | cut -d/ -f2 || true)   # no lib/ at all: reported below
grep -qx netstandard2.1 <<<"$tfms" || { echo "::error::no lib/netstandard2.1/"; fail=1; }
for tfm in $tfms; do
  for dll in Rql.Contracts Rql.Parser Rql.Execution; do
    grep -q "lib/$tfm/$dll.dll" <<<"$listing" || { echo "::error::missing lib/$tfm/$dll.dll"; fail=1; }
  done
done
for f in LICENSE.md PACKAGE.md; do grep -qE " $f$" <<<"$listing" || { echo "::error::missing $f"; fail=1; }; done
if grep -qE "Rql\.(Tests|Bench|Conformance|SampleRuntime)\.dll" <<<"$listing"; then echo "::error::test / sample assembly in the package"; fail=1; fi
nuspec=$(unzip -p "$pkg" '*.nuspec')
grep -q '<license type="file">LICENSE.md</license>' <<<"$nuspec" || { echo "::error::nuspec license is not LICENSE.md"; fail=1; }
# The libraries' package dependencies do not flow through Rql.Package's PrivateAssets="all" reference; the netstandard2.1
# System.Text.Json dependency comes from Directory.Build.targets applying to Rql.Package itself. Unity has no STJ built in.
ns21=$(sed -n '/targetFramework=".NETStandard2.1"/,/<\/group>/p' <<<"$nuspec")
grep -q '<dependency id="System.Text.Json"' <<<"$ns21" || { echo "::error::nuspec .NETStandard2.1 group lacks the System.Text.Json dependency"; fail=1; }
exit $fail
