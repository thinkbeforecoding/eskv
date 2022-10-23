namespace eskv.Hashing

open System
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns
open System.Security.Cryptography
open System.Buffers
open System.Runtime.InteropServices
open System.Runtime.CompilerServices
open System.Collections.Generic
open FSharp.NativeInterop


#nowarn "9"

module private Native =
    let inline asRef (x: 'a inref) = &Unsafe.AsRef(&x)

    let inline asSpan (x: 'a inref) =
        MemoryMarshal.CreateReadOnlySpan(&asRef &x, 1)

    let inline asBytes (x: 'a inref) = MemoryMarshal.AsBytes(asSpan &x)

    let inline stackalloc<'t when 't: unmanaged> n =
        Span<'t>(NativePtr.toVoidPtr (NativePtr.stackalloc<'t> (n)), n)

#warn "9"

open Native

module private Base64 =
    let base64 =
        [| yield! [| 'A' .. 'Z' |]
           yield! [| 'a' .. 'z' |]
           yield! [| '0' .. '9' |]
           '-'
           '_' |]

    let encode (data: ReadOnlySpan<byte>) =
        let str = stackalloc<char> (((data.Length + 2) / 3) * 4)
        let blocks = data.Length / 3
        let srcEnd = blocks * 3
        let safe = data.Slice(0, srcEnd)

        for i in 0 .. blocks - 1 do
            let n = i * 3
            let d1 = safe[n]
            let d2 = safe[n + 1]
            let d3 = safe[n + 2]

            let j = i * 4
            str[j] <- base64[int (d1 >>> 2)]
            str[j + 1] <- base64[int (((d1 &&& 0b11uy) <<< 4) ||| (d2 >>> 4))]
            str[j + 2] <- base64[int ((d2 &&& 0b1111uy) <<< 2 ||| (d3 >>> 6))]
            str[j + 3] <- base64[int (d3 &&& 0b111111uy)]

        let rest = data.Slice(srcEnd)
        let strEnd = str.Slice(blocks * 4)

        let slice =
            if rest.Length > 0 then
                let d1 = rest[0]
                strEnd[0] <- base64[int (d1 >>> 2)]
                let d1' = (d1 &&& 0b11uy) <<< 4

                if rest.Length > 1 then
                    let d2 = rest[1]
                    strEnd[1] <- base64[int (d1' ||| (d2 >>> 4))]
                    strEnd[2] <- base64[int ((d2 &&& 0b1111uy) <<< 2)]
                    str.Slice(0, str.Length - 1)
                else
                    strEnd[1] <- base64[int d1']
                    str.Slice(0, str.Length - 2)
            else
                str

        String(Span.op_Implicit slice)


[<Flags>]
type HasherOptions =
    | Default = 0
    | ShortTypeNames = 1

type Hasher(h: IncrementalHash, options: HasherOptions) =
    let buffer = MemoryPool.Shared.Rent(4096)
    let visitedMethods = HashSet()

    member _.Add(x: ReadOnlySpan<byte>) = h.AppendData(x)

    member this.Add(x: string) =
        let bytes = buffer.Memory.Span
        let len = Text.Encoding.UTF8.GetBytes(x.AsSpan(), bytes)
        this.Add(Span.op_Implicit (bytes.Slice(0, len)))

    member this.Add(x: int32) = this.Add(asBytes &x)

    member this.Add(b: bool) =
        let x = if b then 1uy else 0uy
        this.Add(asSpan &x)


    member this.AddValue(v: obj) =
        match v with
        | null -> this.Add 0
        | :? int as v -> this.Add v
        | :? string as s -> this.Add s
        | :? uint as v -> this.Add(asBytes &v)
        | :? int64 as v -> this.Add(asBytes &v)
        | :? uint64 as v -> this.Add(asBytes &v)
        | :? int16 as v -> this.Add(asBytes &v)
        | :? uint16 as v -> this.Add(asBytes &v)
        | :? byte as v -> this.Add(asBytes &v)
        | :? sbyte as v -> this.Add(asBytes &v)
        | :? float32 as v -> this.Add(asBytes &v)
        | :? float as v -> this.Add(asBytes &v)
        | :? bool as b -> this.Add b
        | _ -> failwith "Value type not supported"


    member this.AddType(t: Type) =
        if options.HasFlag HasherOptions.ShortTypeNames then
            this.Add t.Name
        else
            this.Add t.FullName

    member this.AddVar(v: Var) =
        this.Add v.IsMutable
        this.AddType v.Type

    member this.AddMethod(m: Reflection.MethodInfo) =

        this.Add m.Name
        this.AddType m.DeclaringType

        if m.IsGenericMethod then
            for t in m.GetGenericArguments() do
                this.AddType t

        for p in m.GetParameters() do
            this.AddType p.ParameterType

        this.AddType m.ReturnType

    member this.AddCtor(m: Reflection.ConstructorInfo) =

        this.Add m.Name
        this.AddType m.DeclaringType

        if m.IsGenericMethod then
            for t in m.GetGenericArguments() do
                this.AddType t

        for p in m.GetParameters() do
            this.AddType p.ParameterType



    member this.ComputeHash(m: Reflection.MethodInfo) =

        let rec addExpr =
            function
            | Patterns.Lambda (v, c) ->
                this.Add "Lambda"
                this.AddVar v
                addExpr c

            | Patterns.Call (t, m, args) ->

                this.Add "Call"
                addTarget t
                addMethodOrDef m
                addExprs args
            | Patterns.Var v ->
                this.Add "Var"
                this.AddVar v
            | Patterns.Value (o, t) ->
                this.Add "Value"
                this.AddValue o
                this.AddType t
            | Patterns.AddressOf x ->
                this.Add "AddressOf"
                addExpr x
            | Patterns.AddressSet (x, v) ->
                this.Add "AddressSet"
                addExpr x
                addExpr v
            | Patterns.Application (x, v) ->
                this.Add "Application"
                addExpr x
                addExpr v
            | Patterns.CallWithWitnesses (t, m, m2, args, args2) ->

                this.Add "CallWithWitnesses"
                addTarget t
                addMethodOrDef m
                addMethodOrDef m2
                addExprs args
                addExprs args2
            | Patterns.Coerce (e, t) ->
                this.Add "Coerce"
                addExpr e
                this.AddType t
            | Patterns.DefaultValue (t) ->
                this.Add "DefaultValue"
                this.AddType t
            | Patterns.FieldGet (t, f) ->
                this.Add "FieldGet"
                addTarget t
                this.Add f.Name
                this.AddType f.FieldType
            | Patterns.FieldSet (t, f, e) ->
                this.Add "FieldSet"
                addTarget t
                this.Add f.Name
                this.AddType f.FieldType
                addExpr e

            | Patterns.ForIntegerRangeLoop (v, es, ei, ee) ->
                this.Add "ForIntegerRangeLoop"
                this.AddVar v
                addExpr es
                addExpr ei
                addExpr ee
            | Patterns.IfThenElse (c, t, f) ->
                this.Add "IfThenElse"
                addExpr c
                addExpr t
                addExpr f
            | Patterns.Let (v, e, body) ->
                this.Add "Let"
                this.AddVar v
                addExpr e
                addExpr body
            | Patterns.LetRecursive (defs, body) ->

                this.Add "LetRecursive"
                addExpr body

                for v, e in defs do
                    this.AddVar v
                    addExpr e

            | Patterns.NewArray (t, xs) ->
                this.Add "NewArray"
                this.AddType t
                addExprs xs

            | Patterns.NewDelegate (t, vs, e) ->
                this.Add "NewDelegate"
                this.AddType t

                for v in vs do
                    this.AddVar v

                addExpr e

            | Patterns.NewObject (c, args) ->
                this.Add "NewObject"
                this.AddCtor c
                addExprs args

            | Patterns.NewRecord (t, args) ->
                this.Add "NewRecord"
                this.AddType t
                addExprs args

            | Patterns.NewStructTuple (args) ->
                this.Add "NewStructTuple"
                addExprs args
            | Patterns.NewTuple (args) ->
                this.Add "NewTuple"
                addExprs args
            | Patterns.NewUnionCase (c, args) ->
                this.Add "NewUnionCase"
                this.AddType c.DeclaringType
                this.Add c.Name
                this.Add c.Tag
                addExprs args

            | Patterns.PropertyGet (t, p, args) ->
                this.Add "PropertyGet"
                addTarget t
                this.AddMethod p.GetMethod
                addExprs args

            | Patterns.PropertySet (t, p, args, e) ->
                this.Add "PropertySet"
                addTarget t
                this.AddMethod p.GetMethod
                addExprs args
                addExpr e

            | Patterns.QuoteRaw e ->
                this.Add "QuoteRaw"
                addExpr e
            | Patterns.QuoteTyped e ->
                this.Add "QuoteTyped"
                addExpr e

            | Patterns.Sequential (e1, e2) ->
                this.Add "Sequential"
                addExpr e1
                addExpr e2

            | Patterns.TryFinally (body, fclause) ->
                this.Add "TryFinally"
                addExpr body
                addExpr fclause

            | Patterns.TryWith (body, v, wclause, v2, wclause2) ->
                this.Add "TryWith"
                addExpr body
                this.AddVar v
                addExpr wclause
                this.AddVar v2
                addExpr wclause2
            | Patterns.TupleGet (e, i) ->
                this.Add "TupleGet"
                addExpr e
                this.Add i
            | Patterns.TypeTest (e, t) ->
                this.Add "TypeTest"
                addExpr e
                this.AddType t
            | Patterns.UnionCaseTest (e, c) ->
                this.Add "UnionCaseTest"
                addExpr e
                this.AddType c.DeclaringType
                this.Add c.Name
                this.Add c.Tag
            | Patterns.ValueWithName (v, t, n) ->
                this.Add "ValueWithName"
                this.AddValue v
                this.AddType t
                this.Add n
            | Patterns.VarSet (v, e) ->
                this.Add "VarSet"
                this.AddVar v
                addExpr e
            | Patterns.WhileLoop (body, cond) ->
                this.Add "WhileLoop"
                addExpr body
                addExpr cond
            | Patterns.WithValue (v, t, e) ->
                this.Add "WithValue"
                this.AddValue v
                this.AddType t
                addExpr e
            | x -> failwithf "Unsupported expression: %A" x

        and addMethodOrDef (m: Reflection.MethodInfo) =
            match Expr.TryGetReflectedDefinition(m) with
            | Some e -> if visitedMethods.Add m then this.AddMethod m else addExpr e
            | None -> this.AddMethod m

        and addTarget (t: Expr option) = Option.iter addExpr t

        and addExprs (es: Expr list) =
            for e in es do
                addExpr e

        match Expr.TryGetReflectedDefinition(m) with
        | Some e ->
            visitedMethods.Add m |> ignore
            addExpr e
            let span = buffer.Memory.Span
            let len = h.GetCurrentHash(span)
            Base64.encode (Span.op_Implicit (span.Slice(0, min len 15)))
        | None -> failwithf "No def"

    interface IDisposable with
        member _.Dispose() =
            buffer.Dispose()
            h.Dispose()

    static member Compute([<ReflectedDefinition>] x: Expr<_ -> _>) =
        Hasher.ComputeInternal(null, x, HasherOptions.Default)

    static member Compute([<ReflectedDefinition>] x: Expr<_ -> _>, options) =
        Hasher.ComputeInternal(null, x, options)

    static member Compute(key: string, [<ReflectedDefinition>] x: Expr<_ -> _>) =
        Hasher.ComputeInternal(key, x, HasherOptions.Default)

    static member Compute(key: string, [<ReflectedDefinition>] x: Expr<_ -> _>, options) =
        Hasher.ComputeInternal(key, x, options)

    static member private ComputeInternal(key: string, x: Expr<_ -> _>, options) =

        let rec findCall =
            function
            | Call (None, m, _) -> m
            | Lambda (_, body) -> findCall body
            | expr -> failwithf "Unsupported expression %A. Pass a function" expr

        let m = findCall x

        use h = new Hasher(IncrementalHash.CreateHash(HashAlgorithmName.MD5), options)

        if not (isNull key) then
            h.Add(key)

        h.ComputeHash m
