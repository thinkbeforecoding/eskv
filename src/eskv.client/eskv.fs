﻿namespace eskv

open FSharp.Control
open System
open System.Net.Http

open System.Net
open System.Threading.Tasks
open System.Linq

/// Contains result information for a <see cref="EskvClient.TryLoad"/> operation.
type LoadResult =
    { KeyExists: bool
      Value: string
      ETag: string }


/// Contains event data for a <see cref="EskvClient.Append"/> operation.
[<Struct>]
type EventData = 
    { EventType: string
      Data: string }

/// Contains event data returned by <see cref="EskvClient.ReadStreamForward"/>.
[<Struct>]
type EventRecord = 
    { EventType: string
      EventNumber: int
      Data: string 
      OriginStream: string
      OriginEventNumber: int }


/// Indicates whether a stream exists.
type StreamState =
    | NoStream = 0
    | StreamExists = 1

/// A slice of stream returned by <see cref="EskvClient.ReadStreamForward"/>.
type Slice =
    { State: StreamState
      Events: EventRecord[]
      ExpectedVersion: int
      NextEventNumber: int }

/// A slice of stream list returned by <see cref="EskvClient.GetStreams"/>.
type StreamsSlice =
    { State: StreamState
      Streams: string[]
      LastEventNumber: int
      NextEventNumber: int }

/// Contains result information for a <see cref="EskvClient.TryAppend"/> operation.
type AppendResult =
    { Success: bool
      ExpectedVersion: int 
      NextEventNumber: int }

/// Contains result information for a <see cref="EskvClient.TryAppendOrRead"/> operation.
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
    let [<Literal>] Start = 0
    let [<Literal>] NoStream = -1
    let [<Literal>] Any = -2

module EventNumber =
    let [<Literal>] Start = 0

module EventCount =
    let [<Literal>] All = Int32.MaxValue

