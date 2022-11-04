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


## API

This documentation presents only synchronous operations. All methods have a asynchronous version using an `Async` suffix and returning a `Task` or a `Task<'t>`.

### Constructor

```
let client = EskvClient()
```
Instanciate a new EskvClient using default `http://localhost:5000` url.

```
EskvClient(uri: Uri)
```
Instanciate a new EskVClient using specified url.

### Key Value Store

The key value store saved value under specified keys. **Keys** are grouped by **container**. When the container is not specified, `default` is used. 

The key value store supports [ETags](https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/ETag) for version control.

#### Save

```
Save(key: string, value: string) : unit
```
Saves value under specified key. If the key doesn't exist, it is created. If it already exist, value is updated. No version control is done.

```
Save(container: string,key: string, value: string) : unit
```
Saves value under specified key in given container. If the key doesn't exist, it is created. If it already exist, value is updated. No version control is done.

#### TrySave

```
TrySave(key: string, value: string, etag: string) : string
```
Saves value under specified key with version control. If the key doesn't exist, pass `null` in the `etag` argument to create it. 

If the key already exist, pass current ETag value obtain from a call to `Load` or a previous call to `TrySave` to upate the value.

If the Etag matches, the key is created or updated with the specified value, and the new Etag is returned. Otherwise the method returns `null`. 

```
TrySave(container: string,key: string, value: string, etag: string)
```
Saves value under specified key in given container with version control. If the key doesn't exist, pass `null` in the `etag` argument to create it. 

If the key already exist, pass current ETag value obtain from a call to `Load` or a previous call to `TrySave` to upate the value.

If the Etag matches, the key is created or updated with the specified value, and the new Etag is returned. Otherwise the method returns `null`. 


#### TryLoad

```
TryLoad(key: string) : LoadResult
```
Tries to load the value from specified key.

if the key exists, the `LoadResult`'s `KeyExists` property is true, `Value` contains the key value, and the `Etag` is current key's Etag.

if the key doesn't exists, the `LoadResult`'s `KeyExists` property is false, `Value` and `Etag` are `null`. This `null` Etag can be used in a call to `TrySave` to indicates that the key should not exist.

```
TryLoad(container: string, key: string) : LoadResult
```
Tries to load the value from specified key in given container.

The `LoadResult` is similar to the overload without the container.

#### DeleteKey

```
DeleteKey(key: string) : unit
```

Deletes a key if it exists, does nothing otherwise.

```
DeleteKey(container: string, key: string) : unit
```

Deletes a key from specified container if it exists, does nothing otherwise.

#### GetKeys 

```
GetKeys() : string[]
```

Returns all the keys in the `default` container.

Throws an exception if the container does not exist.

```
GetKeys(container: string) : string[]
```
Returns all the keys in the `specified` container.

Throws an exception if the container does not exist.

#### GetContainers

```
GetContainers() : string[]
```

Returns all the containers names. Empty if no container exists.

#### DeleteContainer

```
DeleteContainer(container: string) : unit
```

Delete specified container if it exists, does nothing otherwise.


### Event Store

#### Append

```
Append(stream: string, events: EventData seq) : unit
```

Append events at the end of specified stream.

The `EventData` structure contains a `EventType` string property and a `Data` string property.

#### TryAppend


```
TryAppend(stream: string, expectedVersion: int events: EventData seq) : AppendResult
```

Tries to append events at the end of specified stream. The operation succeeds if the stream current version is equal to specified `expectedVersion`.

The `EventData` structure contains a `EventType` string property and a `Data` string property.

Provide `ExpectedVersion.NoStream` as the expectedVersion argement when the stream does not exist yet.

When the operation succeeds, `AppendResult` has the `Success`property equal to `true`. The `ExpectedVersion` property indicates the current version of the stream. It can be passed to TryAppend to append new events to the stream. The `NextEventNumber` property indicates the event number to use to read events just after the ones that have been appened.

#### ReadStreamForward

```
ReadStreamForward(stream: string, start: int) : Slice
```

Reads all events from specified stream starting at specified event number. Use 0 for `start` to read all events from the start.

Returns a `Slice` structure:

* `State` : `NoStream` when the stream doesn't exist, or `StreamExists` otherwise.
* `Events` : An array of `EventRecord` containing read events.
* `EndOfStream` : Indicates whether the end of the stream has been reached
* `ExpectedVersion`: The event number of the last event read. Can be passed to `TryAppend` to append new events at the end of the stream.
* `NextEventNumber`: the next event number that can be used to load the rest of the stream.

