namespace eskv

open FSharp.Control
open System
open System.Net.Http

open System.Net
open System.Threading.Tasks
open System.Linq

type LoadResult =
    { KeyExists: bool
      Value: string
      ETag: string }


[<Struct>]
type EventData = 
    { EventType: string
      Data: string }

[<Struct>]
type EventRecord = 
    { EventType: string
      EventNumber: int
      Data: string 
      OriginStream: string
      OriginEventNumber: int }






type StreamState =
    | NoStream = 0
    | StreamExists = 1

type Slice =
    { State: StreamState
      Events: EventRecord[]
      ExpectedVersion: int
      NextEventNumber: int }

type StreamsSlice =
    { State: StreamState
      Streams: string[]
      ExpectedVersion: int
      NextEventNumber: int }

type AppendResult =
    { Success: bool
      ExpectedVersion: int 
      NextEventNumber: int }

type AppendOrReadResult =
    { Success: bool
      NewEvents: EventRecord[]
      ExpectedVersion: int 
      NextEventNumber: int }

module private Http =

    let (|Success|Failure|) (statusCode: HttpStatusCode) =
        if int statusCode >= 200 && int statusCode <= 299 then
            Success
        else
            Failure

module ExpectedVersion =
    let Start = 0
    let NoStream = -1
    let Any = -2

