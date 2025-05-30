// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace Microsoft.FSharp.Core

module ExtraTopLevelOperators =

    open System
    open System.Collections.Generic
    open System.IO
    open System.Diagnostics
    open Microsoft.FSharp
    open Microsoft.FSharp.Core
    open Microsoft.FSharp.Core.Operators
    open Microsoft.FSharp.Collections
    open Microsoft.FSharp.Control
    open Microsoft.FSharp.Linq
    open Microsoft.FSharp.Primitives.Basics
    open Microsoft.FSharp.Core.CompilerServices

    let inline checkNonNullNullArg argName arg =
        match box arg with
        | null -> nullArg argName
        | _ -> ()

    let inline checkNonNullInvalidArg argName message arg =
        match box arg with
        | null -> invalidArg argName message
        | _ -> ()

    [<CompiledName("CreateSet")>]
    let set elements =
        Collections.Set.ofSeq elements

    let dummyArray = [||]

    let inline dont_tail_call f =
        let result = f ()
        dummyArray.Length |> ignore // pretty stupid way to avoid tail call, would be better if attribute existed, but this should be inlineable by the JIT
        result

    let inline ICollection_Contains<'collection, 'item when 'collection :> ICollection<'item>>
        (collection: 'collection)
        (item: 'item)
        =
        collection.Contains item

    [<DebuggerDisplay("Count = {Count}")>]
    [<DebuggerTypeProxy(typedefof<DictDebugView<_, _, _>>)>]
    type DictImpl<'SafeKey, 'Key, 'T>
        (t: Dictionary<'SafeKey, 'T>, makeSafeKey: 'Key -> 'SafeKey, getKey: 'SafeKey -> 'Key) =

        member _.Count = t.Count

        // Give a read-only view of the dictionary
        interface IDictionary<'Key, 'T> with
            member _.Item
                with get x = dont_tail_call (fun () -> t.[makeSafeKey x])
                and set _ _ = raise (NotSupportedException(SR.GetString(SR.thisValueCannotBeMutated)))

            member _.Keys =
                let keys = t.Keys

                { new ICollection<'Key> with
                    member _.Add(x) =
                        raise (NotSupportedException(SR.GetString(SR.thisValueCannotBeMutated)))

                    member _.Clear() =
                        raise (NotSupportedException(SR.GetString(SR.thisValueCannotBeMutated)))

                    member _.Remove(x) =
                        raise (NotSupportedException(SR.GetString(SR.thisValueCannotBeMutated)))

                    member _.Contains(x) =
                        t.ContainsKey(makeSafeKey x)

                    member _.CopyTo(arr, i) =
                        let mutable n = 0

                        for k in keys do
                            arr.[i + n] <- getKey k
                            n <- n + 1

                    member _.IsReadOnly = true

                    member _.Count = keys.Count
                  interface IEnumerable<'Key> with
                      member _.GetEnumerator() =
                          (keys |> Seq.map getKey).GetEnumerator()
                  interface System.Collections.IEnumerable with
                      member _.GetEnumerator() =
                          ((keys |> Seq.map getKey) :> System.Collections.IEnumerable).GetEnumerator()
                }

            member _.Values = upcast t.Values

            member _.Add(_, _) =
                raise (NotSupportedException(SR.GetString(SR.thisValueCannotBeMutated)))

            member _.ContainsKey(k) =
                dont_tail_call (fun () -> t.ContainsKey(makeSafeKey k))

            member _.TryGetValue(k, r) =
                let safeKey = makeSafeKey k

                match t.TryGetValue safeKey with
                | true, tsafe ->
                    (r <- tsafe
                     true)
                | false, _ -> false

            member _.Remove(_: 'Key) =
                (raise (NotSupportedException(SR.GetString(SR.thisValueCannotBeMutated))): bool)

        interface IReadOnlyDictionary<'Key, 'T> with

            member _.Item
                with get key = t.[makeSafeKey key]

            member _.Keys = t.Keys |> Seq.map getKey

            member _.TryGetValue(key, r) =
                match t.TryGetValue(makeSafeKey key) with
                | false, _ -> false
                | true, value ->
                    r <- value
                    true

            member _.Values = (t :> IReadOnlyDictionary<_, _>).Values

            member _.ContainsKey k =
                t.ContainsKey(makeSafeKey k)

        interface ICollection<KeyValuePair<'Key, 'T>> with

            member _.Add(_) =
                raise (NotSupportedException(SR.GetString(SR.thisValueCannotBeMutated)))

            member _.Clear() =
                raise (NotSupportedException(SR.GetString(SR.thisValueCannotBeMutated)))

            member _.Remove(_) =
                raise (NotSupportedException(SR.GetString(SR.thisValueCannotBeMutated)))

            member _.Contains(KeyValue(k, v)) =
                ICollection_Contains t (KeyValuePair<_, _>(makeSafeKey k, v))

            member _.CopyTo(arr, i) =
                let mutable n = 0

                for (KeyValue(k, v)) in t do
                    arr.[i + n] <- KeyValuePair<_, _>(getKey k, v)
                    n <- n + 1

            member _.IsReadOnly = true

            member _.Count = t.Count

        interface IReadOnlyCollection<KeyValuePair<'Key, 'T>> with
            member _.Count = t.Count

        interface IEnumerable<KeyValuePair<'Key, 'T>> with

            member _.GetEnumerator() =
                // We use an array comprehension here instead of seq {} as otherwise we get incorrect
                // IEnumerator.Reset() and IEnumerator.Current semantics.
                let kvps = [| for (KeyValue(k, v)) in t -> KeyValuePair(getKey k, v) |] :> seq<_>
                kvps.GetEnumerator()

        interface System.Collections.IEnumerable with
            member _.GetEnumerator() =
                // We use an array comprehension here instead of seq {} as otherwise we get incorrect
                // IEnumerator.Reset() and IEnumerator.Current semantics.
                let kvps =
                    [| for (KeyValue(k, v)) in t -> KeyValuePair(getKey k, v) |] :> System.Collections.IEnumerable

                kvps.GetEnumerator()

    and DictDebugView<'SafeKey, 'Key, 'T>(d: DictImpl<'SafeKey, 'Key, 'T>) =
        [<DebuggerBrowsable(DebuggerBrowsableState.RootHidden)>]
        member _.Items = Array.ofSeq d

    let inline dictImpl
        (comparer: IEqualityComparer<'SafeKey>)
        (makeSafeKey: 'Key -> 'SafeKey)
        (getKey: 'SafeKey -> 'Key)
        (l: seq<'Key * 'T>)
        =
        let t = Dictionary comparer

        for (k, v) in l do
            t.[makeSafeKey k] <- v

        DictImpl(t, makeSafeKey, getKey)

    // We avoid wrapping a StructBox, because under 64 JIT we get some "hard" tailcalls which affect performance
    let dictValueType (l: seq<'Key * 'T>) =
        dictImpl HashIdentity.Structural<'Key> id id l

    // Wrap a StructBox around all keys in case the key type is itself a type using null as a representation
    let dictRefType (l: seq<'Key * 'T>) =
        dictImpl RuntimeHelpers.StructBox<'Key>.Comparer (RuntimeHelpers.StructBox) (fun sb -> sb.Value) l

    [<CompiledName("CreateDictionary")>]
    let dict (keyValuePairs: seq<'Key * 'T>) : IDictionary<'Key, 'T> =
        if typeof<'Key>.IsValueType then
            dictValueType keyValuePairs
        else
            dictRefType keyValuePairs

    [<CompiledName("CreateReadOnlyDictionary")>]
    let readOnlyDict (keyValuePairs: seq<'Key * 'T>) : IReadOnlyDictionary<'Key, 'T> =
        if typeof<'Key>.IsValueType then
            dictValueType keyValuePairs
        else
            dictRefType keyValuePairs

    let getArray (vals: seq<'T>) =
        match vals with
        | :? ('T array) as arr -> arr
        | _ -> Seq.toArray vals

    [<CompiledName("CreateArray2D")>]
    let array2D (rows: seq<#seq<'T>>) =
        checkNonNullNullArg "rows" rows
        let rowsArr = getArray rows
        let m = rowsArr.Length

        if m = 0 then
            Array2D.zeroCreate<'T> 0 0
        else
            checkNonNullInvalidArg "rows" (SR.GetString(SR.nullsNotAllowedInArray)) rowsArr.[0]
            let firstRowArr = getArray rowsArr.[0]
            let n = firstRowArr.Length
            let res = Array2D.zeroCreate<'T> m n

            for j in 0 .. (n - 1) do
                res.[0, j] <- firstRowArr.[j]

            for i in 1 .. (m - 1) do
                checkNonNullInvalidArg "rows" (SR.GetString(SR.nullsNotAllowedInArray)) rowsArr.[i]
                let rowiArr = getArray rowsArr.[i]

                if rowiArr.Length <> n then
                    invalidArg "vals" (SR.GetString(SR.arraysHadDifferentLengths))

                for j in 0 .. (n - 1) do
                    res.[i, j] <- rowiArr.[j]

            res

    [<CompiledName("PrintFormatToString")>]
    let sprintf format =
        Printf.sprintf format

    [<CompiledName("PrintFormatToStringThenFail")>]
    let failwithf format =
        Printf.failwithf format

    [<CompiledName("PrintFormatToTextWriter")>]
    let fprintf (textWriter: TextWriter) format =
        Printf.fprintf textWriter format

    [<CompiledName("PrintFormatLineToTextWriter")>]
    let fprintfn (textWriter: TextWriter) format =
        Printf.fprintfn textWriter format

    [<CompiledName("PrintFormat")>]
    let printf format =
        Printf.printf format

    [<CompiledName("PrintFormatToError")>]
    let eprintf format =
        Printf.eprintf format

    [<CompiledName("PrintFormatLine")>]
    let printfn format =
        Printf.printfn format

    [<CompiledName("PrintFormatLineToError")>]
    let eprintfn format =
        Printf.eprintfn format

    [<CompiledName("DefaultAsyncBuilder")>]
    let async = AsyncBuilder()

    [<CompiledName("ToSingle")>]
    let inline single value =
        float32 value

    [<CompiledName("ToDouble")>]
    let inline double value =
        float value

    [<CompiledName("ToByte")>]
    let inline uint8 value =
        byte value

    [<CompiledName("ToSByte")>]
    let inline int8 value =
        sbyte value

    module Checked =

        [<CompiledName("ToByte")>]
        let inline uint8 value =
            Checked.byte value

        [<CompiledName("ToSByte")>]
        let inline int8 value =
            Checked.sbyte value

    [<CompiledName("SpliceExpression")>]
    let (~%) (expression: Microsoft.FSharp.Quotations.Expr<'T>) : 'T =
        ignore expression
        raise (InvalidOperationException(SR.GetString(SR.firstClassUsesOfSplice)))

    [<CompiledName("SpliceUntypedExpression")>]
    let (~%%) (expression: Microsoft.FSharp.Quotations.Expr) : 'T =
        ignore expression
        raise (InvalidOperationException(SR.GetString(SR.firstClassUsesOfSplice)))

    [<assembly: AutoOpen("Microsoft.FSharp")>]
    [<assembly: AutoOpen("Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators")>]
    [<assembly: AutoOpen("Microsoft.FSharp.Core")>]
    [<assembly: AutoOpen("Microsoft.FSharp.Collections")>]
    [<assembly: AutoOpen("Microsoft.FSharp.Control")>]
    [<assembly: AutoOpen("Microsoft.FSharp.Control.TaskBuilderExtensions.LowPriority")>]
    [<assembly: AutoOpen("Microsoft.FSharp.Control.TaskBuilderExtensions.LowPlusPriority")>]
    [<assembly: AutoOpen("Microsoft.FSharp.Control.TaskBuilderExtensions.MediumPriority")>]
    [<assembly: AutoOpen("Microsoft.FSharp.Control.TaskBuilderExtensions.HighPriority")>]
    [<assembly: AutoOpen("Microsoft.FSharp.Linq.QueryRunExtensions.LowPriority")>]
    [<assembly: AutoOpen("Microsoft.FSharp.Linq.QueryRunExtensions.HighPriority")>]
    do ()

    [<CompiledName("LazyPattern")>]
    let (|Lazy|) (input: Lazy<_>) =
        input.Force()

    let query = QueryBuilder()

namespace Microsoft.FSharp.Core.CompilerServices

open System
open System.Reflection
open Microsoft.FSharp.Core
open Microsoft.FSharp.Control
open Microsoft.FSharp.Quotations

/// <summary>Represents the product of two measure expressions when returned as a generic argument of a provided type.</summary>
[<Sealed>]
type MeasureProduct<'Measure1, 'Measure2>() = class end

/// <summary>Represents the inverse of a measure expressions when returned as a generic argument of a provided type.</summary>
[<Sealed>]
type MeasureInverse<'Measure> = class end

/// <summary>Represents the '1' measure expression when returned as a generic argument of a provided type.</summary>
[<Sealed>]
type MeasureOne = class end

[<AttributeUsage(AttributeTargets.Class ||| AttributeTargets.Struct, AllowMultiple = false)>]
type TypeProviderAttribute() =
    inherit System.Attribute()

[<AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)>]
type TypeProviderAssemblyAttribute(assemblyName: string) =
    inherit System.Attribute()
    new() = TypeProviderAssemblyAttribute(null)

    member _.AssemblyName = assemblyName

[<AttributeUsage(AttributeTargets.All, AllowMultiple = false)>]
type TypeProviderXmlDocAttribute(commentText: string) =
    inherit System.Attribute()

    member _.CommentText = commentText

[<AttributeUsage(AttributeTargets.All, AllowMultiple = false)>]
type TypeProviderDefinitionLocationAttribute() =
    inherit System.Attribute()
    let mutable filePath: string = null
    let mutable line: int = 0
    let mutable column: int = 0

    member _.FilePath
        with get () = filePath
        and set v = filePath <- v

    member _.Line
        with get () = line
        and set v = line <- v

    member _.Column
        with get () = column
        and set v = column <- v

[<AttributeUsage(AttributeTargets.Class
                 ||| AttributeTargets.Interface
                 ||| AttributeTargets.Struct
                 ||| AttributeTargets.Delegate,
                 AllowMultiple = false)>]
type TypeProviderEditorHideMethodsAttribute() =
    inherit System.Attribute()

/// <summary>Additional type attribute flags related to provided types</summary>
type TypeProviderTypeAttributes =
    | SuppressRelocate = 0x80000000
    | IsErased = 0x40000000

type TypeProviderConfig
    (systemRuntimeContainsType: string -> bool, getReferencedAssembliesOption: (unit -> string array) option) =
    let mutable resolutionFolder: string = null
    let mutable runtimeAssembly: string = null
    let mutable referencedAssemblies: string array = null
    let mutable temporaryFolder: string = null
    let mutable isInvalidationSupported: bool = false
    let mutable useResolutionFolderAtRuntime: bool = false
    let mutable systemRuntimeAssemblyVersion: System.Version = null

    new(systemRuntimeContainsType) = TypeProviderConfig(systemRuntimeContainsType, getReferencedAssembliesOption = None)

    new(systemRuntimeContainsType, getReferencedAssemblies) =
        TypeProviderConfig(systemRuntimeContainsType, getReferencedAssembliesOption = Some getReferencedAssemblies)

    member _.ResolutionFolder
        with get () = resolutionFolder
        and set v = resolutionFolder <- v

    member _.RuntimeAssembly
        with get () = runtimeAssembly
        and set v = runtimeAssembly <- v

    member _.ReferencedAssemblies
        with get () =
            match getReferencedAssembliesOption with
            | None -> referencedAssemblies
            | Some f -> f ()

        and set v =
            match getReferencedAssembliesOption with
            | None -> referencedAssemblies <- v
            | Some _ -> raise (InvalidOperationException())

    member _.TemporaryFolder
        with get () = temporaryFolder
        and set v = temporaryFolder <- v

    member _.IsInvalidationSupported
        with get () = isInvalidationSupported
        and set v = isInvalidationSupported <- v

    member _.IsHostedExecution
        with get () = useResolutionFolderAtRuntime
        and set v = useResolutionFolderAtRuntime <- v

    member _.SystemRuntimeAssemblyVersion
        with get () = systemRuntimeAssemblyVersion
        and set v = systemRuntimeAssemblyVersion <- v

    member _.SystemRuntimeContainsType(typeName: string) =
        systemRuntimeContainsType typeName

type IProvidedNamespace =

    abstract NamespaceName: string

    abstract GetNestedNamespaces: unit -> IProvidedNamespace array

    abstract GetTypes: unit -> Type array

    abstract ResolveTypeName: typeName: string -> (Type | null)

type ITypeProvider =
    inherit System.IDisposable

    abstract GetNamespaces: unit -> IProvidedNamespace array

    abstract GetStaticParameters: typeWithoutArguments: Type -> ParameterInfo array

    abstract ApplyStaticArguments:
        typeWithoutArguments: Type * typePathWithArguments: string array * staticArguments: objnull array -> Type

    abstract GetInvokerExpression: syntheticMethodBase: MethodBase * parameters: Expr array -> Expr

    [<CLIEvent>]
    abstract Invalidate: IEvent<System.EventHandler, System.EventArgs>

    abstract GetGeneratedAssemblyContents: assembly: System.Reflection.Assembly -> byte array

type ITypeProvider2 =
    abstract GetStaticParametersForMethod: methodWithoutArguments: MethodBase -> ParameterInfo array

    abstract ApplyStaticArgumentsForMethod:
        methodWithoutArguments: MethodBase * methodNameWithArguments: string * staticArguments: objnull array ->
            MethodBase
