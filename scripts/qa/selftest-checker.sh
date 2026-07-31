#!/usr/bin/env bash
# Prove check-deploy-script.sh actually catches what it claims to.
#
# Generates a clean script from a sample model (offline — --deploy-full needs no server),
# asserts it passes, then mutates it once per invariant and asserts the matching check
# fires. A harness nobody has watched fail is not evidence.
#
# Usage: selftest-checker.sh [--tx <cmd>] [--fixture <script.xmla>] [--out <dir>]

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
CHECK="$HERE/check-deploy-script.sh"
TX="$ROOT/tx"
FIXTURE=""
OUT="${TMPDIR:-/tmp}/tx-deploy-qa-selftest"
DB="AI Sample"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tx) TX="${2:-}"; shift 2 ;;
    --fixture) FIXTURE="${2:-}"; shift 2 ;;
    --out) OUT="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

command -v jq >/dev/null || { echo "jq is required (brew install jq)" >&2; exit 2; }
mkdir -p "$OUT"

if [[ -z $FIXTURE ]]; then
  FIXTURE="$OUT/fixture.xmla"
  if [[ ! -s $FIXTURE ]]; then
    echo "generating fixture from samples (no server contacted)..."
    "$TX" deploy "$ROOT/samples/Artificial Intelligence Sample.SemanticModel" \
      --xmla "$FIXTURE" --deploy-full --skip-bpa --yes --quiet \
      --server "powerbi://api.powerbi.com/v1.0/myorg/selftest" --database "$DB" >/dev/null
  fi
fi
[[ -s $FIXTURE ]] || { echo "no fixture at $FIXTURE" >&2; exit 1; }

pass=0
fail=0

# $1 = case name, $2 = jq mutation ("" = none), $3 = expected substring, $4.. = extra check args
expect_caught() {
  local name="$1" mutation="$2" expect="$3"; shift 3
  local file="$OUT/$name.xmla" out
  if [[ -z $mutation ]]; then cp "$FIXTURE" "$file"; else jq "$mutation" "$FIXTURE" >"$file"; fi

  out=$("$CHECK" "$file" --database "$DB" --quiet "$@" 2>&1) || true
  if grep -qF -- "$expect" <<<"$out"; then
    printf '  ok      %-22s caught: %s\n' "$name" "$expect"
    pass=$((pass + 1))
  else
    printf '  MISSED  %-22s expected to report: %s\n' "$name" "$expect"
    printf '%s\n' "$out" | sed 's/^/            /'
    fail=$((fail + 1))
  fi
}

# Differential cases need their own reference, and some assert the ABSENCE of a failure.
# $1 = case name, $2 = reference/base, $3 = jq mutation, $4 = expected substring or "-" for
# "must report no failure at all", $5.. = extra check args.
expect_pair() {
  local name="$1" base="$2" mutation="$3" expect="$4"; shift 4
  local file="$OUT/$name.xmla" out
  jq "$mutation" "$base" >"$file"

  out=$("$CHECK" "$file" --database "$DB" --quiet --reference "$base" "$@" 2>&1) || true
  if [[ $expect == "-" ]]; then
    if grep -q "FAIL" <<<"$out"; then
      printf '  MISSED  %-22s expected no failure, got:\n' "$name"
      printf '%s\n' "$out" | sed 's/^/            /'
      fail=$((fail + 1))
    else
      printf '  ok      %-22s correctly reports no failure\n' "$name"
      pass=$((pass + 1))
    fi
  elif grep -qF -- "$expect" <<<"$out"; then
    printf '  ok      %-22s caught: %s\n' "$name" "$expect"
    pass=$((pass + 1))
  else
    printf '  MISSED  %-22s expected to report: %s\n' "$name" "$expect"
    printf '%s\n' "$out" | sed 's/^/            /'
    fail=$((fail + 1))
  fi
}