type Client(uri: Uri) =
    let kv = Uri(uri, "kv/")
    let es = Uri(uri, "es/")
    [<Literal>]
    let defaultContainer = "default"

    let raiseHttpException(response: HttpResponseMessage) =
        raise (HttpRequestException(response.ReasonPhrase, null, Nullable response.StatusCode))

    let readEvents (response: HttpResponseMessage) = 
        task {
                if response.StatusCode = HttpStatusCode.NoContent then
                    return [||]
                else
                    let mutable boundary = ""
                    for p in response.Content.Headers.ContentType.Parameters do
                        if p.Name = "boundary" then
                            boundary <- p.Value.Trim('"')
                    
                    let streamId = response.Headers.GetValues("ESKV-Stream").First()

                    use! stream = response.Content.ReadAsStreamAsync()
                    let reader = Microsoft.AspNetCore.WebUtilities.MultipartReader(boundary, stream)
                    

                    let mutable quit = false
                    let events = ResizeArray()
                    while not quit do
                        
                        let! section = reader.ReadNextSectionAsync()
                        
                        if isNull section then
                            quit <- true
                        else
                             
                            let! eventType, data = 
                                task {
                                match section.Headers.TryGetValue("ESKV-Event-Type") with
                                | true, eth -> 
                                    let eventType = eth.ToString()


                                    return!
                                        task {
                                            use streamReader = new IO.StreamReader(section.Body)
                                            let! data = streamReader.ReadToEndAsync()
                                            return eventType, data 
                                        }
                                    
                                | false, _ ->
                                    return "$>", null
                                }
                            let eventNumber = section.Headers.["ESKV-Event-Number"].ToString() |> int
                            let originEventNumber = 
                                match section.Headers.TryGetValue("ESKV-Origin-Event-Number") with
                                | true, h -> h.ToString() |> int
                                | false, _ -> eventNumber
                            let originStream =
                                match section.Headers.TryGetValue("ESKV-Origin-Stream") with
                                | true, h -> h.ToString()
                                | false, _ -> streamId
                            events.Add({EventType = eventType
                                        EventNumber = eventNumber
                                        Data = data
                                        OriginEventNumber = originEventNumber
                                        OriginStream = originStream
                                        })
                                
                            

                    return events.ToArray()
        }


    new() = Client(Uri "http://localhost:5000")

    
    member _.TryLoadAsync(container: string, key: string) =
        task {
            use client = new HttpClient()
            let! response  = client.GetAsync(Uri(kv, container + "/" + key))
            match response.StatusCode with
            | HttpStatusCode.NotFound ->
                return { KeyExists = false; Value = null; ETag = null }
            | Http.Success ->
                let etag = 
                    match response.Headers.ETag with
                    | null -> null
                    | t -> t.Tag
                let! data = response.Content.ReadAsStringAsync()


                return { KeyExists = true; Value = data; ETag = etag}
            | _ ->
                return raiseHttpException(response)

        } 

       
    member this.TryLoadAsync(key: string) =
        this.TryLoadAsync(defaultContainer, key)

    member this.TryLoad(container, key) = this.TryLoadAsync(container, key).Result

    member this.TryLoad(key) = this.TryLoadAsync(key).Result

    member _.TrySaveAsync(container: string, key: string, value: string, etag: string) =
        task {
            use client = new HttpClient()
            if not (isNull etag) then
                client.DefaultRequestHeaders.IfMatch.Add(Headers.EntityTagHeaderValue.Parse(etag))
            else
                client.DefaultRequestHeaders.IfNoneMatch.Add(Headers.EntityTagHeaderValue.Any)

            let! response = client.PutAsync(Uri(Uri(kv,container),key), new StringContent(value))
            
            match response.StatusCode with
            | HttpStatusCode.Conflict ->
                return null
            | Http.Success ->
                return response.Headers.ETag.Tag
            | _ ->
                 return raiseHttpException(response)
                
        }

    member this.TrySaveAsync(key: string, value: string, etag: string) =
        this.TrySaveAsync(defaultContainer, key, value, etag)

    member this.TrySave(container, key, value, etag) = this.TrySaveAsync(container, key, value, etag).Result

    member this.TrySave(key, value, etag) = this.TrySaveAsync(key, value, etag).Result



    member _.SaveAsync(container: string, key: string, value: string) =
        task {
            use client = new HttpClient()

            let! response = client.PutAsync(Uri(kv, container + "/" + key), new StringContent(value))
            if not response.IsSuccessStatusCode then
                return raiseHttpException(response)
        } :> Task

    member this.SaveAsync(key: string, value: string) =
        this.SaveAsync(defaultContainer, key, value)
    
    member this.Save(container, key, value) = this.SaveAsync(container, key, value).Wait()

    member this.Save(key, value) = this.SaveAsync(key, value).Wait()


    member _.DeleteKeyAsync(container: string, key: string) =
      task {
          use client = new HttpClient()

          let! response = client.DeleteAsync(Uri(kv, container + "/" + key))

          if not response.IsSuccessStatusCode
                && response.StatusCode <> HttpStatusCode.NotFound then
              return raiseHttpException(response)
      } :> Task

    member this.DeleteKeyAsync(key: string) =
        this.DeleteKeyAsync(defaultContainer, key)

    member this.DeleteKey(container, key: string) =
        this.DeleteKeyAsync(container, key).Wait()

    member this.DeleteKey(key: string) =
        this.DeleteKeyAsync(key).Wait()

    member _.DeleteContainerAsync(container: string) =
      task {
          use client = new HttpClient()

          let! response = client.DeleteAsync(Uri(kv, container))
          if not response.IsSuccessStatusCode
                && response.StatusCode <> HttpStatusCode.NotFound then
              return raiseHttpException(response)
      } :> Task

    member this.DeleteContainer(container: string) =
        this.DeleteContainerAsync(container).Wait()

    member _.GetKeysAsync(container: string) =
        task {
          use client = new HttpClient()

          let! response = client.GetStringAsync(Uri(kv, container))
          return response.Split("\n",StringSplitOptions.RemoveEmptyEntries)
        }

    member this.GetKeysAsync() =
        this.GetKeysAsync(defaultContainer)

    member this.GetKeys(container) =
        this.GetKeysAsync(container).Result

    member this.GetKeys() =
        this.GetKeysAsync().Result

    member _.GetContainersAsync() =
        task {
          use client = new HttpClient()

          let! response = client.GetStringAsync(kv)
          return response.Split("\n",StringSplitOptions.RemoveEmptyEntries)
        }

    member this.GetContainers() =
        this.GetContainersAsync().Result

    member _.AppendAsync(stream: string, events: EventData seq) =
        task {
            match events.TryGetNonEnumeratedCount() with
            | true, 0 -> ()
            | _ ->
                use client = new HttpClient()
                
                let content = new MultipartContent()
                let mutable hasParts = false 
                for e in events do
                    hasParts <- true
                    let part = new StringContent(e.Data)
                    part.Headers.Add("ESKV-Event-Type", e.EventType)
                    part.Headers.ContentType.MediaType <- "text/plain"

                    content.Add(part)


                if hasParts then
                    let! response = client.PostAsync(Uri(es,stream), content)
                    if not response.IsSuccessStatusCode then
                        return raiseHttpException(response)
            
        } :> Task


    member this.Append(stream: string, events) = this.AppendAsync(stream, events).Wait()

    member _.TryAppendAsync(stream: string, expectedVersion: int, events: EventData seq) =
        task {
            use client = new HttpClient()
             
            let content = new MultipartContent()
            content.Headers.Add("ESKV-Expected-Version", string expectedVersion)
            for e in events do
                let part = new StringContent(e.Data)
                part.Headers.Add("ESKV-Event-Type", e.EventType)
                part.Headers.ContentType.MediaType <- "text/plain"

                content.Add(part)


            let! response = client.PostAsync(Uri(es,stream), content)
            if response.IsSuccessStatusCode then
                let nextExpectedVersion = response.Headers.GetValues("ESKV-Expected-Version").First() |> int
                let nextEventNumber = response.Headers.GetValues("ESKV-Next-Event-Number").First() |> int
                return { Success = true; ExpectedVersion = nextExpectedVersion; NextEventNumber = nextEventNumber }
            elif response.StatusCode = HttpStatusCode.Conflict then
                return { Success = false; ExpectedVersion = 0; NextEventNumber = 0 }
            else
                return raiseHttpException(response)
             
         } 


     member this.TryAppend(stream: string, expectedVersion, events) = this.TryAppendAsync(stream, expectedVersion, events).Result


    member _.TryAppendOrReadAsync(stream: string, expectedVersion: int, events: EventData seq) =
        task {
            use client = new HttpClient()
             
            let content = new MultipartContent()
            content.Headers.Add("ESKV-Expected-Version", string expectedVersion)
            content.Headers.Add("ESKV-Return-New-Events", "")
            for e in events do
                let part = new StringContent(e.Data)
                part.Headers.Add("ESKV-Event-Type", e.EventType)
                part.Headers.ContentType.MediaType <- "text/plain"
                content.Add(part)


            let! response = client.PostAsync(Uri(es,stream), content)
            if response.IsSuccessStatusCode then
                let nextExpectedVersion = response.Headers.GetValues("ESKV-Expected-Version").First() |> int
                let nextEventNumber = response.Headers.GetValues("ESKV-Next-Event-Number").First() |> int
                return { Success = true; NewEvents = Array.Empty(); ExpectedVersion = nextExpectedVersion; NextEventNumber = nextEventNumber}
            elif response.StatusCode = HttpStatusCode.Conflict then
                let! newEvents = readEvents response 
                let nextExpectedVersion = response.Headers.GetValues("ESKV-Expected-Version").First() |> int
                let nextEventNumber = response.Headers.GetValues("ESKV-Next-Event-Number").First() |> int
                return { Success = false; NewEvents = newEvents; ExpectedVersion = nextExpectedVersion; NextEventNumber = nextEventNumber }
            else
                return raiseHttpException(response)
             
         } 


     member this.TryAppendOrRead(stream: string, expectedVersion, events) = this.TryAppendOrReadAsync(stream, expectedVersion, events).Result

    member _.ReadStreamForwardAsync(stream: string, start: int, count: int, linkOnly: bool) =
        task {
            use client = new HttpClient()

            let request = new HttpRequestMessage(HttpMethod.Get, Uri(es,$"{stream}/{start}/{count}"))
            if linkOnly then
                request.Headers.Add("ESKV-Link-Only","")
            let! response = client.SendAsync(request)
            if response.IsSuccessStatusCode then
                let nextExpectedVersion = response.Headers.GetValues("ESKV-Expected-Version").First() |> int
                let nextEventNumber = response.Headers.GetValues("ESKV-Next-Event-Number").First() |> int
                let! events = readEvents response
                return { State = StreamState.StreamExists
                         Events = events
                         ExpectedVersion = nextExpectedVersion
                         NextEventNumber = nextEventNumber
                         }
            elif response.StatusCode = HttpStatusCode.NotFound then 
                return { State = StreamState.NoStream
                         Events = [||]
                         ExpectedVersion = -1
                         NextEventNumber = 0 }
            else
                return failwithf "%s" response.ReasonPhrase
        }

    member this.ReadStreamForward(stream: string, start: int, count: int, linkOnly: bool) =
        this.ReadStreamForwardAsync(stream,start,count, linkOnly).Result

    member this.ReadStreamForwardAsync(stream: string, start: int, count: int) =
        this.ReadStreamForwardAsync(stream,start,count, false)

    member this.ReadStreamForward(stream: string, start: int, count: int) =
        this.ReadStreamForwardAsync(stream,start,count).Result

    member this.GetStreamsAsync(start: int, count: int) =
        task {
            let! slice = this.ReadStreamForwardAsync("$streams", start, count, true)
        
            return {
                State = slice.State
                Streams = slice.Events |> Array.map (fun e -> e.OriginStream)
                ExpectedVersion = slice.ExpectedVersion
                NextEventNumber = slice.NextEventNumber
            }
        }

    member this.GetStreams(start: int, count: int) =
        this.GetStreamsAsync(start, count).Result
