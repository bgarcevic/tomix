#!/usr/bin/env pwsh
# Dev wrapper: run tx from source without packing/installing. `.\tx.ps1 <args>`.
# Forward piped input explicitly: a script does not pass its pipeline to native commands.
if ($MyInvocation.ExpectingInput) {
    $input | dotnet run --project "$PSScriptRoot/src/Tomix.Cli" -v quiet -- @args
} else {
    dotnet run --project "$PSScriptRoot/src/Tomix.Cli" -v quiet -- @args
}
exit $LASTEXITCODE
