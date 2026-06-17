#!/bin/sh
set -eu
# Remove only this tool's own packages before packing. NuGet orders prereleases
# alphabetically, so a leftover 0.1.0-dev outranks a fresh 0.1.0-alpha.N and would
# be (re)installed instead of the current build. `dotnet pack` regenerates every
# Tomix.Cli* package, so clearing just those is safe.
rm -f tools-packages/Tomix.Cli*.nupkg
dotnet pack src/Tomix.Cli -o tools-packages --nologo -v q
dotnet tool uninstall -g Tomix.Cli >/dev/null 2>&1 || true
dotnet tool install -g Tomix.Cli --add-source ./tools-packages --prerelease
