#!/usr/bin/env bash
# Prints "<version> <bump>" for the next stable release, derived from the
# conventional-commit subjects (and BREAKING CHANGE footers) merged since the
# last stable tag. Prints nothing when there is nothing to release.
#
#   breaking (type!: / BREAKING CHANGE)  -> major; minor while major is 0
#   feat                                 -> minor
#   anything else                        -> patch
#
# The 0.x rule mirrors release-please's bump-minor-pre-major: SemVer allows
# breaking changes in 0.x minors, so a stray "!" can never produce 1.0.0.
# 1.0.0 is a deliberate manual tag, and every major release waits for approval
# in the major-release environment (see .github/workflows/release.yml).
set -euo pipefail

last_tag="$(git describe --tags --abbrev=0 --match 'v[0-9]*' --exclude '*-*' 2>/dev/null || echo v0.0.0)"
range="${last_tag}..HEAD"
[[ "${last_tag}" == "v0.0.0" ]] && range="HEAD"

bump=""
while IFS= read -r -d $'\x1e' entry; do
  entry="${entry#$'\n'}"
  subject="${entry%%$'\x1f'*}"
  body="${entry#*$'\x1f'}"
  [[ -z "${subject}" ]] && continue
  # The release commit itself carries no change.
  [[ "${subject}" =~ ^chore\(release\) ]] && continue
  if [[ "${subject}" =~ ^[a-z]+(\([^\)]*\))?!: ]] || grep -q '^BREAKING[ -]CHANGE:' <<<"${body}"; then
    bump="major"
  elif [[ "${subject}" =~ ^feat(\([^\)]*\))?: ]]; then
    [[ "${bump}" != major ]] && bump="minor"
  else
    [[ -z "${bump}" ]] && bump="patch"
  fi
done < <(git log --format='%s%x1f%b%x1e' "${range}")

[[ -z "${bump}" ]] && exit 0

IFS=. read -r major minor patch <<<"${last_tag#v}"
if [[ "${bump}" == major && "${major}" -eq 0 ]]; then
  bump="minor"
fi
case "${bump}" in
  major) echo "$((major + 1)).0.0 ${bump}" ;;
  minor) echo "${major}.$((minor + 1)).0 ${bump}" ;;
  patch) echo "${major}.${minor}.$((patch + 1)) ${bump}" ;;
esac
