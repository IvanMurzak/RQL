#!/usr/bin/env bash
# Check a com.IvanMurzak.Rql nupkg before it is published: the three RQL assemblies for every target framework, the
# license file, the readme, and no test / sample assembly. Usage: scripts/check-package.sh <path.nupkg>
set -euo pipefail
pkg="${1:?nupkg path}"
test -f "$pkg" || { echo "::error::$pkg not found"; exit 1; }
listing=$(unzip -l "$pkg")
fail=0
# Every target framework in the package must carry all three assemblies; netstandard2.1 (Unity) must be there.
tfms=$(grep -oE 'lib/[^/]+/' <<<"$listing" | sort -u | cut -d/ -f2)
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
exit $fail
