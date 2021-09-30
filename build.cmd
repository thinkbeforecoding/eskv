@ECHO off

if exist ./bin ( rmdir .\bin /q /s )

dotnet tool restore
dotnet restore

npm install .\src\kv.ui
dotnet fable -w .\src\kv.ui --noRestore
npm exec --prefix ./src/kv.ui/ parcel -- build ./src/kv.ui/App.fs.js ./src/kv.ui/style.scss
dotnet pack -c Release -o bin/nuget --no-restore
