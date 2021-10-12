module Streams

open System
open System.Collections.Generic


type AppendList<'t>() =
    let mutable items = Array.zeroCreate<'t> 32
    let mutable length = 0

    member _.Count = length

    member this.AddRange( newItems: 't[]) =
        let newLength = length+newItems.Length
        if newLength > items.Length then
            this.Grow(newLength)
        Array.Copy(newItems, 0, items, length, newItems.Length)
        length <- newLength

    member _.Grow(newLength) = 
        let newCapatcity = max (items.Length*2) newLength
        let nextItems = Array.zeroCreate(newCapatcity)
        Array.Copy(items, nextItems, items.Length)
        items <- nextItems

    member _.GetRange(start, count) : ReadOnlyMemory<'t> =
        if start >= length then
            ReadOnlyMemory.Empty
        else
            items.AsMemory(start, min count (length-start))  |> Memory.op_Implicit


type EventData =
    { Type: string 
      Data: byte[]
      ContentType: string }

type EventRecord =
   { StreamId: string
     EventNumber: int
     Event: EventData }

type Link =
    { StreamId: string
      EventNumber: int
      OriginEvent: EventRecord }

type Event =
    | Record of EventRecord
    | Link of Link

type Stream = 
    { Id: string
      Events: AppendList<Event> }



type Action =
    | Append of streamId: string * EventData[] * expectedVersion:int * reply:((int * EventRecord[]) ValueOption -> unit)
    | ReadStream of streamId: string  * start: int * count: int * reply:(ValueOption<Event ReadOnlyMemory> -> unit)
    | ReadAll of start: int * count: int * reply:(EventRecord ReadOnlyMemory -> unit)
    
    
[<Literal>]
let StreamsId = "$streams"

let streams =
    MailboxProcessor.Start (fun mailbox ->
        let all = AppendList<EventRecord>()
        let streams = Dictionary<string,Stream>()

        let getStream streamId =
            match streams.TryGetValue(streamId) with
            | true, s -> s
            | false, _ ->
                let s = { Id = streamId; Events = AppendList<Event>()}
                streams.Add(streamId, s)
                s

        let rec loop() = async {
            match! mailbox.Receive() with
            | Append(streamId, events, expectedVersion, reply) ->
                let stream = getStream streamId

               
                if expectedVersion = -2 || expectedVersion = stream.Events.Count - 1 then
                    let first = stream.Events.Count
                    let records = events |> Array.mapi(fun i e -> {StreamId = stream.Id; EventNumber = first+i;Event  =  e})
                    stream.Events.AddRange(Array.map Record records)
                    all.AddRange(records)

                    if first = 0 && records.Length > 0 then
                        let streams = getStream StreamsId
                        streams.Events.AddRange([|Link { StreamId = StreamsId; EventNumber = streams.Events.Count; OriginEvent = records.[0]  }|] )


                    reply(ValueSome (stream.Events.Count-1, records))

                else
                    reply(ValueNone)
            | ReadStream(streamId, start, count, reply) ->
                match streams.TryGetValue(streamId) with
                | false,_ -> reply(ValueNone)
                | true, stream ->
                    stream.Events.GetRange(start, count) |> ValueSome |> reply
            | ReadAll(start, count, reply) ->
                  all.GetRange(start, count) |> reply


            return! loop()
        }
    
    
    
        loop()
    )


let appendAsync streamId events expectedVersion =
    streams.PostAndAsyncReply(fun c -> Append(streamId,events,expectedVersion, c.Reply))
    |> Async.StartAsTask


let readStreamAsync streamId start count =
    streams.PostAndAsyncReply(fun c -> ReadStream(streamId, start, count, c.Reply))
    |>Async.StartAsTask


let readAllAsync start count =
    streams.PostAndAsyncReply(fun c -> ReadAll(start, count, c.Reply))
    |>Async.StartAsTask
