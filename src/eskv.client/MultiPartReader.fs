// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
namespace Microsoft.AspNetCore.WebUtilities


open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Net.Http.Headers


// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

open System.Buffers;
open System.Text;
open System.Diagnostics
open System.Collections.Generic
open Microsoft.Extensions.Primitives


/// <summary>
/// A Stream that wraps another stream and allows reading lines.
/// The data is buffered in memory.
/// </summary>
type BufferedReadStream(inner: Stream, bufferSize: int, bytePool: ArrayPool<byte>) =
    inherit Stream()

    do
        if isNull inner then
            raise (ArgumentNullException(nameof(inner)))

    static let CR = '\r'B
    static let LF = '\n'B

    let _inner: Stream =
        inner
    let _buffer: byte[] = bytePool.Rent(bufferSize)
    let  _bytePool = bytePool
    let mutable _bufferOffset = 0
    let mutable _bufferCount = 0
    let mutable _disposed = false

    /// <summary>
    /// Creates a new stream.
    /// </summary>
    /// <param name="inner">The stream to wrap.</param>
    /// <param name="bufferSize">Size of buffer in bytes.</param>
    new (inner, bufferSize) = new BufferedReadStream(inner, bufferSize, ArrayPool<byte>.Shared)

    /// <summary>
    /// The currently buffered data.
    /// </summary>
    member this.BufferedData = ArraySegment(_buffer, _bufferOffset, _bufferCount)

    /// <inheritdoc/>
    override this.CanRead = _inner.CanRead || _bufferCount > 0

    /// <inheritdoc/>
    override this.CanSeek = _inner.CanSeek

    /// <inheritdoc/>
    override this.CanTimeout = _inner.CanTimeout

    /// <inheritdoc/>
    override this.CanWrite = _inner.CanWrite

    /// <inheritdoc/>
    override this.Length = _inner.Length

    /// <inheritdoc/>
    override this.Position
        with get() = _inner.Position - int64 _bufferCount
        and set value =
            if value < 0 then
                raise (ArgumentOutOfRangeException(nameof(value), value, "Position must be positive."))
            if value = this.Position then
                ()

            elif value <= _inner.Position then // Backwards?
                // Forward within the buffer?
                let innerOffset = int(_inner.Position - value);
                if innerOffset <= _bufferCount then
                    // Yes, just skip some of the buffered data
                    _bufferOffset <- _bufferOffset + innerOffset;
                    _bufferCount <- _bufferCount - innerOffset;
                else
                    // No, reset the buffer
                    _bufferOffset <- 0;
                    _bufferCount <- 0;
                    _inner.Position <- value;
            else
                // Forward, reset the buffer
                _bufferOffset <- 0;
                _bufferCount <- 0;
                _inner.Position <- value;

    /// <inheritdoc/>
    override this.Seek(offset, origin) =
        if origin = SeekOrigin.Begin then
            this.Position <- offset
        else if origin = SeekOrigin.Current then
            this.Position <- this.Position + offset
        else // if (origin == SeekOrigin.End)
            this.Position <- this.Length + offset;
        this.Position;

    /// <inheritdoc/>
    override this.SetLength(value) = _inner.SetLength(value)

    /// <inheritdoc/>
    override this.Dispose(disposing) =
        if not (_disposed) then
            _disposed <- true;
            _bytePool.Return(_buffer)

            if disposing then
                _inner.Dispose()

    /// <inheritdoc/>
    override this.Flush() = _inner.Flush()

    /// <inheritdoc/>
    override this.FlushAsync(cancellationToken) =
        _inner.FlushAsync(cancellationToken)

    /// <inheritdoc/>
    override this.Write(buffer, offset, count) =
        _inner.Write(buffer, offset, count)

    /// <inheritdoc/>
    override this.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellationToken) =
        _inner.WriteAsync(buffer, cancellationToken)

    /// <inheritdoc/>
    override this.WriteAsync(buffer: byte[], offset, count, cancellationToken) =
        _inner.WriteAsync(buffer, offset, count, cancellationToken)

    /// <inheritdoc/>
    override this.Read(buffer: byte[], offset, count) =
        BufferedReadStream.ValidateBuffer(buffer, offset, count)

        // Drain buffer
        if _bufferCount > 0 then
            let toCopy = Math.Min(_bufferCount, count)
            Buffer.BlockCopy(_buffer, _bufferOffset, buffer, offset, toCopy)
            _bufferOffset <- _bufferOffset + toCopy
            _bufferCount <- _bufferCount - toCopy
            toCopy;
        else
            _inner.Read(buffer, offset, count);

    /// <inheritdoc/>
    override this.ReadAsync(buffer: byte[], offset, count, cancellationToken) =
        BufferedReadStream.ValidateBuffer(buffer, offset, count)
        this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask()

    /// <inheritdoc/>
    override this.ReadAsync(buffer: Memory<byte>, cancellationToken) =
        // Drain buffer
        if _bufferCount > 0 then
            let toCopy = Math.Min(_bufferCount, buffer.Length);
            _buffer.AsMemory(_bufferOffset, toCopy).CopyTo(buffer);
            _bufferOffset <- _bufferOffset + toCopy;
            _bufferCount <- _bufferCount - toCopy;
            ValueTask<_>(toCopy);
        else
             _inner.ReadAsync(buffer, cancellationToken);

    /// <summary>
    /// Ensures that the buffer is not empty.
    /// </summary>
    /// <returns>Returns <c>true</c> if the buffer is not empty; <c>false</c> otherwise.</returns>
    member this.EnsureBuffered() =
        if (_bufferCount > 0) then
            true
        else
            // Downshift to make room
            _bufferOffset <- 0
            _bufferCount <- _inner.Read(_buffer, 0, _buffer.Length)
            _bufferCount > 0

    /// <summary>
    /// Ensures that the buffer is not empty.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Returns <c>true</c> if the buffer is not empty; <c>false</c> otherwise.</returns>
    member this.EnsureBufferedAsync(cancellationToken) =
        task {
        if _bufferCount > 0 then
            return true
        else
            // Downshift to make room
            _bufferOffset <- 0;
            let! count = _inner.ReadAsync(_buffer.AsMemory(), cancellationToken)
            _bufferCount <- count
            return _bufferCount > 0
        }

    /// <summary>
    /// Ensures that a minimum amount of buffered data is available.
    /// </summary>
    /// <param name="minCount">Minimum amount of buffered data.</param>
    /// <returns>Returns <c>true</c> if the minimum amount of buffered data is available; <c>false</c> otherwise.</returns>
    member this.EnsureBuffered(minCount: int) =
        if (minCount > _buffer.Length) then
            raise (ArgumentOutOfRangeException(nameof(minCount), minCount, sprintf "The value must be smaller than the buffer size: %d" _buffer.Length))

        let rec loop() = 
            if _bufferCount < minCount then
                // Downshift to make room
                if _bufferOffset > 0 then
                    if _bufferCount > 0 then
                        Buffer.BlockCopy(_buffer, _bufferOffset, _buffer, 0, _bufferCount)
                    _bufferOffset <- 0
                let read = _inner.Read(_buffer, _bufferOffset + _bufferCount, _buffer.Length - _bufferCount - _bufferOffset)
                _bufferCount <- _bufferCount + read
                if read = 0 then
                    false
                else
                    loop()
            else
                true
                
        
        loop()

    /// <summary>
    /// Ensures that a minimum amount of buffered data is available.
    /// </summary>
    /// <param name="minCount">Minimum amount of buffered data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Returns <c>true</c> if the minimum amount of buffered data is available; <c>false</c> otherwise.</returns>
    member this.EnsureBufferedAsync(minCount: int, cancellationToken: CancellationToken): Task<bool> =
        task {
        if minCount > _buffer.Length then
            return raise (ArgumentOutOfRangeException(nameof(minCount), minCount, sprintf "The value must be smaller than the buffer size: %d" _buffer.Length))
        else 
            let rec loop() =
                task {
                if _bufferCount < minCount then
                    // Downshift to make room
                    if _bufferOffset > 0 then
                        if _bufferCount > 0 then
                            Buffer.BlockCopy(_buffer, _bufferOffset, _buffer, 0, _bufferCount)
                        _bufferOffset <- 0
                    let! read  =  _inner.ReadAsync(_buffer.AsMemory(_bufferOffset + _bufferCount, _buffer.Length - _bufferCount - _bufferOffset), cancellationToken)
                    _bufferCount <- _bufferCount + read
                    if read = 0 then
                        return false
                    else
                        return! loop()
                else
                    return true
            }
            return! loop()
        }

    /// <summary>
    /// Reads a line. A line is defined as a sequence of characters followed by
    /// a carriage return immediately followed by a line feed. The resulting string does not
    /// contain the terminating carriage return and line feed.
    /// </summary>
    /// <param name="lengthLimit">Maximum allowed line length.</param>
    /// <returns>A line.</returns>
    member this.ReadLine(lengthLimit: int) =
        this.CheckDisposed()
        use builder = new MemoryStream(200)
        let mutable foundCR = false
        let mutable foundCRLF = false

        while not foundCRLF && this.EnsureBuffered() do
            if builder.Length > lengthLimit then
                raise (InvalidDataException($"Line length limit {lengthLimit} exceeded."))
            this.ProcessLineChar(builder, &foundCR, &foundCRLF);

        BufferedReadStream.DecodeLine(builder, foundCRLF)

    /// <summary>
    /// Reads a line. A line is defined as a sequence of characters followed by
    /// a carriage return immediately followed by a line feed. The resulting string does not
    /// contain the terminating carriage return and line feed.
    /// </summary>
    /// <param name="lengthLimit">Maximum allowed line length.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A line.</returns>
    member this.ReadLineAsync(lengthLimit: int, cancellationToken: CancellationToken) =
        task {
        this.CheckDisposed()
        use builder = new MemoryStream(200)
        let mutable foundCR = false
        let mutable foundCRLF = false

        let rec loop() =
            task {
                if not foundCRLF then
                    let! hasBuffered = this.EnsureBufferedAsync(cancellationToken)
                    if hasBuffered then
                        if (builder.Length > lengthLimit) then
                            return raise (InvalidDataException($"Line length limit {lengthLimit} exceeded."))
                        else
                            this.ProcessLineChar(builder, &foundCR, &foundCRLF)
                            return! loop()
            }
        do! loop()

        return BufferedReadStream.DecodeLine(builder, foundCRLF);
        }

    member private this.ProcessLineChar(builder: MemoryStream, foundCR: bool byref, foundCRLF: bool byref) =
        let b = _buffer[_bufferOffset]
        builder.WriteByte(b)
        _bufferOffset<- _bufferOffset+1
        _bufferCount <- _bufferCount-1
        if (b = LF && foundCR) then
            foundCRLF <- true
        else
            foundCR <- b = CR;

    static member DecodeLine(builder: MemoryStream, foundCRLF: bool) =
        // Drop the final CRLF, if any
        let length = if foundCRLF then builder.Length - 2L else builder.Length
        Encoding.UTF8.GetString(builder.ToArray(), 0, (int)length)

    member private _.CheckDisposed() =
        if _disposed then
            raise (ObjectDisposedException(nameof(BufferedReadStream)))

    static member private ValidateBuffer(buffer: byte[] , offset: int, count: int) =
        // Delegate most of our validation.
        let _ = new ArraySegment<byte>(buffer, offset, count)
        if count = 0 then
            raise (ArgumentOutOfRangeException(nameof(count), "The value must be greater than zero."))
       
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

