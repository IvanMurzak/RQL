#!/usr/bin/env bash
# Build and test every runtime against the shared golden vectors. Exit non-zero on any failure.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if [ -z "${DOTNET_ROOT:-}" ] && [ -x "$HOME/.dotnet/dotnet" ]; then export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"; fi
echo "== .NET (unit + conformance, compiled and reflection accessors)"
dotnet test "$ROOT/dotnet/Rql.slnx" --nologo -v q
echo "== .NET conformance runner"
dotnet run --project "$ROOT/dotnet/Rql.Conformance" -v q -- check
echo "== TypeScript"
( cd "$ROOT/typescript" && { [ -d node_modules ] || npm install --no-audit --no-fund; } && npm test )
echo "== C++"
cmake -S "$ROOT/cpp" -B "$ROOT/cpp/build" -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build "$ROOT/cpp/build" -j 8
ctest --test-dir "$ROOT/cpp/build" --output-on-failure
"$ROOT/cpp/build/rql_conformance" "$ROOT"
echo "== Task catalog self-check (reference agent, no LLM)"
"$ROOT/scripts/rql-eval" reference --split all
echo "ALL GREEN"
