# _eskv_

_eskv_ is an in-memory key/value and event store, for education purpose.

## Disclamer
**_eskv_ is not meant to be run in production**. _eskv_ has been created to ease the _learning_ of event sourcing. Use a production-ready event store for production.

## Getting Started

Install _eskv_ as a global dotnet tool:
``` bash
dotnet tool install eskv -g
```
for prerelease version, specify the `--prerelease` flag.

You can also install it as a local dotnet tool:
``` bash
dotnet new tool-manifest
dotnet tool install eskv
```

Then run it:
``` bash
eskv
```
or for a local dotnet tool:
``` bash
dotnet eskv
```

and open [http://localhost:5000](http://localhost:5000) in a browser to access the web ui.

## Usage

```
USAGE: eskv.exe [--help] [--endpoint <string>] [--dev] [--parcel <string>]

OPTIONS:

    --endpoint <string>   eskv http listener endpoint. default is http://localhost:5000
    --dev                 specify dev mode.
    --parcel <string>     parcel dev server url. default is http://localhost:1234
    --help                display this list of options.
```


--endpoint < string >
: the eskv http listener endpoint. Use http://*:5000 to authorize connections over the network, or use it t change port. Default is http://localhost:5000

--dev
: activate development mode. Used only when working on _eskv_ UI development.

--parcel < string >
: The parcel dev server url used in development mode.

--help
: display help.

## eskv.client nuget

_eskv.client_ nuget contains _eskv_ client library to interact with _eskv_ server.

Add it to your project using the IDE, or the following command:
``` bash
 dotnet add package eskv.client
 ```

 In an F# script, you can reference it with a #r directive:
 ``` fsharp
 #r "nuget: eskv.client"
 ```

