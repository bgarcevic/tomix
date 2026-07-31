#!/usr/bin/env bash
# Assert invariants on a TMSL script produced by `tx deploy --xmla`.
#
# Read-only: inspects a generated script, never contacts a server. Intended as the
# offline gate before spending live deploys — see scripts/qa/README.md.
#
# Usage:
#   check-deploy-script.sh <script.xmla> --database <target-name> [options]
#
#   --reference <full.xmla>   Pure-source script (from --deploy-full) to diff against.
#                             Enables the preserve/overwrite differential checks.
#   --deployed <list>         Comma-separated aspects this script was told to deploy:
#                             connections,partitions,policy-partitions,expressions,
#                             roles,role-members,full  (default: none = preserve all)
#   --cloud                   Target is powerbi:// or asazure:// (role member ids must be stripped).
#   --strict                  Treat an inconclusive preservation check as a failure. An aspect
#                             where source and target agree proves nothing, so a run that is
#                             meant to prove preservation should not pass on one. Use with a
#                             target seeded from samples/deploy-qa and a source diverged by
#                             scripts/qa/diverge-deploy-qa.sh.
#   --quiet                   Only print WARN/FAIL lines.
#
# Exit codes: 0 = no failures, 1 = at least one FAIL, 2 = usage error.

set -euo pipefail

SCRIPT=""
DATABASE=""
REFERENCE=""
DEPLOYED=""
CLOUD=0
STRICT=0
QUIET=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --database) DATABASE="${2:-}"; shift 2 ;;
    --reference) REFERENCE="${2:-}"; shift 2 ;;
    --deployed) DEPLOYED="${2:-}"; shift 2 ;;
    --cloud) CLOUD=1; shift ;;
    --strict) STRICT=1; shift ;;
    --quiet) QUIET=1; shift ;;
    -h|--help) sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) echo "unknown option: $1" >&2; exit 2 ;;
    *) SCRIPT="$1"; shift ;;
  esac
done

[[ -n $SCRIPT && -n $DATABASE ]] || { echo "usage: $0 <script.xmla> --database <name> [options]" >&2; exit 2; }
[[ -f $SCRIPT ]] || { echo "no such script: $SCRIPT" >&2; exit 2; }
command -v jq >/dev/null || { echo "jq is required (brew install jq)" >&2; exit 2; }

fails=0
warns=0
fail() { printf '  FAIL  %s\n' "$1"; fails=$((fails + 1)); }
warn() { printf '  WARN  %s\n' "$1"; warns=$((warns + 1)); }
# An aspect where source and target agree cannot show whether it was preserved. That is a
# coverage hole in the fixture, not a product defect, so it warns by default and fails under
# --strict — where the whole point of the run is that no such hole exists.
inconclusive() { ((STRICT)) && fail "$1" || warn "$1"; }
ok()   { ((QUIET)) || printf '  ok    %s\n' "$1"; }
info() { ((QUIET)) || printf '  info  %s\n' "$1"; }

deploys() { [[ ",$DEPLOYED," == *",$1,"* ]]; }
if deploys full; then
  DEPLOYED="connections,partitions,policy-partitions,expressions,roles,role-members,full"
fi

printf '%s\n' "$SCRIPT${DEPLOYED:+  (deploying: $DEPLOYED)}"

# ---------------------------------------------------------------- well-formedness

jq -e . "$SCRIPT" >/dev/null 2>&1 || { fail "not valid JSON"; exit 1; }
ok "valid JSON"

root=$(jq -r 'keys_unsorted | join(",")' "$SCRIPT")
[[ $root == createOrReplace ]] \
  && ok "single createOrReplace command" \
  || fail "root should be exactly createOrReplace, got: $root"

body=$(jq -r '.createOrReplace | keys_unsorted | join(",")' "$SCRIPT")
[[ $body == "object,database" ]] \
  || fail "createOrReplace should hold object+database, got: $body"

jq -e '.createOrReplace.database.model.tables | length > 0' "$SCRIPT" >/dev/null \
  || fail "model has no tables"

jq -e '.createOrReplace.database.compatibilityLevel' "$SCRIPT" >/dev/null \
  || fail "database has no compatibilityLevel"

# ------------------------------------------------------------------- target identity