type MultipartBoundary(boundary: string, expectLeadingCrlf: bool) as self =
    let _skipTable = Array.zeroCreate<int> 256
    let Initialize(boundary: string, expectLeadingCrlf: bool) =
        if expectLeadingCrlf then
            self.BoundaryBytes <- Encoding.UTF8.GetBytes("\r\n--" + boundary)
        else
            self.BoundaryBytes <- Encoding.UTF8.GetBytes("--" + boundary)
        self.FinalBoundaryLength <- self.BoundaryBytes.Length + 2; // Include the final '--' terminator.

        let length = self.BoundaryBytes.Length
        for i in 0 .. _skipTable.Length-1 do
            _skipTable[i] <- length
        for i in 0 .. length-1 do
            _skipTable[int self.BoundaryBytes[i]] <- Math.Max(1, length - 1 - i)


    do 
        if isNull boundary then
            raise (ArgumentNullException(nameof(boundary)))
        Initialize(boundary, expectLeadingCrlf)

    let mutable _expectLeadingCrlf = expectLeadingCrlf


    new(boundary) = MultipartBoundary(boundary, true)


    member this.GetSkipValue(input: byte) =
        _skipTable[int input]

    member this.ExpectLeadingCrlf
        with get() = _expectLeadingCrlf
        and set value =
            if value <> _expectLeadingCrlf then
                _expectLeadingCrlf <- value
                Initialize(boundary, _expectLeadingCrlf);

    [<DefaultValue>]
    val mutable BoundaryBytes: byte[] 


    [<DefaultValue>]
    val mutable FinalBoundaryLength: int

    // Licensed to the .NET Foundation under one or more agreements.
    // The .NET Foundation licenses this file to you under the MIT license.
    
    
