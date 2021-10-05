#! /bin/bash

if [ -d ./bin ]; then rm -Rf ./bin; fi

dotnet tool restore
dotnet restore


npm ci  --prefix ./src/eskv.ui/


dotnet fable ./src/eskv.ui/eskv.ui.fsproj  -w ./src/eskv.ui --noRestore
npm exec --prefix ./src/eskv.ui/ parcel -- build ./src/eskv.ui/App.fs.js ./src/eskv.ui/style.scss
dotnet pack -c Release -o bin/nuget --no-restore
