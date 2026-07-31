#!/usr/bin/env bash
# Write a diverged copy of samples/deploy-qa, so a deploy against a target seeded from the
# pristine fixture differs in every aspect granular deployment can preserve.
#
# Why this exists: check-deploy-script.sh can only prove an aspect was preserved by showing
# the script kept a value the source does NOT have. When source and target agree the check
# reports "inconclusive" — a warning, which does not fail the run, so a matrix over an
# undiverged model reports every cell clean while proving almost nothing.
#
# Usage:
#   diverge-deploy-qa.sh --out <dir> [--force]
#
#   --out <dir>   Where to write the diverged model (must be empty or absent).
#   --force       Overwrite a non-empty --out.
#
# Exit codes: 0 = written, 1 = a divergence did not apply, 2 = usage error.

set -euo pipefail

OUT=""
FORCE=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --out|-o) OUT="${2:-}"; shift 2 ;;
    --force) FORCE=1; shift ;;
    -h|--help) sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

[[ -n $OUT ]] || { echo "usage: $0 --out <dir> [--force]" >&2; exit 2; }

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$(cd "$HERE/../.." && pwd)/samples/deploy-qa"
[[ -d $SRC ]] || { echo "fixture not found: $SRC" >&2; exit 2; }

if [[ -d $OUT ]] && [[ -n $(ls -A "$OUT" 2>/dev/null) ]]; then
  ((FORCE)) || { echo "$OUT is not empty (pass --force to overwrite)" >&2; exit 2; }
  rm -rf "$OUT"
fi

mkdir -p "$OUT"
cp -R "$SRC/." "$OUT/"

applied=0
failed=0

# Every divergence is verified to have changed its file. A silent no-op — a renamed object, a
# reformatted expression — would put the harness back to reporting inconclusive cells as
# clean, which is the failure this script exists to prevent.
edit() { # $1 = file (relative to OUT), $2 = description, $3.. = filter reading stdin
  local file="$OUT/$1" what="$2"; shift 2
  local before after
  before=$(cksum <"$file")
  "$@" <"$file" >"$file.tmp" && mv "$file.tmp" "$file"
  after=$(cksum <"$file")
  if [[ $before == "$after" ]]; then
    printf '  FAIL  %s: no change — the fixture has drifted away from this divergence\n' "$what"
    failed=1
  else
    printf '  ok    %s\n' "$what"
    applied=$((applied + 1))
  fi
}

echo "diverging $SRC -> $OUT"
echo

# --- shared expressions: the environment-specific values a preserving deploy must keep
edit expressions.tmdl 'expressions: Environment development -> production' \
  sed 's/"development"/"production"/'
edit expressions.tmdl 'expressions: WarehouseName dev -> prod' \
  sed 's/qa_warehouse_dev/qa_warehouse_prod/'

# --- ordinary partitions: a changed query on a plain import table
edit tables/Customers.tmdl 'partitions: Customers query changed' \
  sed 's/"Contoso ("/"Contoso Diverged ("/'

# --- ordinary partitions, collection semantics: dropping one must be visible, since
#     partitions deploy as a whole collection rather than merging per name
edit tables/Products.tmdl 'partitions: Products-Archive dropped from the source' \
  sed '/^	partition Products-Archive = m/,$d'

# --- policy partitions: both the policy itself and the query it generates partitions from
edit tables/Sales.tmdl 'policy: incrementalPeriods 2 -> 4' \
  sed 's/incrementalPeriods: 2/incrementalPeriods: 4/'
edit tables/Sales.tmdl 'policy: Sales query changed (partition and sourceExpression)' \
  sed 's/1, 100}}/1, 999}}/g'

# --- roles: a changed filter, a removed role, and a role that is new in the source
edit "roles/QA Reader.tmdl" 'roles: QA Reader filter DK -> SE' \
  sed 's/Customers\[Country\] = "DK"/Customers[Country] = "SE"/'

rm -f "$OUT/roles/QA Admin.tmdl"
cat >"$OUT/roles/QA Auditor.tmdl" <<'TMDL'
/// Role that exists only in the source, so "roles were preserved" can be told apart from
/// "roles happened to match".
role 'QA Auditor'
	modelPermission: read
TMDL
edit model.tmdl 'roles: QA Admin removed, QA Auditor added' \
  sed "s/ref role 'QA Admin'/ref role 'QA Auditor'/"

# --- structure: deployed by every cell whatever the flags say. If this measure is missing
#     from any generated script, a merge has reached past its aspect into model structure.
edit tables/Customers.tmdl 'structure: Diverged Marker measure added (must appear in every cell)' \
  awk '/^\tmeasure .Customer Count/ && !seen {
         print "\tmeasure \047Diverged Marker\047 = 1"
         print "\t\tformatString: 0"
         print ""
         seen = 1
       }
       { print }'

echo
if ((failed)); then
  echo "some divergences did not apply — the matrix would report inconclusive cells as clean"
  exit 1
fi

echo "$applied divergences applied. Next:"
echo "  tx load \"$OUT\""
echo "  scripts/qa/deploy-script-matrix.sh --model \"$OUT\" --server <endpoint> --database <name> --strict"
