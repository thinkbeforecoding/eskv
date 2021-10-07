#! /bin/bash

dotnet tool restore

if [ ! -d ./src/eskv.ui/node_modules ]; then npm ci  --prefix ./src/eskv.ui/; fi

dotnet fable watch ./src/eskv.ui/eskv.ui.fsproj  --run npm exec --prefix ./src/eskv.ui/ parcel -- ./src/eskv.ui/App.fs.js ./src/eskv.ui/style.scss