[<Sealed>]
type MultipartReaderStream(stream: BufferedReadStream, boundary: MultipartBoundary, bytePool: ArrayPool<byte>) =
    inherit Stream()

    let _innerOffset: int64 = if stream.CanSeek then stream.Position else 0L
    let mutable  _position = 0L
    let mutable  _observedLength= 0L
    let mutable _finished = false

    let rec loopFindMatchBytes (segment1: ArraySegment<byte>) (matchBytes: byte[]) segmentEndMinusMatchBytesLength matchBytesLastByte matchBytesLengthMinusOne (matchOffset: int byref) (matchCount: int byref) =
        if matchOffset < segmentEndMinusMatchBytesLength then
            let lookaheadTailChar = segment1.Array[matchOffset + matchBytesLengthMinusOne];
            if (lookaheadTailChar = matchBytesLastByte &&
                MultipartReaderStream.CompareBuffers(segment1.Array, matchOffset, matchBytes, 0, matchBytesLengthMinusOne) = 0) then
                matchCount <- matchBytes.Length
                true
            else
                matchOffset <- matchOffset + boundary.GetSkipValue(lookaheadTailChar);
                loopFindMatchBytes segment1 matchBytes segmentEndMinusMatchBytesLength matchBytesLastByte matchBytesLengthMinusOne &matchOffset &matchCount
        else
            false
    let rec innerLoop (segment1: ArraySegment<byte>) (matchBytes: byte[]) countLimit (matchOffset: int byref) (matchCount: int byref) =
        if matchCount < matchBytes.Length && matchCount < countLimit then
            if matchBytes[matchCount] <> segment1.Array[matchOffset + matchCount] then
                matchCount <- 0
            else
                matchCount <- matchCount + 1
                innerLoop segment1 matchBytes countLimit &matchOffset &matchCount

    let rec loopEndsWithStartOfMatchBytes (segment1: ArraySegment<byte>) (matchBytes: byte[]) segmentEnd (matchOffset: int byref) (matchCount: int byref) =
        if matchOffset < segmentEnd then
            let countLimit = segmentEnd - matchOffset;
            matchCount <- 0
            innerLoop segment1 matchBytes countLimit &matchOffset &matchCount
            if matchCount > 0 then
                ()
            else
                matchOffset <- matchOffset+1
                loopEndsWithStartOfMatchBytes segment1 matchBytes segmentEnd &matchOffset &matchCount


    /// <summary>
    /// Creates a stream that reads until it reaches the given boundary pattern.
    /// </summary>
    /// <param name="stream">The <see cref="BufferedReadStream"/>.</param>
    /// <param name="boundary">The boundary pattern to use.</param>
    new (stream, boundary) = new MultipartReaderStream(stream, boundary, ArrayPool<byte>.Shared)


    [<DefaultValue>]
    val mutable FinalBoundaryFound: bool

    [<DefaultValueAttribute>]
    val mutable LengthLimit  : int64 Nullable

    override this.CanRead = true

    override this.CanSeek = stream.CanSeek

    override this.CanWrite = false

    override this.Length = _observedLength

    override this.Position
        with get() = _position
        and set value =
            if value < 0 then
                raise (ArgumentOutOfRangeException(nameof(value), value, "The Position must be positive."))
            if value > _observedLength then
                raise (ArgumentOutOfRangeException(nameof(value), value, "The Position must be less than length."))
            _position <- value
            if _position < _observedLength then
                _finished <- false

    override this.Seek(offset, origin) =
        if origin = SeekOrigin.Begin then
            this.Position <- offset;
        elif origin = SeekOrigin.Current then
            this.Position <- this.Position + offset
        else // if (origin == SeekOrigin.End)
            this.Position <- this.Length + offset
        this.Position

    override this.SetLength(value) =
        raise (NotSupportedException())

    override this.Write(buffer: byte[], offset, count) = 
        raise (NotSupportedException())

    override this.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellationToken) =
        raise (NotSupportedException())

    override this.WriteAsync(buffer: byte[], offset, count, cancellationToken) =
        raise (NotSupportedException())

    override this.Flush() =
        raise (NotSupportedException())

    member private this.PositionInnerStream() =
        if stream.CanSeek && stream.Position <> (_innerOffset + _position) then
            stream.Position <- _innerOffset + _position;

    member this.UpdatePosition(read: int) =
        _position <- _position + int64 read;
        if _observedLength < _position then
            _observedLength <- _position;
            if this.LengthLimit.HasValue && _observedLength > this.LengthLimit.GetValueOrDefault() then
                raise (InvalidDataException($"Multipart body length limit {this.LengthLimit.GetValueOrDefault()} exceeded."))
        read

    override this.Read(buffer: byte[], offset, count) =
        if _finished then
            0
        else

            this.PositionInnerStream()
            if not (stream.EnsureBuffered(boundary.FinalBoundaryLength)) then
                raise (IOException("Unexpected end of Stream, the content may have already been read by another component. "))
            let bufferedData = stream.BufferedData

            // scan for a boundary match, full or partial.
            let mutable read = 0
            let mutable matchOffset = 0
            let mutable matchCount = 0
            if this.SubMatch(bufferedData, boundary.BoundaryBytes, &matchOffset, &matchCount) then
                // We found a possible match, return any data before it.
                if matchOffset > bufferedData.Offset then
                    read <- stream.Read(buffer, offset, Math.Min(count, matchOffset - bufferedData.Offset));
                    this.UpdatePosition(read);
                else 
                    let length = boundary.BoundaryBytes.Length;
                    Debug.Assert((matchCount = length));

                    // "The boundary may be followed by zero or more characters of
                    // linear whitespace. It is then terminated by either another CRLF"
                    // or -- for the final boundary.
                    let boundary = bytePool.Rent(length);
                    read <- stream.Read(boundary, 0, length);
                    bytePool.Return(boundary);
                    Debug.Assert((read = length)); // It should have all been buffered

                    let mutable remainder = stream.ReadLine(lengthLimit = 100) // Whitespace may exceed the buffer.
                    remainder <- remainder.Trim()
                    if String.Equals("--", remainder, StringComparison.Ordinal) then
                        this.FinalBoundaryFound <- true;
                    Debug.Assert(this.FinalBoundaryFound || String.Equals(String.Empty, remainder, StringComparison.Ordinal), "Un-expected data found on the boundary line: " + remainder);
                    _finished <- true;
                    0
            else

                // No possible boundary match within the buffered data, return the data from the buffer.
                read <- stream.Read(buffer, offset, Math.Min(count, bufferedData.Count));
                this.UpdatePosition(read);

    override this.ReadAsync(buffer: byte[], offset, count, cancellationToken) =
        this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask()

    override this.ReadAsync(buffer: Memory<byte>, cancellationToken) : ValueTask<int> =
        task {
        if _finished then
            return 0
        else
            this.PositionInnerStream()
            let! buffered = stream.EnsureBufferedAsync(boundary.FinalBoundaryLength, cancellationToken)
            if not buffered then
                raise (IOException("Unexpected end of Stream, the content may have already been read by another component. "))
                return 0
            else
                let bufferedData = stream.BufferedData;

                // scan for a boundary match, full or partial.
                let mutable matchOffset = 0
                let mutable matchCount = 0
                let mutable read = 0
                if this.SubMatch(bufferedData, boundary.BoundaryBytes, &matchOffset, &matchCount) then
                    // We found a possible match, return any data before it.
                    if matchOffset > bufferedData.Offset then
                        // Sync, it's already buffered
                        let slice = buffer.Slice(0,Math.Min(buffer.Length, matchOffset - bufferedData.Offset));

                        read <- stream.Read(slice.Span);
                        return this.UpdatePosition(read);
                    else
                        let length = boundary.BoundaryBytes.Length
                        Debug.Assert((matchCount = length))

                        // "The boundary may be followed by zero or more characters of
                        // linear whitespace. It is then terminated by either another CRLF"
                        // or -- for the final boundary.
                        let boundary = bytePool.Rent(length);
                        read <- stream.Read(boundary, 0, length);
                        bytePool.Return(boundary);
                        Debug.Assert((read = length)); // It should have all been buffered

                        let! remainder = stream.ReadLineAsync(lengthLimit= 100, cancellationToken= cancellationToken); // Whitespace may exceed the buffer.
                        let remainder = remainder.Trim()
                        if String.Equals("--", remainder, StringComparison.Ordinal) then
                            this.FinalBoundaryFound <- true;
                        Debug.Assert(this.FinalBoundaryFound || String.Equals(String.Empty, remainder, StringComparison.Ordinal), "Un-expected data found on the boundary line: " + remainder);

                        _finished <- true
                        return 0
                else
                    // No possible boundary match within the buffered data, return the data from the buffer.
                    read <- stream.Read(buffer.Span.Slice(0,Math.Min(buffer.Length, bufferedData.Count)));
                    return this.UpdatePosition(read);
        } |> ValueTask<int>

    // Does segment1 contain all of matchBytes, or does it end with the start of matchBytes?
    // 1: AAAAABBBBBCCCCC
    // 2:      BBBBB
    // Or:
    // 1: AAAAABBB
    // 2:      BBBBB
    member private this.SubMatch(segment1: ArraySegment<byte>, matchBytes: byte[], matchOffset: int byref, matchCount: int byref) =
        // case 1: does segment1 fully contain matchBytes?
        let found =
            let matchBytesLengthMinusOne = matchBytes.Length - 1;
            let matchBytesLastByte = matchBytes[matchBytesLengthMinusOne];
            let segmentEndMinusMatchBytesLength = segment1.Offset + segment1.Count - matchBytes.Length;

            matchOffset <- segment1.Offset;
            loopFindMatchBytes segment1 matchBytes segmentEndMinusMatchBytesLength matchBytesLastByte matchBytesLengthMinusOne &matchOffset &matchCount

        if found then
            true
        else

            // case 2: does segment1 end with the start of matchBytes?
            let segmentEnd = segment1.Offset + segment1.Count;

            // clear matchCount to zero
            matchCount <- 0;

            loopEndsWithStartOfMatchBytes segment1 matchBytes segmentEnd &matchOffset &matchCount
            matchCount > 0

    static member CompareBuffers(buffer1: byte[], offset1: int, buffer2: byte[], offset2: int, count: int) =
        let rec loop count offset1 offset2 =
            if count > 0 then
                if (buffer1[offset1] <> buffer2[offset2]) then
                    int (buffer1[offset1] - buffer2[offset2])
                else
                    loop (count-1) (offset1+1) (offset2+1)
            else
                0
        loop count offset1 offset2

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/// <summary>
/// This API supports infrastructure and is not intended to be used
/// directly from your code. This API may change or be removed in future releases.
/// </summary>
[<Struct>]
type  KeyValueAccumulator =

    [<DefaultValue>]
    val mutable _accumulator : Dictionary<string, StringValues>
    [<DefaultValue>]
    val mutable _expandingAccumulator : Dictionary<string, List<string>>

    /// <summary>
    /// This API supports infrastructure and is not intended to be used
    /// directly from your code. This API may change or be removed in future releases.
    /// </summary>
    member this.Append(key: string, value: string) =
        if isNull this._accumulator then
            this._accumulator <- Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)

        let mutable values = Unchecked.defaultof<StringValues>
        if this._accumulator.TryGetValue(key, &values) then
            if values.Count = 0 then
                // Marker entry for this key to indicate entry already in expanding list dictionary
                this._expandingAccumulator[key].Add(value);
            elif values.Count = 1 then
                // Second value for this key
                this._accumulator[key] <- StringValues [| values[0];  value |] 
            else
                // Third value for this key
                // Add zero count entry and move to data to expanding list dictionary
                this._accumulator[key] <- Unchecked.defaultof<StringValues>

                if isNull this._expandingAccumulator  then
                    this._expandingAccumulator <- Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                // Already 3 entries so use starting allocated as 8; then use List's expansion mechanism for more
                let list = new List<string>(8);
                let array = values.ToArray();

                list.Add(array[0]);
                list.Add(array[1]);
                list.Add(value);

                this._expandingAccumulator[key] <- list;
        else
            // First value for this key
            this._accumulator[key] <- new StringValues(value);

        this.ValueCount <- this.ValueCount+1

    /// <summary>
    /// This API supports infrastructure and is not intended to be used
    /// directly from your code. This API may change or be removed in future releases.
    /// </summary>
    member this.HasValues = this.ValueCount > 0

    /// <summary>
    /// This API supports infrastructure and is not intended to be used
    /// directly from your code. This API may change or be removed in future releases.
    /// </summary>
    member this.KeyCount =
        if isNull this._accumulator then 0
        else this._accumulator.Count 

    /// <summary>
    /// This API supports infrastructure and is not intended to be used
    /// directly from your code. This API may change or be removed in future releases.
    /// </summary>
    [<DefaultValue>]
    val mutable ValueCount : int

    /// <summary>
    /// This API supports infrastructure and is not intended to be used
    /// directly from your code. This API may change or be removed in future releases.
    /// </summary>
    member this.GetResults() =
        if not (isNull this._expandingAccumulator) then
            // Coalesce count 3+ multi-value entries into _accumulator dictionary
            for entry in this._expandingAccumulator do
                this._accumulator[entry.Key] <- StringValues(entry.Value.ToArray())

        if not (isNull this._accumulator) then
            this._accumulator else
                Dictionary<string, StringValues>(0, StringComparer.OrdinalIgnoreCase)

