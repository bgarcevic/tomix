dotnet pack src/Mdl.Cli -o tools-packages --nologo -v q
dotnet tool update -g Mdl.Cli --add-source ./tools-packages --prerelease
