#!/usr/bin/env pwsh
# Dev wrapper: run tx from source without packing/installing. `.\tx.ps1 <args>`.
dotnet run --project "$PSScriptRoot/src/Tomix.Cli" -v quiet -- @args
exit $LASTEXITCODE
