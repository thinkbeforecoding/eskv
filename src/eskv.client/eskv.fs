namespace eskv

open FSharp.Control
open System
open System.Net.Http

open System.Net
open System.Threading
open System.Threading.Tasks
open System.Linq
open System.Collections.Generic
open HttpMultipartParser

/// Contains result information for a <see cref="EskvClient.TryLoad"/> operation.
type LoadResult =
    { KeyExists: bool
      Value: string
      ETag: string }


/// Contains event data for a <see cref="EskvClient.Append"/> operation.
[<Struct>]
type EventData = { EventType: string; Data: string }

/// Contains event data returned by <see cref="EskvClient.ReadStreamForward"/>.
[<Struct>]
type EventRecord =
    { EventType: string
      EventNumber: int
      Data: string
      Stream: string
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
      NextEventNumber: int
      EndOfStream: bool }

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

type StreamResult =
    { State: StreamState
      Events: IAsyncEnumerable<EventRecord>
      ExpectedVersion: int
      NextEventNumber: int }

module private Http =

    let (|Success|Failure|) (statusCode: HttpStatusCode) =
        if int statusCode >= 200 && int statusCode <= 299 then
            Success
        else
            Failure

module HttpMultipartParser =
    type FilePart with

        member part.TryGetHeader(header) =
            if part.AdditionalProperties.ContainsKey(header) then
                Some part.AdditionalProperties[header]
            else
                None

open HttpMultipartParser
open System.Buffers

module Headers =
    [<Literal>]
    let EventType = "ESKV-Event-Type: "

    [<Literal>]
    let EventNumber = "ESKV-Event-Number: "

    [<Literal>]
    let OriginStream = "ESKV-Origin-Stream: "

    [<Literal>]
    let OriginEventNumber = "ESKV-Origin-Event-Number: "

module ExpectedVersion =
    [<Literal>]
    let Start = 0

    [<Literal>]
    let NoStream = -1

    [<Literal>]
    let Any = -2

module EventNumber =
    [<Literal>]
    let Start = 0

module EventCount =
    [<Literal>]
    let All = Int32.MaxValue



