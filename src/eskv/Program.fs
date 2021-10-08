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
open Microsoft.Extensions.FileProviders
open System.Net.Http
open Argu
open AspNetExtensions
open Streams

type Cmd =
    | Dev
    | Parcel of string
    | EndPoint of string
    with
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Dev -> "specify dev mode."
            | Parcel _ -> "parcel dev server url. default is http://localhost:1234"
            | EndPoint _ -> "kv http listener endpoint. default is http://localhost:5000"


let cmd = ArgumentParser.Create<Cmd>().ParseCommandLine()
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
let data = ConcurrentDictionary<Key, Entry>()

//type EventData =
//    { Type: string 
//      Data: byte[]
//      ContentType: string }

//let streams = ConcurrentDictionary<string, ResizeArray<EventData>>()

//type AllStreamData =
//    { Stream: string
//      Position: int
//      Event: EventData
//    }
//let allStream = ResizeArray<EventData>()

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


if isDevMode then
    app.MapGet("/content/{**path}", fun ctx ->
            let path = ctx.Request.RouteValues.["path"] |> string
            ctx.Response.ProxyGetAsync(UriBuilder(parcelUrl,Path=path).Uri)
    ) |> ignore


app.MapGet("/kv/",  Func<_, _>( fun _ ->
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
                if data.TryUpdate(key,{ Etag = newEtag ; Bytes = bytes; ContentType = contentType }, { Etag = etags.[0]; Bytes = null; ContentType = null }) then
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
            if data.TryUpdate(key,{ Etag = "" ; Bytes = null; ContentType = null },{ Etag = etags.[0]; Bytes = null; ContentType = null }) then
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
                  |> Seq.filter (fun kv ->  kv.Value.Etag.Length <> 0 )
                  |> Seq.map (fun (KeyValue(k,v)) -> k, (decode v.Bytes v.ContentType, v.Etag))
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


let renderSlice (response: HttpResponse) start (slice: ReadOnlyMemory<EventRecord>) =
    task {
        response.Headers.Add("ESKV-Expected-Version", string (start+slice.Length-1))
        response.Headers.Add("ESKV-Next-Event-Number", string (start+slice.Length))
        if slice.Length = 0 then
            response.StatusCode <- 204
        else
            let content = new MultipartContent()
            response.ContentType <- string content.Headers.ContentType
            
            do
                let span = slice.Span
                for i in 0.. span.Length-1 do
                    let e = span.[i]
                    let part = new ByteArrayContent(e.Event.Data)
                    part.Headers.ContentType <- Headers.MediaTypeHeaderValue.Parse(e.Event.ContentType)
                    part.Headers.Add("ESKV-Event-Type", e.Event.Type)
                    part.Headers.Add("ESKV-Event-Number", string e.EventNumber)
                    content.Add(part)
                
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
                        do! renderSlice ctx.Response (expectedVersion+1) slice
                    | ValueNone -> ()
                        



    } :> Task
) |> ignore


app.MapGet("/es/{stream}/{start:int}/{count:int}", fun ctx ->
    task {
        let streamId = ctx.Request.RouteValues.["stream"] |> string
        let start = ctx.Request.RouteValues.["start"] |> string |> int
        let count = ctx.Request.RouteValues.["count"] |> string |> int

        match! Streams.readStreamAsync streamId start count with
        | ValueSome slice ->
            do! renderSlice ctx.Response start slice
            
        | ValueNone ->
            ctx.Response.StatusCode <- 404
    
        ()
    } :> Task

) |> ignore

app.Run(endpoint);
