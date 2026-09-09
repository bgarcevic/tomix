## Summary

<!-- What changes and why? Link the issue ("Closes #NNN") when there is one. -->

## Checklist

- [ ] Tests added or updated for the changed behavior
- [ ] Matching page under `docs/commands/` updated (if a command, argument, or option changed)
- [ ] `CommandSurface.approved.txt` regenerated (if the command surface changed): `.\scripts\dev.ps1 snapshot` (Windows) or `./scripts/dev.sh snapshot` (macOS/Linux)
- [ ] CHANGELOG entry added under `[Unreleased]`
- [ ] Machine output preserved (JSON field names, exit codes, flag and command names)