module StreamExtensions =
    [<Literal>]
    let _maxReadBufferSize = 4096;

    type Stream with
        member stream.DrainAsync(cancellationToken) =
              stream.DrainAsync(ArrayPool<byte>.Shared, Nullable(), cancellationToken)
        member stream.DrainAsync(limit: int64 Nullable, cancellationToken) =
            stream.DrainAsync(ArrayPool<byte>.Shared, limit, cancellationToken)

        member stream.DrainAsync(bytePool: ArrayPool<byte>, limit: int64 Nullable, cancellationToken: CancellationToken) =
            task {
                cancellationToken.ThrowIfCancellationRequested()
                let buffer = bytePool.Rent(_maxReadBufferSize);
                let mutable total = 0
                try
                    let! read = stream.ReadAsync(buffer.AsMemory(), cancellationToken)
                    let mutable read = read
                    while read > 0 do
                        // Not all streams support cancellation directly.
                        cancellationToken.ThrowIfCancellationRequested();
                        if (limit.HasValue && (limit.GetValueOrDefault() - int64 total < int64 read)) then
                            raise (InvalidDataException($"The stream exceeded the data limit {limit.GetValueOrDefault()}."))
                        total <- total + read
                        let! r =  stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                        read <- r

                finally
                    bytePool.Return(buffer);
            }