expect_clean() {
  local name="$1"; shift
  local out
  if out=$("$CHECK" "$FIXTURE" --database "$DB" --quiet "$@" 2>&1); then
    printf '  ok      %-22s no failures on a clean script\n' "$name"
    pass=$((pass + 1))
  else
    printf '  MISSED  %-22s clean script reported failures:\n' "$name"
    printf '%s\n' "$out" | sed 's/^/            /'
    fail=$((fail + 1))
  fi
}

echo "== baseline =="
expect_clean "clean-full" --deployed full --cloud
expect_clean "clean-vs-reference" --reference "$FIXTURE"

echo "== structural invariants =="
expect_caught wrong-database \
  '.createOrReplace.object.database = "Some Other Model"' \
  "deploy would hit the wrong database" --deployed full
expect_caught renamed-payload \
  '.createOrReplace.database.name = "Something Else"' \
  "Power BI rejects dataset renames" --deployed full
expect_caught duplicate-name \
  '.createOrReplace.database.model.tables += [{"name":"accounts","partitions":[{"name":"p","source":{"type":"m","expression":"1"}}]}]' \
  'duplicate name "accounts"' --deployed full
expect_caught table-without-partitions \
  '.createOrReplace.database.model.tables[0] |= del(.partitions)' \
  "has no partitions" --deployed full
expect_caught dangling-datasource \
  '.createOrReplace.database.model.tables[0].partitions[0].source.dataSource = "GoneAway"' \
  'binds to missing dataSource "GoneAway"' --deployed full
expect_caught dangling-expression \
  '.createOrReplace.database.model.tables[0].partitions[0].source.expressionSource = "GoneAway"' \
  'binds to missing expression "GoneAway"' --deployed full
expect_caught orphaned-relationship \
  '.createOrReplace.database.model.tables[0].name = "Renamed"' \
  "references a missing table" --deployed full
expect_caught stale-member-id \
  '.createOrReplace.database.model.roles = [{"name":"R","members":[{"memberName":"a@b.c","memberId":"guid"}]}]' \
  "still carry memberId" --deployed full --cloud
expect_caught leaked-credential \
  '.createOrReplace.database.model.dataSources = [{"name":"DS","connectionString":"Server=x;Password=hunter2"}]' \
  "possible credential material" --deployed full

echo "== differential invariants (vs the pure-source reference) =="
expect_caught structure-clobbered \
  '(.createOrReplace.database.model.tables[] | select(.measures) | .measures[0].expression) = "999"' \
  "model structure differs from source outside the preserved aspects" --reference "$FIXTURE"
expect_caught aspect-not-deployed \
  '.createOrReplace.database.model.expressions[0].expression = "\"target-value\""' \
  "was asked to deploy but does not match the source" --reference "$FIXTURE" --deployed expressions
expect_caught source-entry-dropped \
  'del(.createOrReplace.database.model.expressions[0])' \
  "is missing from the script" --reference "$FIXTURE"
echo "== aspects that deploy independently =="
# A base with roles, so definitions can be held equal while membership differs — the fixture
# the README asks you to set up, which must not read as a failure.
ROLES_BASE="$OUT/base-roles.xmla"
jq '.createOrReplace.database.model.roles =
      [{"name":"Reader","modelPermission":"read","members":[{"memberName":"source@x.com"}]}]' \
  "$FIXTURE" >"$ROLES_BASE"

expect_pair members-from-target "$ROLES_BASE" \
  '.createOrReplace.database.model.roles[0].members = [{"memberName":"target@x.com"}]' \
  - --deployed roles
expect_pair role-definition-not-deployed "$ROLES_BASE" \
  '.createOrReplace.database.model.roles[0].modelPermission = "readRefresh"' \
  "role definitions was asked to deploy but does not match the source" --deployed roles
expect_pair ordinary-partitions-not-deployed "$FIXTURE" \
  '.createOrReplace.database.model.tables[0].partitions[0].name = "kept-from-target"' \
  "was asked to deploy but kept non-source partitions" --deployed partitions
