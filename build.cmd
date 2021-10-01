@ECHO off

if exist ./bin ( rmdir .\bin /q /s )

dotnet tool restore
dotnet restore


call npm ci  --prefix .\src\eskv.ui\


dotnet fable .\src\eskv.ui\eskv.ui.fsproj  -w .\src\eskv.ui --noRestore
call npm exec --prefix ./src/eskv.ui/ parcel -- build ./src/eskv.ui/App.fs.js ./src/eskv.ui/style.scss
dotnet pack -c Release -o bin/nuget --no-restore
