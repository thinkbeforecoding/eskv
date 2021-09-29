open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Hosting
open System.Collections.Concurrent
open System.Threading.Tasks
open System.Buffers
open System.Security.Cryptography
open Microsoft.Extensions.Primitives
open Microsoft.AspNetCore.Http
open System.Net.WebSockets
open System.Threading
open Thoth.Json.Net
open Shared
open Microsoft.AspNetCore.WebUtilities
open Microsoft.Net.Http.Headers

module Etag =
    let ofBytes (data: byte[]) =
        let bytes = MD5.HashData(data) 
        $"\"{Convert.ToBase64String(bytes)}\""

[<Struct;CustomEquality; NoComparison>]
type Entry =
    { Etag: ETag 
      Bytes: byte[]
      ContentType: StringValues }
    interface IEquatable<Entry> with
        member this.Equals(other) =
            this.Etag = other.Etag
        
type Microsoft.AspNetCore.Http.HttpContext with
    member this.GetService<'t>() = this.RequestServices.GetService(typeof<'t>) :?> 't

let cts = new CancellationTokenSource()
let data = ConcurrentDictionary<Key, Entry>()

type EventData =
    { Type: string 
      Data: string }

let streams = ConcurrentDictionary<string, ConcurrentQueue<EventData>>()

let sessions = ConcurrentDictionary<WebSocket,obj>()



let publish msg =
    task {
    let payload = Encode.Auto.toString(0,msg) |> Text.Encoding.UTF8.GetBytes
    
    for KeyValue(ws,_) in sessions do
        do! ws.SendAsync(payload.AsMemory(),WebSocketMessageType.Text,true,cts.Token)
    }
        
let send (ws: WebSocket) msg =
    task {
      let payload = Encode.Auto.toString(0,msg) |> Text.Encoding.UTF8.GetBytes
      
      do! ws.SendAsync(payload.AsMemory(),WebSocketMessageType.Text,true,cts.Token)
      }

let builder = WebApplication.CreateBuilder();

let app = builder.Build()
app.UseWebSockets() |> ignore

let index = """<!DOCTYPE html>
<html>
<head>
    <title>kv</title>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <link rel="stylesheet" href="content/style.css">
    <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/5.14.0/css/all.min.css">
    <script type="application/javascript" src="content/App.fs.js"></script>
</head>
<body>
    <div id="elmish-app">
    </div>
</body>

</html>
"""
    //<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bulma@0.9.3/css/bulma.min.css">
    //<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/@creativebulma/bulma-tooltip@1.2.0/dist/bulma-tooltip.min.css">
 
app.MapGet("/", fun ctx ->
    task {
        if ctx.WebSockets.IsWebSocketRequest then
            let! ws = ctx.WebSockets.AcceptWebSocketAsync()
            let upstream = new ClientWebSocket()
            do! upstream.ConnectAsync(Uri "ws://localhost:1234", cts.Token)
            
            let cp1 = task {
                use mem = MemoryPool.Shared.Rent(4096)
                while true do
                    let! result = upstream.ReceiveAsync(mem.Memory,cts.Token)
                    do! ws.SendAsync(mem.Memory.Slice(0,result.Count), result.MessageType, result.EndOfMessage, cts.Token)
                    
            }

            let cp2 = task {
                use mem = MemoryPool.Shared.Rent(4096)
                while true do
                    let! result = ws.ReceiveAsync(mem.Memory,cts.Token)
                    do! upstream.SendAsync(mem.Memory.Slice(0,result.Count), result.MessageType, result.EndOfMessage, cts.Token)
                    
            }

            do! Task.WhenAll [|cp1 :> Task;cp2|]
        else

            ctx.Response.Headers.ContentType <- "text/html"
            do! ctx.Response.WriteAsync(index)
        } :> Task) |> ignore

app.MapGet("/content/App.fs.js", fun ctx ->
    task {
        ctx.Response.Headers.ContentType <- "application/javascript"
        use stream = IO.File.Open(@"C:\dev\kv\src\kv.ui\dist\App.fs.js", IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite)
        do! stream.CopyToAsync(ctx.Response.Body)
    } :> Task)  |> ignore

app.MapGet("/content/style.css", fun ctx ->
    task {
        ctx.Response.Headers.ContentType <- "text/css"
        use stream = IO.File.Open(@"C:\dev\kv\src\kv.ui\dist\style.css", IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite)
        do! stream.CopyToAsync(ctx.Response.Body)
    } :> Task)  |> ignore
app.MapGet("/kv/",  Func<HttpContext, obj>( fun ctx ->
                [| for k in data.Keys -> k |] )) |> ignore


app.MapGet("/kv/{key}", fun ctx -> 
    task {
        let key = ctx.Request.RouteValues.["key"] |> string
        match data.TryGetValue(key) with
        | false, _ -> 
                ctx.Response.StatusCode <- 404
        | true, entry ->
                ctx.Response.StatusCode <- 200
                ctx.Response.ContentType <- entry.ContentType
                ctx.Response.Headers.ETag <- entry.Etag
                ctx.Response.Headers.ContentLength <- entry.Bytes.LongLength

                let! _ =  ctx.Response.BodyWriter.WriteAsync(entry.Bytes)
                do! ctx.Response.BodyWriter.CompleteAsync()
    } :> Task
) |> ignore



