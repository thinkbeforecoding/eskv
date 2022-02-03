open System
open Microsoft.AspNetCore.Builder
open System.Collections.Concurrent
open System.Threading.Tasks
open System.Buffers
open System.Security.Cryptography
open Microsoft.AspNetCore.Http
open System.Net.WebSockets
open System.Threading
open Thoth.Json.Net
open Shared
open Microsoft.AspNetCore.WebUtilities
open Microsoft.Net.Http.Headers
open Microsoft.Extensions.FileProviders
open System.Net.Http
open Argu
open AspNetExtensions
open Streams

type Cmd =
    | EndPoint of string
    | Dev
    | Parcel of string
    with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Dev -> "specify dev mode."
            | Parcel _ -> "parcel dev server url. default is http://localhost:1234"
            | EndPoint _ -> "eskv http listener endpoint. default is http://localhost:5000"

let parser = ArgumentParser.Create<Cmd>()
let cmd =
    try
        parser.ParseCommandLine()
    with
    | ex ->
        printfn "%s" ex.Message
        exit 0

let isDevMode = cmd.Contains Dev
let parcelUrl = cmd.TryGetResult Parcel |> Option.defaultValue "http://localhost:1234"
let endpoint = cmd.TryGetResult EndPoint |> Option.defaultValue "http://localhost:5000"

module Etag =
    let ofBytes (data: byte[]) =
        let bytes = MD5.HashData(data) 
        $"\"{Convert.ToBase64String(bytes)}\""

[<Struct;CustomEquality; NoComparison>]
type Entry =
    { Etag: ETag 
      Bytes: byte[]
      ContentType: string }
    interface IEquatable<Entry> with
        member this.Equals(other) =
            this.Etag = other.Etag
        

let cts = new CancellationTokenSource()
let data = ConcurrentDictionary<string, ConcurrentDictionary<Key, Entry>>()

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

let builder = WebApplication.CreateBuilder()

if not isDevMode then
    builder.Environment.WebRootFileProvider <- new EmbeddedFileProvider(Reflection.Assembly.GetExecutingAssembly(),"eskv.wwwroot")

let app = builder.Build()
app.UseWebSockets() |> ignore
if not isDevMode then
    app.UseStaticFiles().UseDefaultFiles() |> ignore

app.MapGet("/", fun  (ctx: HttpContext) ->
        if isDevMode && ctx.WebSockets.IsWebSocketRequest then
            ctx.ProxyWebSocketAsync( UriBuilder(parcelUrl,Scheme="ws://").Uri, cts.Token)
        else
            ctx.Response.Headers.ContentType <- "text/html"
            ctx.Response.SendWebFileAsync("index.html")
        )
        |> ignore

app.MapGet("/favicon.ico", fun  (ctx: HttpContext) ->
    ctx.Response.Headers.ContentType <- "image/png"
    ctx.Response.SendWebFileAsync("favicon.ico")
    )
    |> ignore


if isDevMode then
    app.MapGet("/content/{**path}", fun ctx ->
            let path = ctx.Request.RouteValues.["path"] |> string
            ctx.Response.ProxyGetAsync(UriBuilder(parcelUrl,Path=path).Uri)
    ) |> ignore



app.MapGet("/kv/",  fun ctx ->
    task {

        ctx.Response.ContentType <- "text/plain"
        for key in data.Keys do
            
            let len = Text.Encoding.UTF8.GetByteCount(key)
            let data = ctx.Response.BodyWriter.GetSpan(len+1)
            let written = Text.Encoding.UTF8.GetBytes(key.AsSpan(), data)
            data.[written] <- 10uy

            ctx.Response.BodyWriter.Advance(written+1)
        do! ctx.Response.BodyWriter.CompleteAsync()

    
    } :> Task
    
) |> ignore

