# Developer task runner for the common inner loop. Exists because recipes like
# `TOMIX_UPDATE_SNAPSHOTS=1 dotnet test ...` are not valid PowerShell, so the
# documented commands must work for both halves of the audience. Works from
# any directory; anchors on the script location like .\tx.ps1.
#
# Usage: dev.ps1 <build|test|format|snapshot|docs> [extra args passed through]
param(
  [Parameter(Position = 0)]
  [string]$Task = '',

  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$TaskArgs
)

# Anchor on the repo root (the script's location), like .\tx.ps1, so every
# task works no matter which directory the script is invoked from.
Push-Location (Join-Path $PSScriptRoot ..)
try {

  switch ($Task) {
    'build'   { dotnet build @TaskArgs }
    'test'    { dotnet test @TaskArgs }
    'format'  { dotnet format @TaskArgs }
    # Keep the env var, the filter, and the failure message in
    # CommandSurfaceSnapshotTests.cs pointing at this recipe.
    'snapshot' {
      $env:TOMIX_UPDATE_SNAPSHOTS = '1'
      try { dotnet test --filter CommandSurfaceSnapshotTests @TaskArgs }
      finally { Remove-Item Env:\TOMIX_UPDATE_SNAPSHOTS -ErrorAction SilentlyContinue }
    }
    'docs'    { uv run zensical build --clean --strict @TaskArgs }
    default {
      Write-Host "usage: .\scripts\dev.ps1 <build|test|format|snapshot|docs> [args]"
      Write-Host "  build     dotnet build"
      Write-Host "  test      dotnet test"
      Write-Host "  format    dotnet format (applies fixes; CI verifies with --verify-no-changes)"
      Write-Host "  snapshot  regenerate CommandSurface.approved.txt"
      Write-Host "  docs      strict docs-site build (what CI runs; requires uv)"
      exit 2
    }
  }

}
finally {
  Pop-Location
}

exit $LASTEXITCODE
