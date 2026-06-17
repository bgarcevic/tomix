# Remove only this tool's own packages before packing. NuGet orders prereleases
# alphabetically, so a leftover `0.1.0-dev` outranks a fresh `0.1.0-alpha.N` and would be
# (re)installed instead of the current build. `dotnet pack` regenerates every `Tomix.Cli*`
# package, so clearing just those is safe -- anything else in tools-packages is left alone.
Remove-Item tools-packages/Tomix.Cli*.nupkg -Force -ErrorAction SilentlyContinue

dotnet pack src/Tomix.Cli -o tools-packages --nologo -v q
dotnet tool uninstall -g Tomix.Cli 2>$null | Out-Null
dotnet tool install -g Tomix.Cli --add-source ./tools-packages --prerelease