addressed=$(jq -r '.createOrReplace.object.database // ""' "$SCRIPT")
pinned=$(jq -r '.createOrReplace.database.name // ""' "$SCRIPT")
dbid=$(jq -r '.createOrReplace.database.id // ""' "$SCRIPT")

lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }

if [[ $(lower "$addressed") == "$(lower "$DATABASE")" ]]; then
  ok "addresses the target database ($addressed)"
else
  fail "addresses \"$addressed\" but the target is \"$DATABASE\" — deploy would hit the wrong database"
fi

if [[ $(lower "$pinned") == "$(lower "$addressed")" ]]; then
  [[ $pinned == "$addressed" ]] || info "name casing differs from the request ($pinned vs $DATABASE) — expected, the target's own name wins"
  ok "payload name pinned to the target (no rename)"
else
  fail "payload name \"$pinned\" != addressed \"$addressed\" — Power BI rejects dataset renames"
fi

if [[ -z $dbid ]]; then
  fail "payload has no id"
elif [[ -n $REFERENCE ]] && ! deploys full; then
  # A preserving deploy reads the target, so the id should be the target's own
  # (usually a guid) rather than a copy of the name.
  [[ $(lower "$dbid") == "$(lower "$pinned")" ]] \
    && warn "id equals the database name — ID pinning may not have taken effect (benign if the target's real id is its name)" \
    || ok "id pinned to the target's existing id"
fi

# ------------------------------------------------------------- collection integrity
# Analysis Services names are case-insensitive: two entries differing only by case
# make the TMSL invalid, and a case-sensitive merge is exactly how that happens.