/// <summary>Creates a new instance of <see cref="EskvClient" /> to use
/// eskv in-memory key/value and event store.</summary>
/// <param name="uri">The uri of the eskv server.</param>
type EskvClient(uri: Uri) =
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


    /// <summary>Creates a new instance of <see cref="EskvClient" /> to use
    /// eskv in-memory key/value and event store. Use the default
    /// http://localhost:5000 eskv server uri.</summary>
    new() = EskvClient(Uri "http://localhost:5000")

    /// <summary>Loads the value from the key in the specified container if it exists.</summary>
    /// <param name="container">The name of the container of the key.</param>
    /// <param name="key">The key used to store the value.</param>
    /// <returns>Returns a <see cref="LoadResult" /> indicating if the key exists, and its
    /// value if any.</returns>
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

       
    /// <summary>Loads the value from the key in the default container if it exists.</summary>
    /// <param name="key">The key used to store the value.</param>
    /// <returns>Returns a <see cref="LoadResult" /> indicating if the key exists, and its
    /// value if any.</returns>
    member this.TryLoadAsync(key: string) =
        this.TryLoadAsync(defaultContainer, key)

    /// <summary>Loads the value from the key in the specified container if it exists.</summary>
    /// <param name="container">The name of the container of the key.</param>
    /// <param name="key">The key used to store the value.</param>
    /// <returns>Returns a <see cref="LoadResult" /> indicating if the key exists, and its
    /// value if any.</returns>
    member this.TryLoad(container, key) = this.TryLoadAsync(container, key).Result

    /// <summary>Loads the value from the key in the default container if it exists.</summary>
    /// <param name="key">The key used to store the value.</param>
    /// <returns>Returns a <see cref="LoadResult" /> indicating if the key exists, and its
    /// value if any.</returns>
    member this.TryLoad(key) = this.TryLoadAsync(key).Result

    /// <summary>Saves the given value under specified container/key. The value is actually
    /// updated only if the etag corresponds to current value. Etag can be obtained either
    /// from this function returned value, or by calling <see cref="TryLoad" />.</summary>
    /// <param name="container">The name of the container of the key.</param>
    /// <param name="key">The key used to store the value.</param>
    /// <param name="value">The value to store under container/key</param>
    /// <param name="etag">The etag value used to validate that the value did not change.
    /// Use null to check that the key does not already exist.</param>
    /// <returns>The new etag, or null if the etag did not match</returns>
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

    /// <summary>Saves the given value under specified key in the default container. The value is actually
    /// updated only if the etag corresponds to current value. Etag can be obtained either
    /// from this function returned value, or by calling <see cref="TryLoad" />.</summary>
    /// <param name="key">The key used to store the value.</param>
    /// <param name="value">The value to store under key</param>
    /// <param name="etag">The etag value used to validate that the value did not change.
    /// Use null to check that the key does not already exist.</param>
    /// <returns>The new etag, or null if the etag did not match</returns>
    member this.TrySaveAsync(key: string, value: string, etag: string) =
        this.TrySaveAsync(defaultContainer, key, value, etag)

    /// <summary>Saves the given value under specified container/key. The value is actually
    /// updated only if the etag corresponds to current value. Etag can be obtained either
    /// from this function returned value, or by calling <see cref="TryLoad" />.</summary>
    /// <param name="container">The name of the container of the key.</param>
    /// <param name="key">The key used to store the value.</param>
    /// <param name="value">The value to store under container/key</param>
    /// <param name="etag">The etag value used to validate that the value did not change.
    /// Use null to check that the key does not already exist.</param>
    /// <returns>The new etag, or null if the etag did not match</returns>
    member this.TrySave(container, key, value, etag) = this.TrySaveAsync(container, key, value, etag).Result

    /// <summary>Saves the given value under specified key in the default container. The value is actually
    /// updated only if the etag corresponds to current value. Etag can be obtained either
    /// from this function returned value, or by calling <see cref="TryLoad" />.</summary>
    /// <param name="key">The key used to store the value.</param>
    /// <param name="value">The value to store under key</param>
    /// <param name="etag">The etag value used to validate that the value did not change.
    /// Use null to check that the key does not already exist.</param>
    /// <returns>The new etag, or null if the etag did not match</returns>
    member this.TrySave(key, value, etag) = this.TrySaveAsync(key, value, etag).Result



    /// <summary>Saves the given value under specified container/key. The value is
    /// updated without checking the etag.</summary>
    /// <param name="container">The name of the container of the key.</param>
    /// <param name="key">The key used to store the value.</param>
    /// <param name="value">The value to store under container/key</param>
    member _.SaveAsync(container: string, key: string, value: string) =
        task {
            use client = new HttpClient()

            let! response = client.PutAsync(Uri(kv, container + "/" + key), new StringContent(value))
            if not response.IsSuccessStatusCode then
                return raiseHttpException(response)
        } :> Task

    /// <summary>Saves the given value under specified key in the default container.
    /// The value is updated without checking the etag.</summary>
    /// <param name="key">The key used to store the value.</param>
    /// <param name="value">The value to store under key</param>
    member this.SaveAsync(key: string, value: string) =
        this.SaveAsync(defaultContainer, key, value)
    
    /// <summary>Saves the given value under specified container/key. The value is
    /// updated without checking the etag.</summary>
    /// <param name="container">The name of the container of the key.</param>
    /// <param name="key">The key used to store the value.</param>
    /// <param name="value">The value to store under container/key</param>
    member this.Save(container, key, value) = this.SaveAsync(container, key, value).Wait()

    /// <summary>Saves the given value under specified key in the default container.
    /// The value is updated without checking the etag.</summary>
    /// <param name="key">The key used to store the value.</param>
    /// <param name="value">The value to store under key</param>
    member this.Save(key, value) = this.SaveAsync(key, value).Wait()


    /// <summary>Deletes a key from a container.</summary>
    /// <param name="container">The name of the container of the key.</param>
    /// <param name="key">The key used to store the value.</param>
    member _.DeleteKeyAsync(container: string, key: string) =
      task {
          use client = new HttpClient()

          let! response = client.DeleteAsync(Uri(kv, container + "/" + key))

          if not response.IsSuccessStatusCode
                && response.StatusCode <> HttpStatusCode.NotFound then
              return raiseHttpException(response)
      } :> Task

    /// <summary>Deletes a key from the default container.</summary>
    /// <param name="key">The key used to store the value.</param>
    member this.DeleteKeyAsync(key: string) =
        this.DeleteKeyAsync(defaultContainer, key)

    /// <summary>Deletes a key from a container.</summary>
    /// <param name="container">The name of the container of the key.</param>
    /// <param name="key">The key used to store the value.</param>
    member this.DeleteKey(container, key: string) =
        this.DeleteKeyAsync(container, key).Wait()

    /// <summary>Deletes a key from the default container.</summary>
    /// <param name="key">The key used to store the value.</param>
    member this.DeleteKey(key: string) =
        this.DeleteKeyAsync(key).Wait()

    /// <summary>Deletes a container and all keys it contains.</summary>
    /// <param name="container">The name of the container.</param>
    member _.DeleteContainerAsync(container: string) =
      task {
          use client = new HttpClient()

          let! response = client.DeleteAsync(Uri(kv, container))
          if not response.IsSuccessStatusCode
                && response.StatusCode <> HttpStatusCode.NotFound then
              return raiseHttpException(response)
      } :> Task

    /// <summary>Deletes a container and all keys it contains.</summary>
    /// <param name="container">The name of the container.</param>
    member this.DeleteContainer(container: string) =
        this.DeleteContainerAsync(container).Wait()

    /// <summary>Get all the keys from specified container.</summary>
    /// <param name="container">The name of the container.</param>
    /// <returns>Returns an array with all the keys.</returns>
    member _.GetKeysAsync(container: string) =
        task {
          use client = new HttpClient()

          let! response = client.GetStringAsync(Uri(kv, container))
          return response.Split("\n",StringSplitOptions.RemoveEmptyEntries)
        }

    /// <summary>Get all the keys from the default container.</summary>
    /// <returns>Returns an array with all the keys.</returns>
    member this.GetKeysAsync() =
        this.GetKeysAsync(defaultContainer)

    /// <summary>Get all the keys from specified container.</summary>
    /// <param name="container">The name of the container.</param>
    /// <returns>Returns an array with all the keys.</returns>
    member this.GetKeys(container) =
        this.GetKeysAsync(container).Result

    /// <summary>Get all the keys from the default container.</summary>
    /// <returns>Returns an array with all the keys.</returns>
    member this.GetKeys() =
        this.GetKeysAsync().Result

    /// <summary>Get a list of all containers names.</summary>
    /// <returns>Returns an array with all the containers names.</returns>
    member _.GetContainersAsync() =
        task {
          use client = new HttpClient()

          let! response = client.GetStringAsync(kv)
          return response.Split("\n",StringSplitOptions.RemoveEmptyEntries)
        }

    /// <summary>Get a list of all containers names.</summary>
    /// <returns>Returns an array with all the containers names.</returns>
    member this.GetContainers() =
        this.GetContainersAsync().Result

    /// <summary>Appends specified events to the end of a stream.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="events">A sequence of <see cref="EventData"/> structures containing
    /// events informaiton.</param>
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


    /// <summary>Appends specified events to the end of a stream.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="events">A sequence of <see cref="EventData"/> structures containing
    /// events informaiton.</param>
    member this.Append(stream: string, events) = this.AppendAsync(stream, events).Wait()

    /// <summary>Appends specified events to the end of a stream, if the stream's
    /// last event number matches provided expected version.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="expectedVersion">The expected number of the last event in the stream.</param>
    /// <param name="events">A sequence of <see cref="EventData"/> structures containing
    /// events informaiton.</param>
    /// <returns>Returns an <see cref="AppendResult"/> indicating if the operation
    /// succeeded, and in case of success, the new version to expect, as well as next event number.</returns>
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


    /// <summary>Appends specified events to the end of a stream, if the stream's
    /// last event number matches provided expected version.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="expectedVersion">The expected number of the last event in the stream.</param>
    /// <param name="events">A sequence of <see cref="EventData"/> structures containing
    /// events informaiton.</param>
    /// <returns>Returns an <see cref="AppendResult"/> indicating if the operation
    /// succeeded, and in case of success, the new version to expect, as well as next event number.</returns>
     member this.TryAppend(stream: string, expectedVersion, events) = this.TryAppendAsync(stream, expectedVersion, events).Result


    /// <summary>Appends specified events to the end of a stream, if the stream's
    /// last event number matches provided expected version. If the expected version does not
    /// match, returns the events that have been added since expected version.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="expectedVersion">The expected number of the last event in the stream.</param>
    /// <param name="events">A sequence of <see cref="EventData"/> structures containing
    /// events informaiton.</param>
    /// <returns>Returns an <see cref="AppendOrReadResult"/> indicating if the operation
    /// succeeded, the new version to expect, as well as next event number,
    /// and in case of failure, the list of new events.</returns>
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


    /// <summary>Appends specified events to the end of a stream, if the stream's
    /// last event number matches provided expected version. If the expected version does not
    /// match, returns the events that have been added since expected version.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="expectedVersion">The expected number of the last event in the stream.</param>
    /// <param name="events">A sequence of <see cref="EventData"/> structures containing
    /// events informaiton.</param>
    /// <returns>Returns an <see cref="AppendOrReadResult"/> indicating if the operation
    /// succeeded, the new version to expect, as well as next event number,
    /// and in case of failure, the list of new events.</returns>
     member this.TryAppendOrRead(stream: string, expectedVersion, events) = this.TryAppendOrReadAsync(stream, expectedVersion, events).Result

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" />  to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref="EventCount.All"/>
    /// to return all events.</param>
    /// <param name="linkOnly">Return only link information without original event data for links.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream, 
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
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

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" /> to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref=""EventCount.All""/>
    /// to return all events.</param>
    /// <param name="linkOnly">Return only link information without original event data for links.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream, 
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamForward(stream: string, start: int, count: int, linkOnly: bool) =
        this.ReadStreamForwardAsync(stream,start,count, linkOnly).Result

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" /> to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref=""EventCount.All""/>
    /// to return all events.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream, 
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamForwardAsync(stream: string, start: int, count: int) =
        this.ReadStreamForwardAsync(stream,start,count, false)

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" /> to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref=""EventCount.All""/>
    /// to return all events.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream, 
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamForward(stream: string, start: int, count: int) =
        this.ReadStreamForwardAsync(stream,start,count).Result

    /// <summary>Gets the list of the stream names in creation order, starting from specified one.</summary>
    /// <param name="start">The number of the first stream included. Use
    /// <see cref="EventNumber.Start" /> to start from the begining.</param>
    /// <param name="count">The number of streamss to return. Use <see cref=""EventCount.All""/>
    /// to return all events.</param>
    /// <returns>Returns a <see cref="StreamsSlice"/> that contains stream names,  
    /// the last stream number, and the next stream number.</returns>
    member this.GetStreamsAsync(start: int, count: int) =
        task {
            let! slice = this.ReadStreamForwardAsync("$streams", start, count, true)
        
            return {
                State = slice.State
                Streams = slice.Events |> Array.map (fun e -> e.OriginStream)
                LastEventNumber = slice.ExpectedVersion
                NextEventNumber = slice.NextEventNumber
            }
        }

    /// <summary>Gets the list of the stream names in creation order, starting from specified one.</summary>
    /// <param name="start">The number of the first stream included. Use
    /// <see cref="EventNumber.Start" /> to start from the begining.</param>
    /// <param name="count">The number of streamss to return. Use <see cref=""EventCount.All""/>
    /// to return all events.</param>
    /// <returns>Returns a <see cref="StreamsSlice"/> that contains stream names,  
    /// the last stream number, and the next stream number.</returns>
    member this.GetStreams(start: int, count: int) =
        this.GetStreamsAsync(start, count).Result
