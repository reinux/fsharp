﻿open System

open System

type Disposable(id: int) =
    static let mutable disposedIds = Set.empty<int>
    static let mutable constructedIds = Set.empty<int>
    
    do constructedIds <- constructedIds.Add(id)
    
    member _.Id = id
    
    static member GetDisposed() = disposedIds
    static member GetConstructed() = constructedIds
    static member Reset() =
        disposedIds <- Set.empty
        constructedIds <- Set.empty
        
    interface IDisposable with
        member this.Dispose() = disposedIds <- disposedIds.Add(this.Id)

type DisposableBuilder() =
    member _.Using(resource: #IDisposable, f) =
        async {
            use res = resource
            return! f res
        }
        
    member _.Bind(disposable: Disposable, f) = async.Bind(async.Return(disposable), f)
    member _.Return(x) = async.Return x
    member _.ReturnFrom(x) = x
    member _.Bind(task, f) = async.Bind(task, f)

let counterDisposable = DisposableBuilder()

let testBindingPatterns() =
    Disposable.Reset()
    
    counterDisposable {
        use! _ = new Disposable(1)
        use! _ = new Disposable(2)
        use! (_) = new Disposable(3)
        use! (_) = new Disposable(4)
        return ()
    } |> Async.RunSynchronously
    
    let constructed = Disposable.GetConstructed()
    let disposed = Disposable.GetDisposed()
    let undisposed = constructed - disposed
    
    if not undisposed.IsEmpty then
        printfn $"Undisposed instances: %A{undisposed}"
        failwithf "Not all disposables were properly disposed"
    else
        printfn $"Success! All %d{constructed.Count} disposables were properly disposed"

testBindingPatterns()