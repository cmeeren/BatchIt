module Tests

open System
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

open Expecto
open Swensen.Unquote
open BatchIt


/// A timer that FakeEnvironment fires when a test advances the clock to its due time.
type private FakeTimer(now: unit -> TimeSpan, callback: unit -> unit) =

    member val Due: TimeSpan voption = ValueNone with get, set

    member _.Fire() = callback ()

    interface IBatchTimer with
        member this.Schedule delay = this.Due <- ValueSome(now () + delay)


/// Runs batchers deterministically: the clock only moves when a test advances it, timers fire while it advances, and
/// queued work runs immediately.
type private FakeEnvironment() =

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
type private Recorder<'extra>() =

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
type private FragileItem = {
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
type private SingleThreadContext() as this =
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


type private BatchedInts = int[] -> Async<(int * int option)[]>


type private Outcome<'a> =
    | Completed of 'a
    | Faulted of exn
    | Canceled


let private ms (value: int) = TimeSpan.FromMilliseconds(float value)


/// Returns a function that calls a batcher with the fake environment and no extra value.
let private fakeBatcher env timing maxSize (getBatched: unit -> int[] -> Async<(int * string option)[]>) =
    Batcher(env, getBatched, None, timing, maxSize).Get()


/// Starts the computation on the current thread until it first waits, so that a call is added to its batch before this
/// returns.
let private start computation = Async.StartImmediateAsTask computation


/// Waits up to 10 seconds for the task and returns how it ended, so that tests fail instead of hanging.
let private outcome (task: Task<'a>) =
    async {
        let! _ = Task.WhenAny(task :> Task, Task.Delay 10000) |> Async.AwaitTask

        return
            if task.IsCanceled then
                Canceled
            elif task.IsFaulted then
                Faulted task.Exception.InnerException
            elif task.IsCompleted then
                Completed task.Result
            else
                failtest "The task did not complete within 10 seconds."
    }


/// Returns the type and message of the exception of a faulted outcome.
let private fault (callOutcome: Outcome<'a>) =
    match callOutcome with
    | Faulted ex -> Some(ex.GetType(), ex.Message)
    | _ -> None


let private result (task: Task<'a>) =
    async {
        match! outcome task with
        | Completed value -> return value
        | other -> return failtestf "Expected the task to complete, but it ended as %A" other
    }


/// Stands in for caller-specific execution context state such as Activity.Current.
let private ambient = AsyncLocal<string>()


/// Runs f in a copy of the current execution context in which ambient has the given value, as code in a separate
/// request would.
let private inContext name (f: unit -> 'a) : 'a =
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
let private startAsCaller name computation =
    inContext name (fun () -> start computation)


/// Starts a call with a batched function that is created and then dropped in this method, so that only the pending
/// batch can keep the batcher alive.
[<MethodImpl(MethodImplOptions.NoInlining)>]
let private startCallWithUnreferencedBatcher () =
    let nonBatched =
        Batch.Create((fun (items: int[]) -> async { return items |> Array.map (fun item -> item, Some item) }), 100)

    start (nonBatched 1)


[<Tests>]
let tests =

    testSequenced
    <| testList "Tests" [


        testList "timing" [


            testCaseAsync "Throttled batches run waitMs after the first call"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder()
                let get = fakeBatcher env (Throttle(ms 100)) Int32.MaxValue recorder.Batched

                let call1 = start (get 1)
                env.Advance 50
                let call2 = start (get 2)
                env.Advance 49
                let itemsBeforeDue = recorder.Items
                env.Advance 1

                let! result1 = result call1
                let! result2 = result call2

                test <@ itemsBeforeDue = [] @>
                test <@ recorder.Items = [ [| 1; 2 |] ] @>
                test <@ (result1, result2) = (Some "1", Some "2") @>
            }


            testCaseAsync "Calls made after a batch has run go into the next batch"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder()
                let get = fakeBatcher env (Throttle(ms 100)) Int32.MaxValue recorder.Batched

                let call1 = start (get 1)
                env.Advance 100
                let call2 = start (get 2)
                env.Advance 99
                let itemsBeforeSecondDue = recorder.Items
                env.Advance 1

                let! _ = result call1
                let! result2 = result call2

                test <@ itemsBeforeSecondDue = [ [| 1 |] ] @>
                test <@ recorder.Items = [ [| 1 |]; [| 2 |] ] @>
                test <@ result2 = Some "2" @>
            }


            testCaseAsync "Debounced batches run minWaitAfterAddMs after the last call"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder()

                let get =
                    fakeBatcher env (Debounce(ms 30, ms 0, ms 1000)) Int32.MaxValue recorder.Batched

                let call1 = start (get 1)
                env.Advance 20
                let call2 = start (get 2)
                env.Advance 29
                let itemsBeforeDue = recorder.Items
                env.Advance 1

                let! _ = result call1
                let! _ = result call2

                test <@ itemsBeforeDue = [] @>
                test <@ recorder.Items = [ [| 1; 2 |] ] @>
            }


            testCaseAsync "Debounced batches wait at least minWaitMs after the first call"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder()

                let get =
                    fakeBatcher env (Debounce(ms 10, ms 50, ms 1000)) Int32.MaxValue recorder.Batched

                let call = start (get 1)
                env.Advance 49
                let itemsBeforeDue = recorder.Items
                env.Advance 1

                let! _ = result call

                test <@ itemsBeforeDue = [] @>
                test <@ recorder.Items = [ [| 1 |] ] @>
            }


            testCaseAsync "Debounced batches wait at most maxWaitMs after the first call"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder()

                let get =
                    fakeBatcher env (Debounce(ms 30, ms 0, ms 100)) Int32.MaxValue recorder.Batched

                let calls = ResizeArray()

                // Calls at 0, 20, ..., 80 ms keep extending the wait beyond maxWaitMs.
                for item in 1..5 do
                    if item > 1 then
                        env.Advance 20

                    calls.Add(start (get item))

                env.Advance 19
                let itemsBeforeDue = recorder.Items
                env.Advance 1

                for call in calls do
                    let! _ = result call
                    ()

                test <@ itemsBeforeDue = [] @>
                test <@ recorder.Items = [ [| 1..5 |] ] @>
            }


            testCaseAsync "With no wait, each call runs at once in its own batch"
            <| async {
                for timing in [ Throttle(ms 0); Debounce(ms 0, ms 0, ms 0) ] do
                    let env = FakeEnvironment()
                    let recorder = Recorder()
                    let get = fakeBatcher env timing Int32.MaxValue recorder.Batched

                    let call1 = start (get 1)
                    let call2 = start (get 2)

                    let! result1 = result call1
                    let! result2 = result call2

                    test <@ recorder.Items = [ [| 1 |]; [| 2 |] ] @>
                    test <@ (result1, result2) = (Some "1", Some "2") @>
            }


            testCaseAsync "Waits of a few milliseconds are honored"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder()
                let get = fakeBatcher env (Throttle(ms 2)) Int32.MaxValue recorder.Batched

                let call = start (get 1)
                let itemsAtStart = recorder.Items
                env.Advance 1
                let itemsBeforeDue = recorder.Items
                env.Advance 1

                let! _ = result call

                test <@ itemsAtStart = [] @>
                test <@ itemsBeforeDue = [] @>
                test <@ recorder.Items = [ [| 1 |] ] @>
            }


            testCaseAsync "Timer callbacks before a batch is due don't run it"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder()
                let get = fakeBatcher env (Throttle(ms 100)) Int32.MaxValue recorder.Batched

                let call = start (get 1)
                env.Advance 50
                env.FireTimersEarly()
                env.Advance 49
                let itemsBeforeDue = recorder.Items
                env.Advance 1

                let! _ = result call

                test <@ itemsBeforeDue = [] @>
                test <@ recorder.Items = [ [| 1 |] ] @>
            }


        ]


        testList "maxSize" [


            testCaseAsync "Full batches run at once"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder()
                let get = fakeBatcher env (Throttle(ms 1000)) 2 recorder.Batched

                let call1 = start (get 1)
                let call2 = start (get 2)

                let! result1 = result call1
                let! result2 = result call2

                test <@ recorder.Items = [ [| 1; 2 |] ] @>
                test <@ (result1, result2) = (Some "1", Some "2") @>
            }


            testCaseAsync "Calls made after a batch became full wait for their own batch"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder()
                let get = fakeBatcher env (Throttle(ms 1000)) 2 recorder.Batched

                start (get 1) |> ignore
                start (get 2) |> ignore
                env.Advance 500
                let call3 = start (get 3)
                env.Advance 999
                let itemsBeforeDue = recorder.Items
                env.Advance 1

                let! _ = result call3

                test <@ itemsBeforeDue = [ [| 1; 2 |] ] @>
                test <@ recorder.Items = [ [| 1; 2 |]; [| 3 |] ] @>
            }


            testCaseAsync "Repeated items count once toward maxSize"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder()
                let get = fakeBatcher env (Throttle(ms 1000)) 2 recorder.Batched

                let call1 = start (get 1)
                let call2 = start (get 1)
                let itemsBeforeFull = recorder.Items
                let call3 = start (get 2)

                let! result1 = result call1
                let! result2 = result call2
                let! result3 = result call3

                test <@ itemsBeforeFull = [] @>
                test <@ recorder.Items = [ [| 1; 2 |] ] @>
                test <@ (result1, result2, result3) = (Some "1", Some "1", Some "2") @>
            }


            testCaseAsync "maxSize counts the items of all extra values"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder<string>()
                let get = Batcher(env, recorder.Batched, None, Throttle(ms 1000), 2).Get

                let call1 = start (get "a" 1)
                let call2 = start (get "b" 1)

                let! _ = result call1
                let! _ = result call2

                test <@ List.sort recorder.Calls = [ "a", [| 1 |]; "b", [| 1 |] ] @>
            }


        ]


        testList "items and results" [


            testCaseAsync "Each distinct item is passed once, in the order first called"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder()
                let get = fakeBatcher env (Throttle(ms 100)) Int32.MaxValue recorder.Batched

                let calls = [ 3; 1; 3; 2 ] |> List.map (get >> start)
                env.Advance 100

                let! results = calls |> List.map result |> Async.Sequential

                test <@ recorder.Items = [ [| 3; 1; 2 |] ] @>
                test <@ results = [| Some "3"; Some "1"; Some "3"; Some "2" |] @>
            }


            testCaseAsync "Items missing from the results get the notFound value"
            <| async {
                let env = FakeEnvironment()

                let batched () (items: int[]) =
                    async {
                        return
                            items
                            |> Array.filter (fun item -> item % 2 = 1)
                            |> Array.map (fun item -> item, "odd")
                    }

                let get = Batcher(env, batched, "missing", Throttle(ms 100), Int32.MaxValue).Get()

                let call1 = start (get 1)
                let call2 = start (get 2)
                env.Advance 100

                let! result1 = result call1
                let! result2 = result call2

                test <@ (result1, result2) = ("odd", "missing") @>
            }


            testCaseAsync "When the results contain an item more than once, the last value is used"
            <| async {
                let env = FakeEnvironment()

                let batched () (_: int[]) =
                    async { return [| 1, Some "first"; 1, Some "last" |] }

                let get = fakeBatcher env (Throttle(ms 0)) Int32.MaxValue batched
                let! result1 = start (get 1) |> result

                test <@ result1 = Some "last" @>
            }


            testCaseAsync "Items are matched with F# structural equality"
            <| async {
                let env = FakeEnvironment()
                let calls = ConcurrentQueue<int[][]>()

                let batched () (items: int[][]) =
                    async {
                        calls.Enqueue items
                        // Return equal but different array instances
                        return items |> Array.map (fun item -> Array.copy item, Some(Array.sum item))
                    }

                let get = Batcher(env, batched, None, Throttle(ms 100), Int32.MaxValue).Get()

                let call1 = start (get [| 1; 2 |])
                let call2 = start (get [| 1; 2 |])
                env.Advance 100

                let! result1 = result call1
                let! result2 = result call2

                test <@ List.ofSeq calls = [ [| [| 1; 2 |] |] ] @>
                test <@ (result1, result2) = (Some 3, Some 3) @>
            }


            testCaseAsync "Null items and extra values are supported"
            <| async {
                let env = FakeEnvironment()

                let batched (extra: string) (items: string[]) =
                    async { return items |> Array.map (fun item -> item, Some $"%s{extra}/%s{item}") }

                let get = Batcher(env, batched, None, Throttle(ms 100), Int32.MaxValue).Get

                let call1 = start (get null null)
                let call2 = start (get null "x")
                let call3 = start (get "e" null)
                env.Advance 100

                let! result1 = result call1
                let! result2 = result call2
                let! result3 = result call3

                test <@ (result1, result2, result3) = (Some "/", Some "/x", Some "e/") @>
            }


        ]


        testList "extra" [


            testCaseAsync "Calls with different extra values share a batch, with one batched call per extra value"
            <| async {
                let env = FakeEnvironment()
                let recorder = Recorder<string>()
                let get = Batcher(env, recorder.Batched, None, Throttle(ms 100), Int32.MaxValue).Get

                let calls =
                    [ "a", 1; "b", 2; "a", 3 ]
                    |> List.map (fun (extra, item) -> start (get extra item))

                env.Advance 99
                let callsBeforeDue = recorder.Calls
                env.Advance 1

                for call in calls do
                    let! _ = result call
                    ()

                test <@ callsBeforeDue = [] @>
                test <@ List.sort recorder.Calls = [ "a", [| 1; 3 |]; "b", [| 2 |] ] @>
            }


            testCaseAsync "Results are kept separate per extra value"
            <| async {
                let env = FakeEnvironment()

                let batched (extra: string) (items: int[]) =
                    async { return items |> Array.map (fun item -> item, Some $"%s{extra}%i{item}") }

                let get = Batcher(env, batched, None, Throttle(ms 100), Int32.MaxValue).Get

                let callA = start (get "a" 1)
                let callB = start (get "b" 1)
                env.Advance 100

                let! resultA = result callA
                let! resultB = result callB

                test <@ (resultA, resultB) = (Some "a1", Some "b1") @>
            }


            testCaseAsync "A slow batched call for one extra value doesn't hold up callers with other values"
            <| async {
                let env = FakeEnvironment()

                let releaseSlow =
                    TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

                let batched (extra: string) (items: int[]) =
                    async {
                        if extra = "slow" then
                            do! releaseSlow.Task |> Async.AwaitTask

                        return items |> Array.map (fun item -> item, Some extra)
                    }

                let get = Batcher(env, batched, None, Throttle(ms 1000), 2).Get

                let slowCall = start (get "slow" 1)
                let fastCall = start (get "fast" 2)

                let! fastResult = result fastCall
                let slowCompletedBeforeRelease = slowCall.IsCompleted
                releaseSlow.SetResult()
                let! slowResult = result slowCall

                test <@ fastResult = Some "fast" @>
                test <@ not slowCompletedBeforeRelease @>
                test <@ slowResult = Some "slow" @>
            }


        ]


        testList "failures" [


            testCaseAsync "Callers get the exception that the batched function raised"
            <| async {
                let env = FakeEnvironment()

                let batched () (_: int[]) : Async<(int * string option)[]> =
                    async { return raise (InvalidOperationException "Boom") }

                let get = fakeBatcher env (Throttle(ms 0)) Int32.MaxValue batched
                let! callOutcome = start (get 1) |> outcome

                test <@ fault callOutcome = Some(typeof<InvalidOperationException>, "Boom") @>
            }


            testCaseAsync "Callers get exceptions that the batched function throws before returning its async"
            <| async {
                let env = FakeEnvironment()

                let batched () (_: int[]) : Async<(int * string option)[]> =
                    raise (InvalidOperationException "Boom")

                let get = fakeBatcher env (Throttle(ms 0)) Int32.MaxValue batched
                let! callOutcome = start (get 1) |> outcome

                test <@ fault callOutcome = Some(typeof<InvalidOperationException>, "Boom") @>
            }


            testCaseAsync "A failure for one extra value only fails the callers with that value"
            <| async {
                let env = FakeEnvironment()

                let batched (extra: string) (items: int[]) =
                    async {
                        if extra = "bad" then
                            raise (InvalidOperationException "Boom")

                        return items |> Array.map (fun item -> item, Some item)
                    }

                let get = Batcher(env, batched, None, Throttle(ms 100), Int32.MaxValue).Get

                let goodCall = start (get "good" 1)
                let badCall = start (get "bad" 2)
                env.Advance 100

                let! goodOutcome = outcome goodCall
                let! badOutcome = outcome badCall

                test <@ goodOutcome = Completed(Some 1) @>
                test <@ fault badOutcome = Some(typeof<InvalidOperationException>, "Boom") @>
            }


            testCaseAsync "When the batched function is canceled, its callers are canceled"
            <| async {
                let env = FakeEnvironment()

                let batched () (_: int[]) : Async<(int * string option)[]> =
                    Async.FromContinuations(fun (_, _, onCancel) -> onCancel (OperationCanceledException()))

                let get = fakeBatcher env (Throttle(ms 0)) Int32.MaxValue batched
                let! callOutcome = start (get 1) |> outcome

                test <@ callOutcome = Canceled @>
            }


            testCaseAsync "Calls whose item can't be hashed fail alone and don't start a batch"
            <| async {
                let env = FakeEnvironment()
                let calls = ConcurrentQueue<int list>()

                let batched () (items: FragileItem[]) =
                    async {
                        calls.Enqueue(items |> Array.map (fun item -> item.Value) |> List.ofArray)
                        return items |> Array.map (fun item -> item, Some item.Value)
                    }

                let get = Batcher(env, batched, None, Throttle(ms 100), Int32.MaxValue).Get()

                let! failedOutcome = start (get { Value = 13 }) |> outcome
                env.Advance 50
                let call1 = start (get { Value = 1 })
                let! failedAgainOutcome = start (get { Value = 13 }) |> outcome
                let call2 = start (get { Value = 2 })
                env.Advance 99
                let callsBeforeDue = List.ofSeq calls
                env.Advance 1

                let! result1 = result call1
                let! result2 = result call2

                let unhashable = Some(typeof<InvalidOperationException>, "Unhashable")
                test <@ (fault failedOutcome, fault failedAgainOutcome) = (unhashable, unhashable) @>
                test <@ callsBeforeDue = [] @>
                test <@ List.ofSeq calls = [ [ 1; 2 ] ] @>
                test <@ (result1, result2) = (Some 1, Some 2) @>
            }


            testCaseAsync "Calls whose extra value can't be hashed fail alone, and groups left empty aren't run"
            <| async {
                let env = FakeEnvironment()
                let calls = ConcurrentQueue<int * int list>()

                let batched (extra: FragileItem) (items: FragileItem[]) =
                    async {
                        calls.Enqueue((extra.Value, items |> Array.map (fun item -> item.Value) |> List.ofArray))
                        return items |> Array.map (fun item -> item, Some item.Value)
                    }

                let get = Batcher(env, batched, None, Throttle(ms 100), Int32.MaxValue).Get

                let call = start (get { Value = 1 } { Value = 1 })
                let! unhashableExtraOutcome = start (get { Value = 13 } { Value = 2 }) |> outcome
                // Adds a group for extra value 2 before its item fails, which leaves the group empty.
                let! unhashableItemOutcome = start (get { Value = 2 } { Value = 13 }) |> outcome
                env.Advance 100

                let! callResult = result call

                let unhashable = Some(typeof<InvalidOperationException>, "Unhashable")
                test <@ (fault unhashableExtraOutcome, fault unhashableItemOutcome) = (unhashable, unhashable) @>
                test <@ List.ofSeq calls = [ 1, [ 1 ] ] @>
                test <@ callResult = Some 1 @>
            }


        ]


        testList "execution context" [


            testCaseAsync "Callers continue in their own execution context"
            <| async {
                let batched (items: int[]) =
                    async { return items |> Array.map (fun item -> item, Some item) }

                let nonBatched =
                    inContext "creator" (fun () -> Batch.Create(batched, 10000, maxSize = 2))

                let callAndGetAmbient item =
                    async {
                        let! _ = nonBatched item
                        return ambient.Value
                    }

                let callA = startAsCaller "A" (callAndGetAmbient 1)
                let callB = startAsCaller "B" (callAndGetAmbient 2)

                let! ambientA = result callA
                let! ambientB = result callB

                test <@ (ambientA, ambientB) = ("A", "B") @>
            }


            testCaseAsync "Callers continue in their own SynchronizationContext"
            <| async {
                let batched (items: int[]) =
                    async { return items |> Array.map (fun item -> item, Some item) }

                let nonBatched = Batch.Create(batched, 10)
                use context = new SingleThreadContext()
                let threadAfterCall = TaskCompletionSource<int>()

                context.Post(
                    (fun _ ->
                        Async.StartImmediate(
                            async {
                                let! _ = nonBatched 1
                                threadAfterCall.SetResult Thread.CurrentThread.ManagedThreadId
                            }
                        )
                    ),
                    null
                )

                let! threadAfterCall = result threadAfterCall.Task
                let contextThread = context.ThreadId
                test <@ threadAfterCall = contextThread @>
            }


            testList "The batched function runs without the callers' execution context" [
                for name, create in
                    [
                        "when scheduled", (fun (batched: BatchedInts) -> Batch.Create(batched, 10))
                        "when full", (fun (batched: BatchedInts) -> Batch.Create(batched, 10000, maxSize = 1))
                        "without wait", (fun (batched: BatchedInts) -> Batch.Create(batched, 0))
                    ] do
                    testCaseAsync name
                    <| async {
                        let ambientInBatch = ConcurrentQueue<string>()

                        let batched (items: int[]) =
                            async {
                                ambientInBatch.Enqueue ambient.Value
                                return items |> Array.map (fun item -> item, Some item)
                            }

                        let nonBatched = inContext "creator" (fun () -> create batched)
                        let! _ = startAsCaller "caller" (nonBatched 1) |> result

                        test <@ List.ofSeq ambientInBatch = [ null ] @>
                    }
            ]


        ]


        testList "system environment" [


            testCaseAsync "Throttled batches run with real timers"
            <| async {
                let recorder = Recorder()
                let nonBatched = Batch.Create(recorder.Batched(), 200)

                let calls = [ 1..5 ] |> List.map (nonBatched >> start)
                let! results = calls |> List.map result |> Async.Sequential

                test <@ recorder.Items = [ [| 1..5 |] ] @>
                test <@ results = [| for i in 1..5 -> Some(string i) |] @>
            }


            testCaseAsync "Debounced batches run with real timers"
            <| async {
                let recorder = Recorder()
                let nonBatched = Batch.Create(recorder.Batched(), 100, 200, 2000)

                let calls = [ 1..5 ] |> List.map (nonBatched >> start)
                let! results = calls |> List.map result |> Async.Sequential

                test <@ recorder.Items = [ [| 1..5 |] ] @>
                test <@ results = [| for i in 1..5 -> Some(string i) |] @>
            }


            testCaseAsync "Concurrent calls from many threads each get their own result"
            <| async {
                let maxSize = 7
                let batches = ConcurrentQueue<int[]>()

                let batched (extra: string) (items: int[]) =
                    async {
                        batches.Enqueue items
                        return items |> Array.map (fun item -> item, Some $"%s{extra}%i{item}")
                    }

                let nonBatched = Batch.Create(batched, 1, 2, 5, maxSize = maxSize)

                let startCalls thread =
                    Task.Run<(string * int * Task<string option>) list>(
                        Func<_>(fun () -> [
                            for i in 1..200 do
                                let extra = if i % 2 = 0 then "a" else "b"
                                // Overlapping items across threads, so that calls share and repeat items.
                                let item = (thread * 31 + i) % 50
                                extra, item, Async.StartAsTask(nonBatched extra item)
                        ])
                    )

                let! callsPerThread = [ for thread in 1..16 -> startCalls thread ] |> Task.WhenAll |> Async.AwaitTask
                let calls = List.concat callsPerThread
                let! results = calls |> List.map (fun (_, _, call) -> result call) |> Async.Sequential

                let wrongResults =
                    List.zip calls (List.ofArray results)
                    |> List.filter (fun ((extra, item, _), result) -> result <> Some $"%s{extra}%i{item}")

                let invalidBatches =
                    batches
                    |> Seq.filter (fun items -> items.Length > maxSize || (Array.distinct items).Length < items.Length)
                    |> List.ofSeq

                test <@ wrongResults.IsEmpty @>
                test <@ invalidBatches.IsEmpty @>
            }


            testCaseAsync "Callers resume concurrently, not one after another on the batch's thread"
            <| async {
                use secondResumed = new ManualResetEventSlim()

                let batched (items: int[]) =
                    async { return items |> Array.map (fun item -> item, Some item) }

                let nonBatched = Batch.Create(batched, 10000, maxSize = 2)

                let first =
                    start (
                        async {
                            let! _ = nonBatched 1
                            // Blocks the thread, so the second caller can only resume on another thread.
                            return secondResumed.Wait 5000
                        }
                    )

                let second =
                    start (
                        async {
                            let! _ = nonBatched 2
                            secondResumed.Set()
                        }
                    )

                let! firstSawSecondResume = result first
                do! result second

                test <@ firstSawSecondResume @>
            }


            testCaseAsync "Pending batches run even if nothing else references the batched function"
            <| async {
                let call = startCallWithUnreferencedBatcher ()

                GC.Collect()
                GC.WaitForPendingFinalizers()
                GC.Collect()

                let! callResult = result call
                test <@ callResult = Some 1 @>
            }


        ]


        testList "overloads" [


            testCaseAsync "Each overload returns the notFound value of its result type for missing items"
            <| async {
                // Returns value for item 1 only.
                let onlyFirst value (items: int[]) =
                    async { return items |> Array.filter ((=) 1) |> Array.map (fun item -> item, value) }

                let withExtra value (_: string) items = onlyFirst value items

                let check name (get: int -> Async<'b>) (found: 'b) (missing: 'b) =
                    async {
                        let! foundResult = get 1
                        let! missingResult = get 2

                        return
                            if (foundResult, missingResult) = (found, missing) then
                                None
                            else
                                Some name
                    }

                let unitCalls = ConcurrentQueue<int[]>()
                let runUnit (items: int[]) = async { unitCalls.Enqueue items }
                let runUnitWithExtra (_: string) items = runUnit items

                let! failures =
                    [
                        check "option" (Batch.Create(onlyFirst (Some "x"), 0)) (Some "x") None
                        check "voption" (Batch.Create(onlyFirst (ValueSome "x"), 0)) (ValueSome "x") ValueNone
                        check "list" (Batch.Create(onlyFirst [ "x" ], 0)) [ "x" ] []
                        check "array" (Batch.Create(onlyFirst [| "x" |], 0)) [| "x" |] [||]
                        check "notFound" (Batch.Create(onlyFirst 5, notFound = -1, waitMs = 0)) 5 -1
                        check "unit" (Batch.Create(runUnit, 0)) () ()

                        check "option, extra" (Batch.Create (withExtra (Some "x"), 0) "e") (Some "x") None

                        check
                            "voption, extra"
                            (Batch.Create (withExtra (ValueSome "x"), 0) "e")
                            (ValueSome "x")
                            ValueNone

                        check "list, extra" (Batch.Create (withExtra [ "x" ], 0) "e") [ "x" ] []
                        check "array, extra" (Batch.Create (withExtra [| "x" |], 0) "e") [| "x" |] [||]
                        check "notFound, extra" (Batch.Create (withExtra 5, notFound = -1, waitMs = 0) "e") 5 -1
                        check "unit, extra" (Batch.Create (runUnitWithExtra, 0) "e") () ()

                        check "option, debounced" (Batch.Create(onlyFirst (Some "x"), 0, 0, 0)) (Some "x") None

                        check
                            "voption, debounced"
                            (Batch.Create(onlyFirst (ValueSome "x"), 0, 0, 0))
                            (ValueSome "x")
                            ValueNone

                        check "list, debounced" (Batch.Create(onlyFirst [ "x" ], 0, 0, 0)) [ "x" ] []
                        check "array, debounced" (Batch.Create(onlyFirst [| "x" |], 0, 0, 0)) [| "x" |] [||]

                        check
                            "notFound, debounced"
                            (Batch.Create(
                                onlyFirst 5,
                                notFound = -1,
                                minWaitAfterAddMs = 0,
                                minWaitMs = 0,
                                maxWaitMs = 0
                            ))
                            5
                            -1

                        check "unit, debounced" (Batch.Create(runUnit, 0, 0, 0)) () ()

                        check
                            "option, extra, debounced"
                            (Batch.Create (withExtra (Some "x"), 0, 0, 0) "e")
                            (Some "x")
                            None

                        check
                            "voption, extra, debounced"
                            (Batch.Create (withExtra (ValueSome "x"), 0, 0, 0) "e")
                            (ValueSome "x")
                            ValueNone

                        check "list, extra, debounced" (Batch.Create (withExtra [ "x" ], 0, 0, 0) "e") [ "x" ] []

                        check "array, extra, debounced" (Batch.Create (withExtra [| "x" |], 0, 0, 0) "e") [| "x" |] [||]

                        check
                            "notFound, extra, debounced"
                            (Batch.Create
                                (withExtra 5, notFound = -1, minWaitAfterAddMs = 0, minWaitMs = 0, maxWaitMs = 0)
                                "e")
                            5
                            -1

                        check "unit, extra, debounced" (Batch.Create (runUnitWithExtra, 0, 0, 0) "e") () ()
                    ]
                    |> Async.Sequential

                let failures = failures |> Array.choose id
                test <@ failures = [||] @>
                test <@ unitCalls |> Seq.sumBy Array.length = 8 @>
            }


            testCase "Invalid configurations are rejected"
            <| fun () ->
                let batched (items: int[]) =
                    async { return items |> Array.map (fun item -> item, Some item) }

                let batchedWithExtra (_: string) (items: int[]) = batched items

                let rejectedParam (create: unit -> 'a) =
                    try
                        create () |> ignore
                        "(accepted)"
                    with :? ArgumentException as ex ->
                        ex.ParamName

                let rejected = [
                    rejectedParam (fun () -> Batch.Create(batched, -1))
                    rejectedParam (fun () -> Batch.Create(batched, 10, maxSize = 0))
                    rejectedParam (fun () -> Batch.Create(batched, -1, 10, 20))
                    rejectedParam (fun () -> Batch.Create(batched, 5, -1, 20))
                    rejectedParam (fun () -> Batch.Create(batched, 5, 20, 10))
                    rejectedParam (fun () -> Batch.Create(batched, 5, 0, -1))
                    rejectedParam (fun () -> Batch.Create(batched, 5, 10, 20, maxSize = 0))
                    rejectedParam (fun () -> Batch.Create(batchedWithExtra, -1))
                    rejectedParam (fun () -> Batch.Create(batchedWithExtra, 5, 20, 10))
                ]

                test
                    <@
                        rejected = [
                            "waitMs"
                            "maxSize"
                            "minWaitAfterAddMs"
                            "minWaitMs"
                            "maxWaitMs"
                            "maxWaitMs"
                            "maxSize"
                            "waitMs"
                            "maxWaitMs"
                        ]
                    @>


        ]

    ]