The `EventRecord` structure has the following properties:
* `EventNumber`: the number of the event in the stream
* `EventType`: The type of the event
* `Data`: The event data
* `OriginalEventNumber`: The number of the event in the original stream in case of projections (like for `$streams`), otherwise equal to `EventNumber`.
* 'OriginalStream': The original strema name in case of projections (like from `$streams`), otherwise equal to provided stream name.

```
ReadStreamForward(stream: string, start: int, count: int) : Slice
```

Reads a maximum of `count` events from specified stream starting at specified event number. Use 0 for `start` to read events from the start.

The returned `Slice` is similar to the previous overload.

Use `NextEventNumber` from the slice as the `start` argument of the next call to `ReadStreamForward` to get the next slice.

```
ReadStreamForward(stream: string, start: int, count: int, linkOnly: bool) : Slice
```
Reads a maximum of `count` events from specified stream starting at specified event number. Use 0 for `start` to read events from the start.

The returned `Slice` is similar to the previous overload.

When `linkOnly` is true, and the stream is a projection (like `stream`), the `EventRecord` `Data` property is null.

```
ReadStreamForward(stream: string, start: int, count: int, linkOnly: bool, startExcluded: bool) : Slice
```

Reads a maximum of `count` events from specified stream starting at specified event number. Use 0 for `start` to read events from the start.

The returned `Slice` is similar to the previous overload.

When `startExcluded` is true, the first event returned is the one just after `start`. This can be used when maintaining an expected version, and reading events that follow.

#### ReadStreamSince

```
ReadStreamSince(stream: string, start: int)
```

Equivalent to `ReadStreamForward`

```
ReadStreamSince(stream: string, start: int, count: int)
```

Equivalent to `ReadStreamForward`

```
ReadStreamSince(stream: string, start: int, count: int, linkOnly: bool)
```

Equivalent to `ReadStreamForward`

#### GetStreamAsync

```
GetStreamAsync(stream: string, start: int, count: int, linkOnly: bool, startExcluded: bool) : Task<ReadResult>
```

This method exists only in Async version, as it returns a `ReadResult` that contains an `IAsyncEnumerable<EventRecord>`.

Arguments are similar to `ReadStreamForward`.

The returned `ReadResult` has the following properties:

* `State` : `NoStream` when the stream doesn't exist, or `StreamExists` otherwise.
* `Events` : An `IAsyncEnumerable<EventRecord>` containing read events.
* `ExpectedVersion`: The event number of the last event read. Can be passed to `TryAppend` to append new events at the end of the stream.
* `NextEventNumber`: the next event number that can be used to load the rest of the stream.

#### TryAppendOrRead


```
TryAppendOrRead(stream: string, expectedVersion: int events: EventData seq) : AppendOrReadResult
```

Arguments are similar to `TryAppend`. When `expectedVersion` does not match current stream version, events following `expectedVersion` are returned in the `AppendOrReadResult` structure's `NewEvents` property. In this case, the `ExpectedVersion` and `NextEventVersion` property relate to the last returned event.

#### GetStreams

```
GetStreams(start: int, count: int) : StreamSlice
```

Get `count` streams names, starting at `start`.

The `StreamSlice` structure has the following properties:

* `State`: `NoStream` if no stream exists yet, otherwise `StreamExists`.
* `Streams`: An array that contains streams names.
* `LastEventNumber`: The event number of the last returned stream creation.
* `NextEventNumber`: The event number of the next stream creation.

#### Subscribe

```
Subscribe(stream: string, start: int, handler: EventRecord -> unit) : IDisposable
```

Subscribe to `stream` from `start` event.

Events already persisted are sent as soon as subscribing, new events are sent as they are added to the stream.

Call the `Dispose` on the returned `IDisposable`, to stop the subscription.


```
SubscribeAsync(stream: string, start: int, handler: EventRecord -> Task, cancellationToken: CancellationToken) : Task
```

Subscribe to `stream` from `start` event asynchronously.

Events already persisted are sent as soon as subscribing, new events are sent as they are added to the stream.

Cancel the `cancellationToken` to stop the subscription.

#### SubscribeAll

```
SubscribeAll(start: int, handler: EventRecord -> unit) : IDisposable
```

Subscribe to all events from `start` event.

Events already persisted are sent as soon as subscribing, new events are sent as they are added to the stream.

Call the `Dispose` on the returned `IDisposable`, to stop the subscription.


```
SubscribeAllAsync(start: int, handler: EventRecord -> Task, cancellationToken: CancellationToken) : Task
```

Subscribe to all events from `start` event asynchronously.

Events already persisted are sent as soon as subscribing, new events are sent as they are added to the stream.

Cancel the `cancellationToken` to stop the subscription.