expect_pair policy-partitions-exempt "$FIXTURE" \
  '.createOrReplace.database.model.tables[0] |= (
     .refreshPolicy = {"policyType":"basic","sourceExpression":"let S = Sql in S"}
     | .partitions[0].name = "target-2024")' \
  - --deployed partitions
expect_pair policy-partitions-not-deployed "$FIXTURE" \
  '.createOrReplace.database.model.tables[0] |= (
     .refreshPolicy = {"policyType":"basic","sourceExpression":"let S = Sql in S"}
     | .partitions[0].name = "target-2024")' \
  "policy table partitions was asked to deploy but kept non-source partitions" \
  --deployed partitions,policy-partitions

echo "== strict mode =="
# A script identical to its reference cannot show anything was preserved. That must warn by
# default (so a matrix over an arbitrary model still runs) and fail under --strict.
expect_pair inconclusive-warns "$FIXTURE" '.' -
expect_pair inconclusive-fails-strict "$FIXTURE" '.' \
  "FAIL  shared expressions preserved, but matches the source" --strict

echo "== deploy-qa fixture divergence =="
# The live matrix can only prove preservation if the diverged source differs from the pristine
# fixture in every aspect. Checked here, offline, because the failure mode is silent: an edit
# to samples/deploy-qa that collides with a divergence turns a live cell inconclusive, and
# without --strict that still reads as clean.
QA_SRC="$ROOT/samples/deploy-qa"
QA_DIVERGED="$OUT/deploy-qa-diverged"
"$HERE/diverge-deploy-qa.sh" --out "$QA_DIVERGED" --force >"$OUT/diverge.log" 2>&1 || {
  printf '  MISSED  %-22s diverge-deploy-qa.sh failed:\n' "divergence-applies"
  sed 's/^/            /' "$OUT/diverge.log"
  fail=$((fail + 1))
}

qa_script() { # $1 = model dir, $2 = output
  "$TX" deploy "$1" --xmla "$2" --deploy-full --skip-bpa --yes --quiet --non-interactive \
    --server "powerbi://api.powerbi.com/v1.0/myorg/selftest" --database deploy-qa >/dev/null
}
qa_script "$QA_SRC" "$OUT/qa-pristine.xmla"
qa_script "$QA_DIVERGED" "$OUT/qa-diverged.xmla"

expect_differs() { # $1 = aspect label, $2 = jq path
  local label="$1" path="$2" a b
  a=$(jq -S "$2" "$OUT/qa-pristine.xmla")
  b=$(jq -S "$2" "$OUT/qa-diverged.xmla")
  if [[ $a != "$b" ]]; then
    printf '  ok      %-22s diverges, so a live cell can be conclusive\n' "$label"
    pass=$((pass + 1))
  else
    printf '  MISSED  %-22s identical in pristine and diverged — the live cell would only WARN\n' "$label"
    fail=$((fail + 1))
  fi
}

QM='.createOrReplace.database.model'
expect_differs shared-expressions "$QM.expressions"
expect_differs role-definitions "$QM.roles | map(del(.members))"
expect_differs partitions-plain "$QM.tables[] | select(.name==\"Customers\") | .partitions"
expect_differs partitions-collection "$QM.tables[] | select(.name==\"Products\") | .partitions"
expect_differs partitions-policy \
  "$QM.tables[] | select(.name==\"Sales\") | {p: .partitions, r: .refreshPolicy}"

# The marker measure must survive every flag combination; if it can go missing, a merge is
# reaching past its aspect into model structure.
if jq -e "$QM.tables[] | select(.name==\"Customers\") | .measures[]?
          | select(.name==\"Diverged Marker\")" "$OUT/qa-diverged.xmla" >/dev/null; then
  printf '  ok      %-22s present in the diverged source\n' "structure-marker"
  pass=$((pass + 1))
else
  printf '  MISSED  %-22s Diverged Marker measure absent\n' "structure-marker"
  fail=$((fail + 1))
fi

printf '\n  %d passed, %d missed\n' "$pass" "$fail"
((fail == 0))
