dotnet pack src/Mdl.Cli -o tools-packages --nologo -v q
dotnet tool uninstall -g Mdl.Cli 2>$null | Out-Null
dotnet tool install -g Mdl.Cli --add-source ./tools-packages --prerelease
