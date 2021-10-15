# _eskv.client_

_eskv.client_ is a client library for [_eskv_](https://www.nuget.org/packages/eskv)

## Getting started

Add it to your project using the IDE, or the following command:
``` bash
dotnet add package eskv.client
```

In an F# script, you can reference it with a #r directive:
``` fsharp
#r "nuget: eskv.client"
```

For beta versions, use the `--prerelease` flag on the cli or specify the version:
``` fsharp
#r "nuget: eskv.client, Version=*-beta*"
```

Save your first value in the _eskv_ with the following lines in a eskv.fsx script file:
 
``` fsharp
#r "nuget: eskv.client"
open eskv

let client = EskvClient()

client.Save("Hello","World")
```

Execute it in F# interactive or with `dotnet fsi eskv.fsx` with eskv server running, and a browser open on http://localhost:5000. You should see a new `Hello` key with value `World` appear in the default container.

 ## Copyright and License

Code copyright Jérémie Chassaing. _eskv_ and _eskv.client_ are released under the [Academic Public License](https://github.com/thinkbeforecoding/eskv/blob/main/LICENSE.md).