open StreamExtensions

module HeaderNames =
    [<Literal>]
    let ContentType = "Content-Type"

    [<Literal>]
    let ContentDisposition = "Content-Disposition"

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/// <summary>
/// A multipart section read by <see cref="MultipartReader"/>.
/// </summary>
[<AllowNullLiteral>]
type MultipartSection() =
    /// <summary>
    /// Gets or sets the multipart header collection.
    /// </summary>
    [<DefaultValue>]
    val mutable Headers : Dictionary<string, StringValues>


    /// <summary>
    /// Gets the value of the <c>Content-Type</c> header.
    /// </summary>
    member this.ContentType =
    
        let mutable values = Unchecked.defaultof<StringValues>
        if not (isNull this.Headers) && this.Headers.TryGetValue(HeaderNames.ContentType, &values) then
            Nullable values
        else
            Nullable()

    /// <summary>
    /// Gets the value of the <c>Content-Disposition</c> header.
    /// </summary>
    member this.ContentDisposition =
        let mutable values = Unchecked.defaultof<StringValues>
        if not (isNull this.Headers) && this.Headers.TryGetValue(HeaderNames.ContentDisposition, &values) then
            Nullable values
        else
            Nullable()


    /// <summary>
    /// Gets or sets the body.
    /// </summary>
    [<DefaultValue>]
    val mutable  Body : Stream

    /// <summary>
    /// The position where the body starts in the total multipart body.
    /// This may not be available if the total multipart body is not seekable.
    /// </summary>
    [<DefaultValue>]
    val mutable BaseStreamOffset : int64 Nullable

