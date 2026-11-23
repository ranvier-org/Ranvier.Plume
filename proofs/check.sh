#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p artifacts
work=$(mktemp -d artifacts/plume-proofs.XXXXXXXX)
scratch="$PWD/$work"
trap 'rm -rf -- "$scratch"' EXIT
dotnet=${DOTNET:-dotnet}
"$dotnet" build tests/Ranvier.Plume.Tests/Ranvier.Plume.Tests.fsproj -c Release --nologo
"$dotnet" fsi --exec proofs/Conformance.fsx "$work/Observations.v" "$@"
cp proofs/{Retention,Edits,Cursors,Snapshots,InputGeometry,Search,Folds,Brackets,Audit}.v "$work/"
cd "$work"
if grep -nE '(^|[[:space:]])(Admitted|admit|give_up|Axiom|Conjecture)([[:space:].]|$)' *.v; then
  echo "Unproved declaration in the Plume theories" >&2
  exit 1
fi
for theory in Retention Edits Cursors Snapshots InputGeometry Search Folds Brackets Observations; do
  rocq compile -Q . Plume "$theory.v"
done
rocq compile -Q . Plume Audit.v > audit.txt
expected=$(grep -c '^Print Assumptions ' Audit.v)
closed=$(grep -c '^Closed under the global context' audit.txt)
if [[ "$expected" != "$closed" ]]; then
  cat audit.txt >&2
  exit 1
fi
rocq check -silent -Q . Plume Plume.Audit Plume.Observations
printf '%s closed proof obligations; %s F# observations checked.\n' "$closed" "$(grep -c '^Example ' Observations.v)"
