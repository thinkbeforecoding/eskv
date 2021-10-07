@echo off

dotnet tool restore

if not exist .\src\eskv.ui\node_modules ( call npm ci  --prefix .\src\eskv.ui\ )

dotnet fable watch .\src\eskv.ui\eskv.ui.fsproj  --run "call npm exec --prefix ./src/eskv.ui/ parcel -- ./src/eskv.ui/App.fs.js ./src/eskv.ui/style.scss"