// https://www.ietf.org/rfc/rfc2046.txt
/// <summary>
/// Reads multipart form content from the specified <see cref="Stream"/>.
/// </summary>
type  MultipartReader(boundary: string, stream: Stream, bufferSize: int) =
    do
        if isNull stream then
            raise (ArgumentNullException("stream"))
        if bufferSize < boundary.Length + 8 then
            raise (ArgumentOutOfRangeException(nameof(bufferSize), bufferSize, "Insufficient buffer space, the buffer must be larger than the boundary: " + boundary))
        if isNull boundary then
            raise (ArgumentNullException("boundary"))


    /// <summary>
    /// Gets the default value for <see cref="HeadersCountLimit"/>.
    /// Defaults to 16.
    /// </summary>
    static let DefaultHeadersCountLimit = 16;

    /// <summary>
    /// Gets the default value for <see cref="HeadersLengthLimit"/>.
    /// Defaults to 16,384 bytes, which is approximately 16KB.
    /// </summary>
    static let  DefaultHeadersLengthLimit = 1024 * 16;
    
    static let DefaultBufferSize = 1024 * 4;

    let _stream = new BufferedReadStream(stream, bufferSize);
    let _boundary = MultipartBoundary(boundary, false)
    let mutable _currentStream = new MultipartReaderStream(_stream, _boundary, LengthLimit =  int64 DefaultHeadersLengthLimit (*self.HeadersLengthLimit*) )

    /// <summary>
    /// The limit for the number of headers to read.
    /// </summary>
    member val HeadersCountLimit = DefaultHeadersCountLimit

    /// <summary>
    /// The combined size limit for headers per multipart section.
    /// </summary>
    member val HeadersLengthLimit = DefaultHeadersLengthLimit

    /// <summary>
    /// The optional limit for the total response body length.
    /// </summary>
    member val BodyLengthLimit : Nullable<int64> = Nullable()


    /// <summary>
    /// Initializes a new instance of <see cref="MultipartReader"/>.
    /// </summary>
    /// <param name="boundary">The multipart boundary.</param>
    /// <param name="stream">The <see cref="Stream"/> containing multipart data.</param>
    new (boundary: string, stream: Stream) = MultipartReader(boundary, stream, DefaultBufferSize)


    /// <summary>
    /// Reads the next <see cref="MultipartSection"/>.
    /// </summary>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.
    /// The default value is <see cref="CancellationToken.None"/>.</param>
    /// <returns></returns>
    member this.ReadNextSectionAsync(cancellationToken: CancellationToken) =
        task {
        // Drain the prior section.
        do! _currentStream.DrainAsync(cancellationToken)
        // If we're at the end return null
        if _currentStream.FinalBoundaryFound then
            // There may be trailer data after the last boundary.
            do! _stream.DrainAsync(int64 this.HeadersLengthLimit, cancellationToken)
            return null
        else
            let! headers = this.ReadHeadersAsync(cancellationToken)
            _boundary.ExpectLeadingCrlf <- true;
            _currentStream <- new MultipartReaderStream(_stream, _boundary, LengthLimit = this.BodyLengthLimit)
            let baseStreamOffset = if _stream.CanSeek then Nullable _stream.Position else Nullable()
            return MultipartSection(Headers = headers, Body = _currentStream, BaseStreamOffset = baseStreamOffset);
        }

    member private this.ReadHeadersAsync(cancellationToken: CancellationToken) =
        task {
        let mutable totalSize = 0
        let mutable accumulator = KeyValueAccumulator()
        let! l = _stream.ReadLineAsync(this.HeadersLengthLimit - totalSize, cancellationToken)
        let mutable line = l
        while not (String.IsNullOrEmpty(line)) do
            if this.HeadersLengthLimit - totalSize < line.Length then
                raise (InvalidDataException($"Multipart headers length limit {this.HeadersLengthLimit} exceeded."))
            totalSize <- totalSize + line.Length
            let splitIndex = line.IndexOf(':')
            if splitIndex <= 0 then
                raise (InvalidDataException($"Invalid header line: {line}"))

            let name = line.Substring(0, splitIndex)
            let value = line.Substring(splitIndex + 1, line.Length - splitIndex - 1).Trim()
            accumulator.Append(name, value)
            if accumulator.KeyCount > this.HeadersCountLimit then
                raise (InvalidDataException($"Multipart headers count limit {this.HeadersCountLimit} exceeded."))

            let! l = _stream.ReadLineAsync(this.HeadersLengthLimit - totalSize, cancellationToken)
            line <- l

        return accumulator.GetResults()
        }


