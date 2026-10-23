[<AutoOpen>]
module TestUtils

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

open BatchIt
open Xunit


// The real-timer tests assume that calls made microseconds apart land in the same batch, which load from tests running
// in parallel could break.
[<assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)>]
do ()


/// A timer that FakeEnvironment fires when a test advances the clock to its due time.
type internal FakeTimer(now: unit -> TimeSpan, callback: unit -> unit) =

    member val Due: TimeSpan voption = ValueNone with get, set

    member _.Fire() = callback ()

    interface IBatchTimer with
        member this.Schedule delay = this.Due <- ValueSome(now () + delay)


/// Runs batchers deterministically: the clock only moves when a test advances it, timers fire while it advances, and
/// queued work runs immediately.
type internal FakeEnvironment() =

    let mutable now = TimeSpan.Zero
    let timers = ResizeArray<FakeTimer>()

    /// Advances the clock by ms, firing each timer that becomes due at its due time.
    member _.Advance(ms: int) =
        let target = now + TimeSpan.FromMilliseconds(float ms)

        let rec fireDueTimers () =
            let dueTimers =
                timers
                |> Seq.choose (fun timer ->
                    match timer.Due with
                    | ValueSome due when due <= target -> Some(due, timer)
                    | _ -> None
                )
                |> Seq.sortBy fst
                |> Seq.toList

            match dueTimers with
            | (due, timer) :: _ ->
                now <- max now due
                timer.Due <- ValueNone
                timer.Fire()
                fireDueTimers ()
            | [] -> ()

        fireDueTimers ()
        now <- target

    /// Fires every scheduled timer now, as when a timer fires early.
    member _.FireTimersEarly() =
        for timer in Seq.toList timers do
            if timer.Due.IsSome then
                timer.Due <- ValueNone
                timer.Fire()

    interface IBatchEnvironment with
        member _.Now() = now

        member _.CreateTimer callback =
            let timer = FakeTimer((fun () -> now), callback)
            timers.Add timer
            timer :> IBatchTimer

        member _.Queue work = work ()


/// A batched function that records the items of each call and returns Some(string item) for each item.
type internal Recorder<'extra>() =

    let calls = ConcurrentQueue<'extra * int[]>()

    member _.Calls = List.ofSeq calls

    member _.Items = calls |> Seq.map snd |> List.ofSeq

    member _.Batched (extra: 'extra) (items: int[]) =
        async {
            calls.Enqueue((extra, items))
            return items |> Array.map (fun item -> item, Some(string item))
        }


/// An item whose hash code throws for the value 13.
[<CustomEquality; NoComparison>]
type internal FragileItem = {
    Value: int
} with

    override this.GetHashCode() =
        if this.Value = 13 then
            raise (InvalidOperationException "Unhashable")
        else
            this.Value

    override this.Equals other =
        match other with
        | :? FragileItem as other -> this.Value = other.Value
        | _ -> false


/// A SynchronizationContext that runs posted callbacks one at a time on its own thread, like a UI thread.
type internal SingleThreadContext() as this =
    inherit SynchronizationContext()

    let callbacks = new BlockingCollection<unit -> unit>()

    let thread =
        Thread(
            (fun () ->
                SynchronizationContext.SetSynchronizationContext this

                for callback in callbacks.GetConsumingEnumerable() do
                    callback ()
            ),
            IsBackground = true
        )

    do thread.Start()

    member _.ThreadId = thread.ManagedThreadId

    override _.Post(callback, state) =
        callbacks.Add(fun () -> callback.Invoke state)

    interface IDisposable with
        member _.Dispose() = callbacks.CompleteAdding()


type internal Outcome<'a> =
    | Completed of 'a
    | Faulted of exn
    | Canceled


let internal ms (value: int) = TimeSpan.FromMilliseconds(float value)


/// Returns a function that calls a batcher with the fake environment and no extra value.
let internal fakeBatcher env timing maxSize (getBatched: unit -> int[] -> Async<(int * string option)[]>) =
    Batcher(env, getBatched, None, timing, maxSize).Get()


/// Starts the computation on the current thread until it first waits, so that a call is added to its batch before this
/// returns.
let internal start computation = Async.StartImmediateAsTask computation


/// Waits up to 10 seconds for the call and returns how it ended, so that tests fail instead of hanging.
let internal outcome (call: Task<'a>) =
    task {
        let! _ = Task.WhenAny(call :> Task, Task.Delay 10000)

        return
            if call.IsCanceled then
                Canceled
            elif call.IsFaulted then
                Faulted call.Exception.InnerException
            elif call.IsCompleted then
                Completed call.Result
            else
                failwith "The call did not complete within 10 seconds."
    }


/// Waits up to 10 seconds for the call and returns its result.
let internal result (call: Task<'a>) =
    task {
        match! outcome call with
        | Completed value -> return value
        | other -> return failwith $"Expected the call to complete, but it ended as %A{other}"
    }


/// Stands in for caller-specific execution context state such as Activity.Current.
let internal ambient = AsyncLocal<string>()


/// Runs f in a copy of the current execution context in which ambient has the given value, as code in a separate
/// request would.
let internal inContext name (f: unit -> 'a) : 'a =
    let result = ref Unchecked.defaultof<'a>

    ExecutionContext.Run(
        ExecutionContext.Capture(),
        (fun _ ->
            ambient.Value <- name
            result.Value <- f ()
        ),
        null
    )

    result.Value


/// Starts the computation in a separate caller's context (see inContext), running it synchronously until it first
/// waits.
let internal startAsCaller name computation =
    inContext name (fun () -> start computation)