dupes_prog=$(cat <<'JQ'
def dupes($lbl; arr):
  (arr // [])
  | map((.name // .memberName // "") | ascii_downcase)
  | group_by(.) | map(select(length > 1))
  | map("\($lbl): duplicate name \"\(.[0])\" appears \(length)x");

.createOrReplace.database.model as $m
| dupes("model.tables"; $m.tables)
  + dupes("model.dataSources"; $m.dataSources)
  + dupes("model.expressions"; $m.expressions)
  + dupes("model.roles"; $m.roles)
  + dupes("model.perspectives"; $m.perspectives)
  + dupes("model.cultures"; $m.cultures)
  + dupes("model.relationships"; $m.relationships)
  + ([$m.tables[]? | dupes("table[\(.name)].columns"; .columns)
       + dupes("table[\(.name)].measures"; .measures)
       + dupes("table[\(.name)].partitions"; .partitions)
       + dupes("table[\(.name)].hierarchies"; .hierarchies)] | add // [])
  + ([$m.roles[]? | dupes("role[\(.name)].members"; .members)
       + dupes("role[\(.name)].tablePermissions"; .tablePermissions)] | add // [])
| .[]
JQ
)
dupes=$(jq -r "$dupes_prog" "$SCRIPT")
if [[ -z $dupes ]]; then
  ok "no duplicate names in any collection (case-insensitive)"
else
  while IFS= read -r line; do fail "$line"; done <<<"$dupes"
fi

# A table with no partition is invalid TMSL. This is the detector for a preserved-by-name
# merge dropping the partitions of a table that has no counterpart on the target.
nopart=$(jq -r '
  .createOrReplace.database.model.tables[]?
  | select(has("calculationGroup") | not)
  | select((.partitions // []) | length == 0)
  | .name' "$SCRIPT")
if [[ -z $nopart ]]; then
  ok "every table has at least one partition"
else
  while IFS= read -r t; do fail "table[$t] has no partitions — the target would reject this"; done <<<"$nopart"
fi

# Preserved partitions can outlive the data source or shared expression they bind to.
dangling_prog=$(cat <<'JQ'
.createOrReplace.database.model as $m
| ([$m.dataSources[]?.name | ascii_downcase]) as $ds
| ([$m.expressions[]?.name | ascii_downcase]) as $ex
| [ $m.tables[]? as $t
    | $t.partitions[]?
    | . as $p
    | (if ($p.source.dataSource // null) != null and (($ds | index($p.source.dataSource | ascii_downcase)) == null)
       then "table[\($t.name)].partition[\($p.name)] binds to missing dataSource \"\($p.source.dataSource)\""
       else empty end),
      (if ($p.source.expressionSource // null) != null and (($ex | index($p.source.expressionSource | ascii_downcase)) == null)
       then "table[\($t.name)].partition[\($p.name)] binds to missing expression \"\($p.source.expressionSource)\""
       else empty end) ]
| .[]
JQ
)
dangling=$(jq -r "$dangling_prog" "$SCRIPT")
if [[ -z $dangling ]]; then
  ok "every partition binds to a data source / expression that exists"
else
  while IFS= read -r line; do fail "$line"; done <<<"$dangling"
fi

relbad=$(jq -r '
  .createOrReplace.database.model as $m
  | ([$m.tables[]?.name | ascii_downcase]) as $t
  | [ $m.relationships[]? as $r
      | select((($t | index($r.fromTable | ascii_downcase)) == null)
               or (($t | index($r.toTable | ascii_downcase)) == null))
      | "relationship \($r.name // "?") references a missing table (\($r.fromTable) -> \($r.toTable))" ]
  | .[]' "$SCRIPT")
if [[ -z $relbad ]]; then
  ok "every relationship endpoint resolves to a table"
else
  while IFS= read -r line; do fail "$line"; done <<<"$relbad"
fi

# ------------------------------------------------------------------- cloud specifics

if ((CLOUD)); then
  n=$(jq -r '[.. | objects | select(has("memberId"))] | length' "$SCRIPT")
  [[ $n == 0 ]] \
    && ok "no service-assigned memberId values" \
    || fail "$n role member(s) still carry memberId — cloud redeploys fail on stale ids"
fi

# ------------------------------------------------------- credential leak (issue #123)
# Script output must never carry restricted information. Names can legitimately contain
# these words, so hits are reported for review rather than treated as proven leaks.

if hits=$(grep -n -i -E 'password|pwd=|accountkey|sharedaccesssignature|access_token|client_secret|"Bearer |apikey|api_key' "$SCRIPT"); then
  warn "possible credential material — inspect these lines (nothing here should be a real secret):"
  # head before sed: with 20+ hits, head exiting early would SIGPIPE the producer, and
  # under pipefail that exit 141 aborts the checker mid-report.
  head -20 <<<"$hits" | sed 's/^/          /'
else
  ok "no credential-shaped strings in script output"
fi

# ------------------------------------------------------------- differential checks

if [[ -z $REFERENCE ]]; then
  info "no --reference given; skipping preserve/overwrite differentials"
else
  [[ -f $REFERENCE ]] || { fail "reference not found: $REFERENCE"; exit 1; }

  # The merge is only allowed to touch the preserved aspects. Everything else — tables,
  # columns, measures, hierarchies, relationships, annotations — must be byte-identical to
  # the pure-source script. This is the broadest guard in the harness: it catches any splice
  # that reaches past its aspect, for every flag combination, without knowing the target.
  strip='.createOrReplace.database.model
         | del(.dataSources, .expressions, .roles)
         | (if .tables then .tables |= map(del(.partitions, .refreshPolicy)) else . end)'
  if diff -q <(jq -S "$strip" "$SCRIPT") <(jq -S "$strip" "$REFERENCE") >/dev/null; then
    ok "model structure identical to source outside the preserved aspects"
  else
    fail "model structure differs from source outside the preserved aspects:"
    # diff exits 1 whenever the files differ — which in this branch is always — so without
    # the || true, pipefail + set -e killed the checker here every time this guard fired,
    # skipping every remaining check.
    { diff <(jq -S "$strip" "$REFERENCE") <(jq -S "$strip" "$SCRIPT") || true; } | head -40 | sed 's/^/          /'
  fi

  # For each aspect: deployed => must equal source; preserved => must differ from source,
  # or the check is inconclusive because target and source happen to agree.
  aspect() {
    local label="$1" path="$2" deployed="$3"
    local a b
    a=$(jq -S "$path" "$SCRIPT")
    b=$(jq -S "$path" "$REFERENCE")
    if [[ $a == "$b" && ( $a == "[]" || $a == "null" ) ]]; then
      info "$label: none in either model — nothing to compare"
      return
    fi
    if [[ $deployed == 1 ]]; then
      [[ $a == "$b" ]] \
        && ok "$label deployed from source" \
        || fail "$label was asked to deploy but does not match the source"
    elif [[ $a == "$b" ]]; then
      inconclusive "$label preserved, but matches the source — inconclusive; make the target differ to test this"
    else
      ok "$label preserved from the target"
    fi
  }

  M='.createOrReplace.database.model'
  aspect "data sources"       "$M.dataSources // []" "$(deploys connections && echo 1 || echo 0)"
  aspect "shared expressions" "$M.expressions // []" "$(deploys expressions && echo 1 || echo 0)"

  # Role definitions and role membership deploy independently, so they are compared
  # independently: --deploy-roles takes the definitions from the source while the members
  # still come from the target, and comparing the whole roles array would fail on exactly the
  # fixture this harness asks you to set up.
  if deploys roles; then
    aspect "role definitions" "$M.roles // [] | map(del(.members))" 1
    aspect "role members" "[$M.roles[]? | {(.name): (.members // [])}]" \
      "$(deploys role-members && echo 1 || echo 0)"
  else
    aspect "roles" "$M.roles // []" 0
  fi

  # Partitions split into two populations with different contracts: --deploy-partitions
  # overwrites ordinary tables, while a table whose refresh policy carries a sourceExpression
  # keeps the target's partitions until --deploy-policy-partitions is passed too. Comparing
  # them as one blob lets a regression that preserves everything pass as success.
  partition_groups=$(jq -n --slurpfile s "$SCRIPT" --slurpfile r "$REFERENCE" '
    def tables(m): [m[0].createOrReplace.database.model.tables[]?
      | {name: (.name | ascii_downcase),
         parts: (.partitions // []),
         policy: ((.refreshPolicy.sourceExpression // null) != null)}];
    tables($s) as $S | tables($r) as $R
    | [ $S[] as $st | ($R[] | select(.name == $st.name)) as $rt
        # Either side claiming a policy is treated as protected: a table the target has under
        # policy but the source does not must not be reported as a preservation failure.
        | {name: $st.name, policy: ($st.policy or $rt.policy), same: ($st.parts == $rt.parts)} ]')

  group() { # $1 = label, $2 = policy true/false, $3 = deployed 0/1
    local label="$1" policy="$2" deployed="$3" total differing
    total=$(jq -r --argjson p "$policy" '[.[] | select(.policy == $p)] | length' <<<"$partition_groups")
    differing=$(jq -r --argjson p "$policy" \
      '[.[] | select(.policy == $p and (.same | not)) | .name] | join(", ")' <<<"$partition_groups")

    if [[ $total == 0 ]]; then
      info "$label: none in this model — that contract is untested here"
    elif [[ $deployed == 1 ]]; then
      [[ -z $differing ]] \
        && ok "$label deployed from source ($total table(s))" \
        || fail "$label was asked to deploy but kept non-source partitions: $differing"
    elif [[ -z $differing ]]; then
      inconclusive "$label preserved, but every table matches the source — inconclusive; make the target differ to test this"
    else
      ok "$label preserved from the target ($differing)"
    fi
  }

  group "ordinary table partitions" false "$(deploys partitions && echo 1 || echo 0)"
  group "policy table partitions" true "$(deploys policy-partitions && echo 1 || echo 0)"

  # Preserving shared expressions still deploys expressions that are new in the source, so no
  # source-side name may go missing. Same contract for data sources.
  missing=$(jq -r -n --slurpfile s "$SCRIPT" --slurpfile r "$REFERENCE" '
    def names(m; k): [m.createOrReplace.database.model[k][]?.name | ascii_downcase];
    ["expressions", "dataSources"] as $keys
    | [ $keys[] as $k
        | (names($r[0]; $k) - names($s[0]; $k))[] as $n
        | "\($k): source entry \"\($n)\" is missing from the script" ]
    | .[]')
  if [[ -z $missing ]]; then
    ok "no source-side data source or expression was dropped"
  else
    while IFS= read -r line; do fail "$line"; done <<<"$missing"
  fi

fi

# --------------------------------------------------------------------------- verdict

printf '  ----  %d failure(s), %d warning(s)\n\n' "$fails" "$warns"
((fails == 0))