app.MapGet("/kv/{container}",  fun ctx ->
    task {

        let containerKey = ctx.Request.RouteValues.["container"] |> string
        ctx.Response.ContentType <- "text/plain"
        match data.TryGetValue(containerKey) with
        | true, container ->
            for key in container.Keys do
                
                let len = Text.Encoding.UTF8.GetByteCount(key)
                let data = ctx.Response.BodyWriter.GetSpan(len+1)
                let written = Text.Encoding.UTF8.GetBytes(key.AsSpan(), data)
                data.[written] <- 10uy

                ctx.Response.BodyWriter.Advance(written+1)
            do! ctx.Response.BodyWriter.CompleteAsync()
        | false, _ ->
            ctx.Response.StatusCode <- 404
    } :> Task
    
) |> ignore

app.MapGet("/kv/{container}/{key}", fun ctx -> 
    task {
        let key = ctx.Request.RouteValues.["key"] |> string
        let containerKey = ctx.Request.RouteValues.["container"] |> string

        let container = data.GetOrAdd(containerKey, fun _ -> ConcurrentDictionary())

        match container.TryGetValue(key) with
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



app.MapPut("/kv/{container}/{key}", fun ctx ->
    task {
        let key = ctx.Request.RouteValues.["key"] |> string
        let containerKey = ctx.Request.RouteValues.["container"] |> string

        let contentType = ctx.Request.ContentType
        let length = ctx.Request.Headers.ContentLength
        let! result = 
            if length.HasValue then
                ctx.Request.BodyReader.ReadAtLeastAsync(int length.Value)
            else
                ctx.Request.BodyReader.ReadAsync()


        let bytes = result.Buffer.ToArray()

        ctx.Request.BodyReader.AdvanceTo(result.Buffer.End)
        
        let newEtag = Etag.ofBytes bytes

        let container = data.GetOrAdd(containerKey, fun _ -> ConcurrentDictionary())        

        if ctx.Request.Headers.IfNoneMatch.Count = 1 && ctx.Request.Headers.IfNoneMatch.[0] = "*" then
            // the data should not already exist:
            if container.TryAdd(key, { Etag = newEtag; Bytes = bytes; ContentType = contentType} ) then
                ctx.Response.StatusCode <- 204
                ctx.Response.Headers.ETag <-  newEtag
                do! publish (Shared.KeyChanged(containerKey, key, (Text.Encoding.UTF8.GetString(bytes), newEtag)))
            else
                ctx.Response.StatusCode <- 409 // conflict
        else 
            let etags = ctx.Request.Headers.IfMatch
            if etags.Count = 1 then
                if container.TryUpdate(key,{ Etag = newEtag ; Bytes = bytes; ContentType = contentType }, { Etag = etags.[0]; Bytes = null; ContentType = null }) then
                    ctx.Response.StatusCode <- 204
                    ctx.Response.Headers.ETag <-  newEtag
                    do! publish (Shared.KeyChanged(containerKey, key, (Text.Encoding.UTF8.GetString(bytes), newEtag)))
                else
                    ctx.Response.StatusCode <- 409 // conflict


            else
                // save data without etag check
                container.[key] <- { Etag = newEtag ; Bytes = bytes; ContentType = contentType }
                ctx.Response.StatusCode <- 204
                ctx.Response.Headers.ETag <-  newEtag
                do! publish (Shared.KeyChanged(containerKey, key, (Text.Encoding.UTF8.GetString(bytes), newEtag)))


            } :> Task
) |> ignore

app.MapDelete("/kv/{container}/{key}", fun ctx ->
    task {
        let key = ctx.Request.RouteValues.["key"] |> string
        let containerKey = ctx.Request.RouteValues.["container"] |> string

        let etags = ctx.Request.Headers.IfMatch
        match data.TryGetValue(containerKey) with
        | true, container ->
            if etags.Count = 1 then
                if container.TryUpdate(key,{ Etag = "" ; Bytes = null; ContentType = null },{ Etag = etags.[0]; Bytes = null; ContentType = null }) then
                    data.TryRemove(key) |> ignore
                    ctx.Response.StatusCode <- 204
                    do! publish (Shared.KeyDeleted(containerKey, key))
                else
                    ctx.Response.StatusCode <- 409 // conflict


            else
                // save data without etag check
                container.TryRemove(key)|> ignore
                ctx.Response.StatusCode <- 204
                do! publish (Shared.KeyDeleted(containerKey, key))
        | false, _ ->
            ctx.Response.StatusCode <- 204


            } :> Task
) |> ignore

app.MapDelete("/kv/{container}", fun ctx ->
    task {
        let containerKey = ctx.Request.RouteValues.["container"] |> string

        match data.TryRemove(containerKey) with
        | true, container ->
            
                do! publish (Shared.ContainerDeleted(containerKey))
        | false, _ -> ()
        ctx.Response.StatusCode <- 204


            } :> Task
) |> ignore


let decode (bytes: byte[]) (contentType: string) =
    match System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType) with
    | true, ct ->
        if ct.MediaType.StartsWith("text/") then
            Text.Encoding.UTF8.GetString(bytes)
        else
            match contentType with
            | "application/json"
            | "application/xml" -> 
                Text.Encoding.UTF8.GetString(bytes)
            | _ -> null
        

    | _ -> null

app.MapGet("/ws", fun ctx ->
    task {
           if ctx.WebSockets.IsWebSocketRequest then
              let! webSocket = ctx.WebSockets.AcceptWebSocketAsync()
              
              sessions.TryAdd(webSocket, obj()) |> ignore

              let msg =
                  data
                  |> Seq.collect(fun kv -> kv.Value |> Seq.map( fun v -> kv.Key, v.Key, v.Value))
                  |> Seq.filter (fun (_,_,v) ->  v.Etag.Length <> 0 )
                  |> Seq.map (fun (container,key,v) -> container,key, (decode v.Bytes v.ContentType, v.Etag))
                  |> Seq.toList
                  |> Shared.KeysLoaded

           

              do! send webSocket msg

              let! events = Streams.readAllAsync 0 Int32.MaxValue
              let msg =
                let data = Array.zeroCreate events.Length
                let s = events.Span
                for i in 0 .. s.Length-1 do
                    let event = s.[i]
                    data.[i] <- 
                     { StreamId = event.StreamId
                       EventNumber = event.EventNumber
                       EventType = event.Event.Type
                       EventData = decode event.Event.Data event.Event.ContentType}
                data
              
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


let renderSliceContent includeLink (slice: ReadOnlyMemory<Event>) = 
    let content = new MultipartContent()
    let span = slice.Span
    for i in 0.. span.Length-1 do
        let e = span.[i]

        match e with
        | Record record -> 
            let part = new ByteArrayContent(record.Event.Data)
            part.Headers.ContentType <- Headers.MediaTypeHeaderValue.Parse(record.Event.ContentType)
            part.Headers.Add("ESKV-Event-Type", record.Event.Type)
            part.Headers.Add("ESKV-Event-Number", string record.EventNumber)

            content.Add(part)
        | Link l -> 
            
            let part =
            
                if includeLink then
                    new ByteArrayContent(l.OriginEvent.Event.Data)
                else
                    new ByteArrayContent([||])
            part.Headers.ContentType <- Headers.MediaTypeHeaderValue.Parse(l.OriginEvent.Event.ContentType)
            part.Headers.Add("ESKV-Event-Number", string l.EventNumber)
            part.Headers.Add("ESKV-Origin-Stream", string l.OriginEvent.StreamId)
            if includeLink then
                part.Headers.Add("ESKV-Origin-Event-Number", string l.OriginEvent.EventNumber)
                part.Headers.Add("ESKV-Event-Type", l.OriginEvent.Event.Type)

            content.Add(part)

    content

let renderSlice (response: HttpResponse) (streamId: string) includeLink start (slice: ReadOnlyMemory<Event>) =
    task {
        response.Headers.Add("ESKV-Stream", streamId)
        response.Headers.Add("ESKV-Expected-Version", string (start+slice.Length-1))
        response.Headers.Add("ESKV-Next-Event-Number", string (start+slice.Length))
        if slice.Length = 0 then
            response.StatusCode <- 204
        else
            
            let content = renderSliceContent includeLink slice
            
            response.ContentType <- string content.Headers.ContentType
                
            do! content.CopyToAsync(response.Body)
    }

app.MapPost("/es/{stream}", fun ctx ->
    task {
        let streamId = ctx.Request.RouteValues.["stream"] |> string
        let expectedVersion = 
            let h = ctx.Request.Headers.["ESKV-Expected-Version"]
            if h.Count  = 0 then
                ValueSome -2
            else
                let v = int h.[0]
                if v < -2 then
                    ValueNone  
                else
                    ValueSome v

        let returnNewEvents = ctx.Request.Headers.ContainsKey("ESKV-Return-New-Events")



        match expectedVersion with
        | ValueNone ->
            ctx.Response.StatusCode <- 400
        | ValueSome expectedVersion ->
            let boundary =HeaderUtilities.RemoveQuotes(
                MediaTypeHeaderValue.Parse(ctx.Request.ContentType).Boundary).Value

            let events = ResizeArray()
            if not (isNull boundary) then
                let reader = MultipartReader(boundary,ctx.Request.Body)

                let mutable quit = false
                while not quit do
                    let! section = reader.ReadNextSectionAsync() 
                    if isNull section then
                        quit <- true
                    else
                        match section.Headers.TryGetValue("ESKV-Event-Type") with
                        | true, et ->
                            let eventType = et.[0]

                            let! data = 
                                task { 
                                    use memory = new IO.MemoryStream()
                                    do! section.Body.CopyToAsync(memory)
                                    return memory.ToArray()
                                }
                            events.Add { Type = eventType; Data = data; ContentType = section.ContentType }
                        | false,_ ->
                            quit <- true
                        

            let! nextExpectedVersion = Streams.appendAsync streamId (events.ToArray()) expectedVersion

            match nextExpectedVersion with
            | ValueSome(nextExpectedVersion, records) ->




                do! publish (StreamUpdated (records |> Array.map(fun e -> { StreamId = e.StreamId; EventNumber = e.EventNumber; EventType = e.Event.Type; EventData = decode e.Event.Data e.Event.ContentType})
                ))

                ctx.Response.StatusCode <- 204
                ctx.Response.Headers.Add("ESKV-Expected-Version", string nextExpectedVersion)
                ctx.Response.Headers.Add("ESKV-Next-Event-Number", string (nextExpectedVersion+1))

            | ValueNone ->
                ctx.Response.StatusCode <- 409
                if returnNewEvents then
                    match! Streams.readStreamAsync streamId (expectedVersion+1) (Int32.MaxValue) with
                    | ValueSome slice ->
                        do! renderSlice ctx.Response streamId true (expectedVersion+1) slice
                    | ValueNone -> ()
                        



    } :> Task
) |> ignore


app.MapGet("/es/{stream}/{start:int}/{count:int}", fun ctx ->
    task {
        let streamId = ctx.Request.RouteValues.["stream"] |> string
        let start = ctx.Request.RouteValues.["start"] |> string |> int
        let count = ctx.Request.RouteValues.["count"] |> string |> int

        let includeLink =
            not (ctx.Request.Headers.ContainsKey("ESKV-Link-Only"))

        match! Streams.readStreamAsync streamId start count with
        | ValueSome slice ->
            do! renderSlice ctx.Response streamId includeLink start slice
            
        | ValueNone ->
            ctx.Response.StatusCode <- 404
    
        ()
    } :> Task

) |> ignore



app.Run(endpoint);
