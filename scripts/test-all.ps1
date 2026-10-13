# Windows/PowerShell equivalent of scripts/test-all.sh
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet test "$root/dotnet/Rql.slnx" --nologo -v q; if ($LASTEXITCODE) { exit 1 }
dotnet run --project "$root/dotnet/Rql.Conformance" -v q -- check; if ($LASTEXITCODE) { exit 1 }
Push-Location "$root/typescript"; if (-not (Test-Path node_modules)) { npm install --no-audit --no-fund }; npm test; $ts = $LASTEXITCODE; Pop-Location; if ($ts) { exit 1 }
cmake -S "$root/cpp" -B "$root/cpp/build"; cmake --build "$root/cpp/build" --config Release; if ($LASTEXITCODE) { exit 1 }
ctest --test-dir "$root/cpp/build" -C Release --output-on-failure; if ($LASTEXITCODE) { exit 1 }
dotnet run --project "$root/evaluation/runner/Rql.Eval" -v q -- reference --split all; if ($LASTEXITCODE) { exit 1 }
Write-Host "ALL GREEN"