/// <summary>Creates a new instance of <see cref="EskvClient" /> to use
/// eskv in-memory key/value and event store.</summary>
/// <param name="uri">The uri of the eskv server.</param>
type EskvClient(uri: Uri) =
    let kv = Uri(uri, "kv/")
    let es = Uri(uri, "es/")

    [<Literal>]
    let defaultContainer = "default"

    let raiseHttpException (response: HttpResponseMessage) =
        raise (HttpRequestException(response.ReasonPhrase, null, Nullable response.StatusCode))

    let readEvents (response: HttpResponseMessage) =
        task {
            if response.StatusCode = HttpStatusCode.NoContent then
                return [||]
            else
                let streamId = response.Headers.GetValues("ESKV-Stream").First()

                use! stream = response.Content.ReadAsStreamAsync()
                let! reader = MultipartFormDataParser.ParseAsync(stream)


                let events = ResizeArray()

                for section in reader.Files do


                    let! eventType, data =
                        task {
                            match section.TryGetHeader("eskv-event-type") with
                            | Some eth ->
                                let eventType = eth.ToString()


                                return!
                                    task {
                                        use streamReader = new IO.StreamReader(section.Data)
                                        let! data = streamReader.ReadToEndAsync()
                                        return eventType, data
                                    }

                            | None -> return "$>", null
                        }

                    let eventNumber =
                        section.AdditionalProperties[ "eskv-event-number" ].ToString() |> int

                    let originEventNumber =
                        match section.TryGetHeader("eskv-origin-event-number") with
                        | Some h -> h.ToString() |> int
                        | None -> eventNumber

                    let originStream =
                        match section.TryGetHeader("eskv-origin-stream") with
                        | Some h -> h.ToString()
                        | None -> streamId

                    events.Add(
                        { EventType = eventType
                          EventNumber = eventNumber
                          Data = data
                          Stream = streamId
                          OriginEventNumber = originEventNumber
                          OriginStream = originStream }
                    )

                return events.ToArray()
        }

    let readStreamForwardAsync stream start count linkOnly =
        task {
            use client = new HttpClient()

            let request =
                new HttpRequestMessage(HttpMethod.Get, Uri(es, $"%s{stream}/%d{start}/%d{count}"))

            if linkOnly then
                request.Headers.Add("ESKV-Link-Only", "")

            let! response = client.SendAsync(request)

            if response.IsSuccessStatusCode then
                let nextExpectedVersion =
                    response.Headers.GetValues("ESKV-Expected-Version").First() |> int

                let nextEventNumber =
                    response.Headers.GetValues("ESKV-Next-Event-Number").First() |> int

                let endOfStream =
                    match response.Headers.TryGetValues("ESKV-End-Of-Stream") with
                    | true, values -> values |> Seq.map Boolean.Parse |> Seq.head
                    | false, _ -> false

                let! events = readEvents response

                return
                    { State = StreamState.StreamExists
                      Events = events
                      ExpectedVersion = nextExpectedVersion
                      NextEventNumber = nextEventNumber
                      EndOfStream = endOfStream }
            elif response.StatusCode = HttpStatusCode.NotFound then
                return
                    { State = StreamState.NoStream
                      Events = [||]
                      ExpectedVersion = -1
                      NextEventNumber = 0
                      EndOfStream = true }
            else
                return failwithf "%s" response.ReasonPhrase
        }

    let getStreamAsync eskv stream start linkOnly =
        task {
            use client = new HttpClient()

            let request =
                new HttpRequestMessage(HttpMethod.Get, Uri(es, $"%s{stream}/%d{start}/100"))

            if linkOnly then
                request.Headers.Add("ESKV-Link-Only", "")

            let! response = client.SendAsync(request)

            if response.IsSuccessStatusCode then
                let nextExpectedVersion =
                    response.Headers.GetValues("ESKV-Expected-Version").First() |> int

                let nextEventNumber =
                    response.Headers.GetValues("ESKV-Next-Event-Number").First() |> int

                let nextStreamExpectedVersion =
                    response.Headers.GetValues("ESKV-Stream-Expected-Version").First() |> int

                let nextStreamEventNumber =
                    response.Headers.GetValues("ESKV-Stream-Next-Event-Number").First() |> int

                let endOfStream =
                    match response.Headers.TryGetValues("ESKV-End-Of-Stream") with
                    | true, values -> values |> Seq.map Boolean.Parse |> Seq.head
                    | false, _ -> false

                let! events = readEvents response

                let slice =
                    { Slice.State = StreamState.StreamExists
                      Slice.Events = events
                      Slice.ExpectedVersion = nextExpectedVersion
                      Slice.NextEventNumber = nextEventNumber
                      Slice.EndOfStream = endOfStream }

                return
                    { State = slice.State
                      Events = new SliceEnumerable(eskv, stream, linkOnly, slice)
                      ExpectedVersion = nextStreamExpectedVersion
                      NextEventNumber = nextStreamEventNumber }
            elif response.StatusCode = HttpStatusCode.NotFound then
                return
                    { State = StreamState.NoStream
                      Events =
                        { new IAsyncEnumerable<EventRecord> with
                            member _.GetAsyncEnumerator(_) =
                                { new IAsyncEnumerator<EventRecord> with
                                    member _.MoveNextAsync() = ValueTask.FromResult(false)
                                    member _.Current = failwith "The collection is empty"
                                    member _.DisposeAsync() = ValueTask.CompletedTask } }
                      ExpectedVersion = -1
                      NextEventNumber = 0 }
            else
                return failwithf "%s" response.ReasonPhrase
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
            let! response = client.GetAsync(Uri(kv, container + "/" + key))

            match response.StatusCode with
            | HttpStatusCode.NotFound ->
                return
                    { KeyExists = false
                      Value = null
                      ETag = null }
            | Http.Success ->
                let etag =
                    match response.Headers.ETag with
                    | null -> null
                    | t -> t.Tag

                let! data = response.Content.ReadAsStringAsync()


                return
                    { KeyExists = true
                      Value = data
                      ETag = etag }
            | _ -> return raiseHttpException (response)

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
    member this.TryLoad(container, key) =
        this.TryLoadAsync(container, key).Result

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

            let! response = client.PutAsync(Uri(kv, container + "/" + key), new StringContent(value))

            match response.StatusCode with
            | HttpStatusCode.Conflict -> return null
            | Http.Success -> return response.Headers.ETag.Tag
            | _ -> return raiseHttpException (response)

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
    member this.TrySave(container, key, value, etag) =
        this.TrySaveAsync(container, key, value, etag).Result

    /// <summary>Saves the given value under specified key in the default container. The value is actually
    /// updated only if the etag corresponds to current value. Etag can be obtained either
    /// from this function returned value, or by calling <see cref="TryLoad" />.</summary>
    /// <param name="key">The key used to store the value.</param>
    /// <param name="value">The value to store under key</param>
    /// <param name="etag">The etag value used to validate that the value did not change.
    /// Use null to check that the key does not already exist.</param>
    /// <returns>The new etag, or null if the etag did not match</returns>
    member this.TrySave(key, value, etag) =
        this.TrySaveAsync(key, value, etag).Result



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
                return raiseHttpException (response)
        }
        :> Task

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
    member this.Save(container, key, value) =
        this.SaveAsync(container, key, value).Wait()

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

            if
                not response.IsSuccessStatusCode
                && response.StatusCode <> HttpStatusCode.NotFound
            then
                return raiseHttpException (response)
        }
        :> Task

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
    member this.DeleteKey(key: string) = this.DeleteKeyAsync(key).Wait()

    /// <summary>Deletes a container and all keys it contains.</summary>
    /// <param name="container">The name of the container.</param>
    member _.DeleteContainerAsync(container: string) =
        task {
            use client = new HttpClient()

            let! response = client.DeleteAsync(Uri(kv, container))

            if
                not response.IsSuccessStatusCode
                && response.StatusCode <> HttpStatusCode.NotFound
            then
                return raiseHttpException (response)
        }
        :> Task

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
            return response.Split("\n", StringSplitOptions.RemoveEmptyEntries)
        }

    /// <summary>Get all the keys from the default container.</summary>
    /// <returns>Returns an array with all the keys.</returns>
    member this.GetKeysAsync() = this.GetKeysAsync(defaultContainer)

    /// <summary>Get all the keys from specified container.</summary>
    /// <param name="container">The name of the container.</param>
    /// <returns>Returns an array with all the keys.</returns>
    member this.GetKeys(container) = this.GetKeysAsync(container).Result

    /// <summary>Get all the keys from the default container.</summary>
    /// <returns>Returns an array with all the keys.</returns>
    member this.GetKeys() = this.GetKeysAsync().Result

    /// <summary>Get a list of all containers names.</summary>
    /// <returns>Returns an array with all the containers names.</returns>
    member _.GetContainersAsync() =
        task {
            use client = new HttpClient()

            let! response = client.GetStringAsync(kv)
            return response.Split("\n", StringSplitOptions.RemoveEmptyEntries)
        }

    /// <summary>Get a list of all containers names.</summary>
    /// <returns>Returns an array with all the containers names.</returns>
    member this.GetContainers() = this.GetContainersAsync().Result

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
                    let! response = client.PostAsync(Uri(es, stream), content)

                    if not response.IsSuccessStatusCode then
                        return raiseHttpException (response)

        }
        :> Task


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


            let! response = client.PostAsync(Uri(es, stream), content)

            if response.IsSuccessStatusCode then
                let nextExpectedVersion =
                    response.Headers.GetValues("ESKV-Expected-Version").First() |> int

                let nextEventNumber =
                    response.Headers.GetValues("ESKV-Next-Event-Number").First() |> int

                return
                    { Success = true
                      ExpectedVersion = nextExpectedVersion
                      NextEventNumber = nextEventNumber }
            elif response.StatusCode = HttpStatusCode.Conflict then
                return
                    { Success = false
                      ExpectedVersion = 0
                      NextEventNumber = 0 }
            else
                return raiseHttpException (response)

        }


    /// <summary>Appends specified events to the end of a stream, if the stream's
    /// last event number matches provided expected version.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="expectedVersion">The expected number of the last event in the stream.</param>
    /// <param name="events">A sequence of <see cref="EventData"/> structures containing
    /// events informaiton.</param>
    /// <returns>Returns an <see cref="AppendResult"/> indicating if the operation
    /// succeeded, and in case of success, the new version to expect, as well as next event number.</returns>
    member this.TryAppend(stream: string, expectedVersion, events) =
        this.TryAppendAsync(stream, expectedVersion, events).Result


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


            let! response = client.PostAsync(Uri(es, stream), content)

            if response.IsSuccessStatusCode then
                let nextExpectedVersion =
                    response.Headers.GetValues("ESKV-Expected-Version").First() |> int

                let nextEventNumber =
                    response.Headers.GetValues("ESKV-Next-Event-Number").First() |> int

                return
                    { Success = true
                      NewEvents = Array.Empty()
                      ExpectedVersion = nextExpectedVersion
                      NextEventNumber = nextEventNumber }
            elif response.StatusCode = HttpStatusCode.Conflict then
                let! newEvents = readEvents response

                let nextExpectedVersion =
                    response.Headers.GetValues("ESKV-Expected-Version").First() |> int

                let nextEventNumber =
                    response.Headers.GetValues("ESKV-Next-Event-Number").First() |> int

                return
                    { Success = false
                      NewEvents = newEvents
                      ExpectedVersion = nextExpectedVersion
                      NextEventNumber = nextEventNumber }
            else
                return raiseHttpException (response)

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
    member this.TryAppendOrRead(stream: string, expectedVersion, events) =
        this.TryAppendOrReadAsync(stream, expectedVersion, events).Result

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" />  to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref="EventCount.All"/>
    /// to return all events.</param>
    /// <param name="linkOnly">Return only link information without original event data for links.</param>
    /// <param name="startExcluded">Returns events starting after start position</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member _.ReadStreamForwardAsync(stream: string, start: int, count: int, linkOnly: bool, startExcluded: bool) =
        let start = if startExcluded then start + 1 else start
        readStreamForwardAsync stream start count linkOnly

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" /> to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref=""EventCount.All""/>
    /// to return all events.</param>
    /// <param name="linkOnly">Return only link information without original event data for links.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamForward(stream: string, start: int, count: int, linkOnly: bool, startExcluded: bool) =
        this
            .ReadStreamForwardAsync(
                stream,
                start,
                count,
                linkOnly,
                startExcluded
            )
            .Result

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" /> to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref=""EventCount.All""/>
    /// to return all events.</param>
    /// <param name="linkOnly">Return only link information without original event data for links.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamForwardAsync(stream: string, start: int, count: int, linkOnly: bool) =
        this.ReadStreamForwardAsync(stream, start, count, linkOnly, false)


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
        this.ReadStreamForwardAsync(stream, start, count, linkOnly).Result

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" /> to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref=""EventCount.All""/>
    /// to return all events.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamForwardAsync(stream: string, start: int, count: int) =
        this.ReadStreamForwardAsync(stream, start, count, false)




    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" /> to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref=""EventCount.All""/>
    /// to return all events.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamForward(stream: string, start: int, count: int) =
        this.ReadStreamForwardAsync(stream, start, count).Result

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" /> to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref=""EventCount.All""/>
    /// to return all events.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamForwardAsync(stream: string, start: int) =
        this.ReadStreamForwardAsync(stream, start, Int32.MaxValue)




    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" /> to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref=""EventCount.All""/>
    /// to return all events.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamForward(stream: string, start: int) =
        this.ReadStreamForwardAsync(stream, start).Result


    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" />  to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref="EventCount.All"/>
    /// to return all events.</param>
    /// <param name="linkOnly">Return only link information without original event data for links.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamSinceAsync(stream: string, start: int, count: int, linkOnly: bool) =
        this.ReadStreamForwardAsync(stream, start, count, linkOnly, true)

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" />  to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref="EventCount.All"/>
    /// to return all events.</param>
    /// <param name="linkOnly">Return only link information without original event data for links.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamSince(stream: string, start: int, count: int, linkOnly: bool) =
        this.ReadStreamSinceAsync(stream, start, count, linkOnly).Result


    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" />  to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref="EventCount.All"/>
    /// to return all events.</param>
    /// <param name="linkOnly">Return only link information without original event data for links.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamSinceAsync(stream: string, start: int, count: int) =
        this.ReadStreamForwardAsync(stream, start, count, false)

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" />  to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref="EventCount.All"/>
    /// to return all events.</param>
    /// <param name="linkOnly">Return only link information without original event data for links.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamSince(stream: string, start: int, count: int) =
        this.ReadStreamSinceAsync(stream, start, count).Result

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" />  to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref="EventCount.All"/>
    /// to return all events.</param>
    /// <param name="linkOnly">Return only link information without original event data for links.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamSinceAsync(stream: string, start: int) =
        this.ReadStreamForwardAsync(stream, start, Int32.MaxValue, false)

    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" />  to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref="EventCount.All"/>
    /// to return all events.</param>
    /// <param name="linkOnly">Return only link information without original event data for links.</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.ReadStreamSince(stream: string, start: int) =
        this.ReadStreamSinceAsync(stream, start).Result


    /// <summary>Read events from stream starting from specified event number.</summary>
    /// <param name="stream">The name of the stream.</param>
    /// <param name="start">The number of the first event included. Use
    /// <see cref="EventNumber.Start" />  to start from the begining.</param>
    /// <param name="count">The number of events to return. Use <see cref="EventCount.All"/>
    /// to return all events.</param>
    /// <param name="linkOnly">Return only link information without original event data for links.</param>
    /// <param name="startExcluded">Returns events starting after start position</param>
    /// <returns>Returns a <see cref="Slice"/> that contains events, the state of the stream,
    /// the version to expect on next <see cref="TryAppend"/> operation, and the next event number.</returns>
    member this.GetStreamAsync(stream: string, start: int, linkOnly: bool, startExcluded: bool) =
        let start = if startExcluded then start + 1 else start
        getStreamAsync this stream start linkOnly








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

            return
                { State = slice.State
                  Streams = slice.Events |> Array.map (fun e -> e.OriginStream)
                  LastEventNumber = slice.ExpectedVersion
                  NextEventNumber = slice.NextEventNumber }
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


    member this.SubscribeAsync
        (
            stream: string,
            start: int,
            handler: EventRecord -> Task,
            cancellationToken: CancellationToken
        ) =
        task {
            let ws = new WebSockets.ClientWebSocket()
            let uri = UriBuilder(Uri(es, $"%s{stream}/%d{start}"), Scheme = "ws:").Uri
            do! ws.ConnectAsync(uri, cancellationToken)

            Task.Factory.StartNew(fun () ->
                task {
                    use bytes = MemoryPool.Shared.Rent(4096)

                    try
                        while true do

                            use memstream = new IO.MemoryStream()
                            let mutable quit = false

                            while not quit do
                                let! result = ws.ReceiveAsync(bytes.Memory, cancellationToken)
                                memstream.Write(bytes.Memory.Span.Slice(0, result.Count))
                                quit <- result.EndOfMessage

                            memstream.Position <- 0
                            use reader = new IO.StreamReader(memstream)
                            let mutable line = reader.ReadLine()
                            let mutable eventType = null
                            let mutable eventNumber = 0
                            let mutable originStream = null
                            let mutable originEventNumber = 0

                            while not (isNull line) && line.Length > 0 do
                                if line.StartsWith(Headers.EventType) then
                                    eventType <- line.Substring(Headers.EventType.Length).Trim()

                                if line.StartsWith(Headers.EventNumber) then
                                    let s = line.AsSpan().Slice(Headers.EventNumber.Length)

                                    match Int32.TryParse(s) with
                                    | true, n -> eventNumber <- n
                                    | false, _ -> ()

                                if line.StartsWith(Headers.OriginStream) then
                                    originStream <- line.Substring(Headers.OriginStream.Length).Trim()

                                if line.StartsWith(Headers.OriginEventNumber) then
                                    let s = line.AsSpan().Slice(Headers.OriginEventNumber.Length)

                                    match Int32.TryParse(s) with
                                    | true, n -> originEventNumber <- n
                                    | false, _ -> ()


                                line <- reader.ReadLine()


                            let data = reader.ReadToEnd()

                            let event =
                                { EventRecord.Data = data
                                  EventType = eventType
                                  EventNumber = eventNumber
                                  Stream = stream
                                  OriginEventNumber =  if isNull originStream then eventNumber else originEventNumber
                                  OriginStream = if isNull originStream then stream else originStream }

                            try
                                do! handler event
                            with ex ->
                                printfn "%O" ex
                    with
                    | :? TaskCanceledException  -> ()
                    | ex ->

                        printfn "%O" ex
                }
                :> Task)
            |> ignore

        } :> Task

    member this.Subscribe
        (
            stream: string,
            start: int,
            handler: EventRecord -> unit
        ) =
        let cts = new CancellationTokenSource()
        this.SubscribeAsync(stream, start, (fun e -> Task.FromResult(handler e)), cts.Token).Wait()
        { new IDisposable with
            member _.Dispose() = 
                cts.Cancel()
                cts.Dispose()}


    member this.SubscribeAllAsync(start, handler, cancellationToken) =
        this.SubscribeAsync("$all", start, handler, cancellationToken)

    member this.SubscribeAll(start, handler) =
        this.Subscribe("$all", start, handler)

and SliceEnumerable(client, stream, linkOnly, slice) =
    interface IAsyncEnumerable<EventRecord> with
        member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
            new SliceEnumerator(client, stream, linkOnly, slice, cancellationToken)


and SliceEnumerator(client: EskvClient, stream, linkOnly, slice, cancellationToken: CancellationToken) =
    let mutable slice = slice
    let mutable pos = -1
    let mutable ended = false


    interface IAsyncEnumerator<EventRecord> with
        member _.MoveNextAsync() =
            task {
                cancellationToken.ThrowIfCancellationRequested()

                if pos >= slice.Events.Length - 1 then
                    if slice.EndOfStream then
                        ended <- true
                        return false
                    else
                        let! s = client.ReadStreamForwardAsync(stream, slice.NextEventNumber, 100, linkOnly)
                        slice <- s

                        pos <- 0
                        return s.Events.Length <> 0
                else
                    pos <- pos + 1
                    return true
            }
            |> ValueTask<bool>

        member _.Current =
            if not ended then
                slice.Events[pos]
            else
                failwith "Stream is already terminated"

        member _.DisposeAsync() = ValueTask.CompletedTask