app.MapPut("/kv/{key}", fun ctx ->
    task {
        let key = ctx.Request.RouteValues.["key"] |> string

        let contentType = ctx.Request.Headers.ContentType
        let length = ctx.Request.Headers.ContentLength
        let! result = 
            if length.HasValue then
                ctx.Request.BodyReader.ReadAtLeastAsync(int length.Value)
            else
                ctx.Request.BodyReader.ReadAsync()


        let bytes = result.Buffer.ToArray()

        ctx.Request.BodyReader.AdvanceTo(result.Buffer.End)
        
        let newEtag = Etag.ofBytes bytes
        

        if ctx.Request.Headers.IfNoneMatch.Count = 1 && ctx.Request.Headers.IfNoneMatch.[0] = "*" then
            // the data should not already exist:
            if data.TryAdd(key, { Etag = newEtag; Bytes = bytes; ContentType = contentType} ) then
                ctx.Response.StatusCode <- 204
                ctx.Response.Headers.ETag <-  newEtag
                do! publish (Shared.KeyChanged(key, (Text.Encoding.UTF8.GetString(bytes), newEtag)))
            else
                ctx.Response.StatusCode <- 409 // conflict
        else 
            let etags = ctx.Request.Headers.IfMatch
            if etags.Count = 1 then
                if data.TryUpdate(key,{ Etag = newEtag ; Bytes = bytes; ContentType = contentType }, { Etag = etags.[0]; Bytes = null; ContentType = StringValues.Empty }) then
                    ctx.Response.StatusCode <- 204
                    ctx.Response.Headers.ETag <-  newEtag
                    do! publish (Shared.KeyChanged(key, (Text.Encoding.UTF8.GetString(bytes), newEtag)))
                else
                    ctx.Response.StatusCode <- 409 // conflict


            else
                // save data without etag check
                data.[key] <- { Etag = newEtag ; Bytes = bytes; ContentType = contentType }
                ctx.Response.StatusCode <- 204
                ctx.Response.Headers.ETag <-  newEtag
                do! publish (Shared.KeyChanged(key, (Text.Encoding.UTF8.GetString(bytes), newEtag)))


            } :> Task
) |> ignore

app.MapDelete("/kv/{key}", fun ctx ->
    task {
        let key = ctx.Request.RouteValues.["key"] |> string

        let etags = ctx.Request.Headers.IfMatch
        if etags.Count = 1 then
            if data.TryUpdate(key,{ Etag = "" ; Bytes = null; ContentType = StringValues.Empty },{ Etag = etags.[0]; Bytes = null; ContentType = StringValues.Empty }) then
                data.TryRemove(key) |> ignore
                ctx.Response.StatusCode <- 204
                do! publish (Shared.KeyDeleted(key))
            else
                ctx.Response.StatusCode <- 409 // conflict


        else
            // save data without etag check
            data.TryRemove(key)|> ignore
            ctx.Response.StatusCode <- 204
            do! publish (Shared.KeyDeleted(key))


            } :> Task
) |> ignore
app.MapGet("/ws", fun ctx ->
    task {
        if ctx.Request.Path = PathString "/ws" then
           if ctx.WebSockets.IsWebSocketRequest then
                  let! webSocket = ctx.WebSockets.AcceptWebSocketAsync()
                  
                  sessions.TryAdd(webSocket, obj()) |> ignore
                  //do! webSocket.SendAsync("Hello"B.AsMemory(),WebSocketMessageType.Text, true, cts.Token )

                  let msg =
                      data
                      |> Seq.filter (fun kv ->  kv.Value.Etag.Length <> 0 )
                      |> Seq.map (fun (KeyValue(k,v)) -> k, (Text.Encoding.UTF8.GetString(v.Bytes), v.Etag))
                      |> Seq.toList
                      |> Shared.KeysLoaded

               

                  do! send webSocket msg

                  let msg = 
                   [ for KeyValue(streamId, events) in streams do
                         { Id = streamId; Events = [ for e in events -> e.Type,  e.Data ]} ]
                  
                  do! send webSocket (StreamLoaded msg)

                  let buffer = MemoryPool<byte>.Shared.Rent(4096)
                  let mutable closed = false
                  while not closed do

                    try
                      let! result = webSocket.ReceiveAsync(buffer.Memory, cts.Token)
                      ()
                    with
                    | _ -> 
                        sessions.TryRemove(webSocket) |> ignore
                        closed <- true
                  
              else
                  ctx.Response.StatusCode <- 400
    } :> Task
) |> ignore


app.MapPost("/es/{stream}", fun ctx ->
    task {
        let streamId = ctx.Request.RouteValues.["stream"] |> string

        let boundary =HeaderUtilities.RemoveQuotes(
            MediaTypeHeaderValue.Parse(ctx.Request.ContentType).Boundary).Value
        let reader = MultipartReader(boundary,ctx.Request.Body)

        let events = ResizeArray()
        let mutable quit = false
        while not quit do
            let! section = reader.ReadNextSectionAsync() 
            if isNull section then
                quit <- true
            else
                let eventType = section.Headers.["X-Event-Type"].[0]
                let! data = section.ReadAsStringAsync()
                events.Add { Type = eventType; Data = data }
                

        let stream = streams.GetOrAdd(streamId,fun _ -> ConcurrentQueue())
        lock stream (fun _ ->
            for e in events do
                stream.Enqueue(e)
        )

        do! publish (StreamUpdated { Id = streamId; Events = [ for e in events -> e.Type,  e.Data ]})

        ctx.Response.StatusCode <- 204
                


    } :> Task
) |> ignore

app.Run("http://*:5000");




