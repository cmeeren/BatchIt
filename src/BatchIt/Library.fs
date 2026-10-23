namespace BatchIt

open System
open System.Collections.Generic
open System.Diagnostics
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

[<assembly: InternalsVisibleTo("BatchIt.Tests")>]
do ()


/// Wraps a dictionary key so that null keys are allowed and keys are compared with F# structural equality.
[<Struct>]
type internal Key<'T> = Key of 'T


/// A timer that calls a fixed callback once when due.
type internal IBatchTimer =
    /// Schedules the callback to run after the delay, replacing any earlier schedule.
    abstract Schedule: delay: TimeSpan -> unit


/// The clock, timers and thread pool that a batcher uses.
type internal IBatchEnvironment =
    /// Returns the current time on a monotonic clock.
    abstract Now: unit -> TimeSpan

    /// Creates an unscheduled timer for the callback.
    abstract CreateTimer: callback: (unit -> unit) -> IBatchTimer

    /// Runs the work on the thread pool without the current execution context.
    abstract Queue: work: (unit -> unit) -> unit


[<Sealed>]
type internal SystemBatchTimer(callback: unit -> unit) =

    // Created without the current execution context, so that the callback doesn't run in the context of whichever
    // caller created the batcher. While scheduled, the timer stays alive through its callback, which references the
    // batcher and thereby this timer, so pending batches run even if nothing else references the batcher.
    let timer =
        let create () =
            new Timer((fun _ -> callback ()), null, Timeout.Infinite, Timeout.Infinite)

        if ExecutionContext.IsFlowSuppressed() then
            create ()
        else
            use _ = ExecutionContext.SuppressFlow()
            create ()

    interface IBatchTimer with
        member _.Schedule delay =
            timer.Change(int64 (Math.Ceiling delay.TotalMilliseconds), int64 Timeout.Infinite)
            |> ignore


[<Sealed>]
type internal SystemBatchEnvironment() =

    let stopwatch = Stopwatch.StartNew()

    interface IBatchEnvironment with
        member _.Now() = stopwatch.Elapsed

        member _.CreateTimer callback =
            SystemBatchTimer callback :> IBatchTimer

        member _.Queue work =
            ThreadPool.UnsafeQueueUserWorkItem(WaitCallback(fun _ -> work ()), null)
            |> ignore


/// When a pending batch is due.
type internal BatchTiming =
    /// A fixed wait after the first item.
    | Throttle of wait: TimeSpan
    /// minWaitAfterAdd after the last item, but at least minWait and at most maxWait after the first item.
    | Debounce of minWaitAfterAdd: TimeSpan * minWait: TimeSpan * maxWait: TimeSpan


/// The items of a batch that have the same 'extra value, and their results.
type internal BatchGroup<'extra, 'a, 'b> = {
    Extra: 'extra
    Items: ResizeArray<'a>
    ItemKeys: HashSet<Key<'a>>
    Results: TaskCompletionSource<Dictionary<Key<'a>, 'b>>
}


type internal PendingBatch<'extra, 'a, 'b> = {
    FirstAdd: TimeSpan
    mutable LastAdd: TimeSpan
    mutable ItemCount: int
    Groups: Dictionary<Key<'extra>, BatchGroup<'extra, 'a, 'b>>
}


[<AutoOpen>]
module internal TaskHelpers =

    /// Like Async.AwaitTask, but raises the task's own exception instead of an AggregateException.
    let awaitTask (task: Task<'T>) : Async<'T> =
        async {
            // Async.AwaitTask continues in the caller's SynchronizationContext (if any) and execution context. Awaiting
            // this continuation instead of the task itself never raises, so the task's own exception can be raised.
            do!
                task.ContinueWith(
                    Action<Task<'T>>(ignore),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                )
                |> Async.AwaitTask

            return!
                Async.FromContinuations(fun (onSuccess, onError, onCancel) ->
                    if task.IsCanceled then
                        onCancel (OperationCanceledException())
                    elif task.IsFaulted then
                        let ex = task.Exception

                        if ex.InnerExceptions.Count = 1 then
                            onError ex.InnerExceptions[0]
                        else
                            onError ex
                    else
                        onSuccess task.Result
                )
        }


/// Collects calls into batches and runs the batched function for them.
[<Sealed>]
type internal Batcher<'extra, 'a, 'b when 'extra: equality and 'a: equality>
    (
        env: IBatchEnvironment,
        getBatched: 'extra -> 'a[] -> Async<('a * 'b)[]>,
        notFound: 'b,
        timing: BatchTiming,
        maxSize: int
    ) =

    let sync = obj ()
    let mutable pending: PendingBatch<'extra, 'a, 'b> voption = ValueNone
    let mutable timer = Unchecked.defaultof<IBatchTimer>

    let dueTime (batch: PendingBatch<'extra, 'a, 'b>) =
        match timing with
        | Throttle wait -> batch.FirstAdd + wait
        | Debounce(minWaitAfterAdd, minWait, maxWait) ->
            max (batch.LastAdd + minWaitAfterAdd) (batch.FirstAdd + minWait)
            |> min (batch.FirstAdd + maxWait)

    let runGroup (group: BatchGroup<'extra, 'a, 'b>) =
        let setResults (results: ('a * 'b)[]) =
            try
                let resultsByItem = Dictionary(results.Length)

                for item, value in results do
                    resultsByItem[Key item] <- value

                group.Results.TrySetResult resultsByItem |> ignore
            with ex ->
                group.Results.TrySetException ex |> ignore

        try
            Async.StartWithContinuations(
                getBatched group.Extra (group.Items.ToArray()),
                setResults,
                (fun ex -> group.Results.TrySetException ex |> ignore),
                (fun _ -> group.Results.TrySetCanceled() |> ignore),
                CancellationToken.None
            )
        with ex ->
            group.Results.TrySetException ex |> ignore

    let run (batch: PendingBatch<'extra, 'a, 'b>) =
        for group in batch.Groups.Values do
            // A group is empty if adding its only item failed.
            if group.Items.Count > 0 then
                env.Queue(fun () -> runGroup group)

    let onTimer () =
        let dueBatch =
            lock
                sync
                (fun () ->
                    match pending with
                    | ValueNone -> ValueNone
                    | ValueSome batch ->
                        let now = env.Now()
                        let due = dueTime batch

                        if due <= now then
                            pending <- ValueNone
                            ValueSome batch
                        else
                            // A later item extended the wait, the timer fired early, or the timer was set for an earlier
                            // batch that ran when it became full.
                            timer.Schedule(due - now)
                            ValueNone
                )

        match dueBatch with
        | ValueSome batch -> run batch
        | ValueNone -> ()

    do timer <- env.CreateTimer onTimer

    /// Adds the item to the pending batch, starting a new batch if needed, and returns the results for its group.
    let add (extra: 'extra) (item: 'a) =
        let struct (results, fullOrDueBatch) =
            lock
                sync
                (fun () ->
                    let now = env.Now()

                    let batch, isNew =
                        match pending with
                        | ValueSome batch -> batch, false
                        | ValueNone ->
                            {
                                FirstAdd = now
                                LastAdd = now
                                ItemCount = 0
                                Groups = Dictionary()
                            },
                            true

                    // Equality and hashing of items and extra values may throw. This leaves the pending batch as it was,
                    // apart from possibly an empty group.
                    let group =
                        match batch.Groups.TryGetValue(Key extra) with
                        | true, group -> group
                        | false, _ ->
                            let group = {
                                Extra = extra
                                Items = ResizeArray()
                                ItemKeys = HashSet()
                                Results = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                            }

                            batch.Groups[Key extra] <- group
                            group

                    if group.ItemKeys.Add(Key item) then
                        group.Items.Add item
                        batch.ItemCount <- batch.ItemCount + 1

                    batch.LastAdd <- now
                    let due = dueTime batch

                    if batch.ItemCount >= maxSize || due <= now then
                        pending <- ValueNone
                        struct (group.Results.Task, ValueSome batch)
                    else
                        if isNew then
                            pending <- ValueSome batch
                            timer.Schedule(due - now)

                        struct (group.Results.Task, ValueNone)
                )

        match fullOrDueBatch with
        | ValueSome batch -> run batch
        | ValueNone -> ()

        results

    /// Returns the result for the item from the batch it's added to.
    member _.Get (extra: 'extra) (item: 'a) : Async<'b> =
        async {
            let! resultsByItem = add extra item |> awaitTask

            match resultsByItem.TryGetValue(Key item) with
            | true, value -> return value
            | false, _ -> return notFound
        }


[<AutoOpen>]
module private Create =

    let private fromMs (ms: int) =
        TimeSpan.FromTicks(int64 ms * TimeSpan.TicksPerMillisecond)

    let private validateMaxSize maxSize =
        if maxSize < 1 then
            invalidArg "maxSize" "Must be at least 1."

    let private createBatched getBatched notFound timing maxSize : 'extra -> 'a -> Async<'b> =
        Batcher(SystemBatchEnvironment(), getBatched, notFound, timing, maxSize).Get

    let createThrottledWithExtra getBatched notFound waitMs maxSize =
        if waitMs < 0 then
            invalidArg "waitMs" "Must be non-negative."

        validateMaxSize maxSize
        createBatched getBatched notFound (Throttle(fromMs waitMs)) maxSize

    let createDebouncedWithExtra getBatched notFound minWaitAfterAddMs minWaitMs maxWaitMs maxSize =
        if minWaitAfterAddMs < 0 then
            invalidArg "minWaitAfterAddMs" "Must be non-negative."

        if minWaitMs < 0 then
            invalidArg "minWaitMs" "Must be non-negative."

        if maxWaitMs < minWaitMs then
            invalidArg "maxWaitMs" "Must be at least minWaitMs."

        validateMaxSize maxSize

        createBatched
            getBatched
            notFound
            (Debounce(fromMs minWaitAfterAddMs, fromMs minWaitMs, fromMs maxWaitMs))
            maxSize

    let createThrottled (getBatched: 'a[] -> Async<('a * 'b)[]>) notFound waitMs maxSize : 'a -> Async<'b> =
        createThrottledWithExtra (fun () -> getBatched) notFound waitMs maxSize ()

    let createDebounced
        (getBatched: 'a[] -> Async<('a * 'b)[]>)
        notFound
        minWaitAfterAddMs
        minWaitMs
        maxWaitMs
        maxSize
        : 'a -> Async<'b> =
        createDebouncedWithExtra (fun () -> getBatched) notFound minWaitAfterAddMs minWaitMs maxWaitMs maxSize ()


[<AbstractClass; Sealed>]
type Batch private () =

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return None for items that are not found.
    static member Create(getBatched: 'a[] -> Async<('a * 'b option)[]>, waitMs, ?maxSize) : 'a -> Async<'b option> =
        createThrottled getBatched None waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return None for items that are not found.
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create
        (
            getBatched: 'extra -> 'a[] -> Async<('a * 'b option)[]>,
            waitMs,
            ?maxSize
        ) : 'extra -> 'a -> Async<'b option> =
        createThrottledWithExtra getBatched None waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least minWaitMs and at most maxWaitMs after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified). The returned
    /// non-batched function will return None for items that are not found.
    static member Create
        (
            getBatched: 'a[] -> Async<('a * 'b option)[]>,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'a -> Async<'b option> =
        createDebounced getBatched None minWaitAfterAddMs minWaitMs maxWaitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least minWaitMs and at most maxWaitMs after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified). The returned
    /// non-batched function will return None for items that are not found.
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create
        (
            getBatched: 'extra -> 'a[] -> Async<('a * 'b option)[]>,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'extra -> 'a -> Async<'b option> =
        createDebouncedWithExtra
            getBatched
            None
            minWaitAfterAddMs
            minWaitMs
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified).
    static member Create(runBatched: 'a[] -> Async<unit>, waitMs, ?maxSize) : 'a -> Async<unit> =
        let getBatched args =
            async {
                do! runBatched args
                return args |> Array.map (fun x -> x, ())
            }

        createThrottled getBatched () waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified).
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create(runBatched: 'extra -> 'a[] -> Async<unit>, waitMs, ?maxSize) : 'extra -> 'a -> Async<unit> =
        let getBatched extra args =
            async {
                do! runBatched extra args
                return args |> Array.map (fun x -> x, ())
            }

        createThrottledWithExtra getBatched () waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least minWaitMs and at most maxWaitMs after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified).
    static member Create
        (
            runBatched: 'a[] -> Async<unit>,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'a -> Async<unit> =
        let getBatched args =
            async {
                do! runBatched args
                return args |> Array.map (fun x -> x, ())
            }

        createDebounced getBatched () minWaitAfterAddMs minWaitMs maxWaitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least minWaitMs and at most maxWaitMs after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified).
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create
        (
            runBatched: 'extra -> 'a[] -> Async<unit>,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'extra -> 'a -> Async<unit> =
        let getBatched extra args =
            async {
                do! runBatched extra args
                return args |> Array.map (fun x -> x, ())
            }

        createDebouncedWithExtra getBatched () minWaitAfterAddMs minWaitMs maxWaitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return ValueNone for items that are not found.
    static member Create(getBatched: 'a[] -> Async<('a * 'b voption)[]>, waitMs, ?maxSize) : 'a -> Async<'b voption> =
        createThrottled getBatched ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return ValueNone for items that are not found.
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create
        (
            getBatched: 'extra -> 'a[] -> Async<('a * 'b voption)[]>,
            waitMs,
            ?maxSize
        ) : 'extra -> 'a -> Async<'b voption> =
        createThrottledWithExtra getBatched ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least minWaitMs and at most maxWaitMs after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified). The returned
    /// non-batched function will return ValueNone for items that are not found.
    static member Create
        (
            getBatched: 'a[] -> Async<('a * 'b voption)[]>,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'a -> Async<'b voption> =
        createDebounced getBatched ValueNone minWaitAfterAddMs minWaitMs maxWaitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least minWaitMs and at most maxWaitMs after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified). The returned
    /// non-batched function will return ValueNone for items that are not found.
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create
        (
            getBatched: 'extra -> 'a[] -> Async<('a * 'b voption)[]>,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'extra -> 'a -> Async<'b voption> =
        createDebouncedWithExtra
            getBatched
            ValueNone
            minWaitAfterAddMs
            minWaitMs
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return an empty list for items that are not
    /// found.
    static member Create(getBatched: 'a[] -> Async<('a * 'b list)[]>, waitMs, ?maxSize) : 'a -> Async<'b list> =
        createThrottled getBatched [] waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return an empty list for items that are not
    /// found.
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create
        (
            getBatched: 'extra -> 'a[] -> Async<('a * 'b list)[]>,
            waitMs,
            ?maxSize
        ) : 'extra -> 'a -> Async<'b list> =
        createThrottledWithExtra getBatched [] waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least minWaitMs and at most maxWaitMs after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified). The returned
    /// non-batched function will return an empty list for items that are not found.
    static member Create
        (
            getBatched: 'a[] -> Async<('a * 'b list)[]>,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'a -> Async<'b list> =
        createDebounced getBatched [] minWaitAfterAddMs minWaitMs maxWaitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least minWaitMs and at most maxWaitMs after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified). The returned
    /// non-batched function will return an empty list for items that are not found.
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create
        (
            getBatched: 'extra -> 'a[] -> Async<('a * 'b list)[]>,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'extra -> 'a -> Async<'b list> =
        createDebouncedWithExtra getBatched [] minWaitAfterAddMs minWaitMs maxWaitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return an empty array for items that are not
    /// found.
    static member Create(getBatched: 'a[] -> Async<('a * 'b[])[]>, waitMs, ?maxSize) : 'a -> Async<'b[]> =
        createThrottled getBatched [||] waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return an empty array for items that are not
    /// found.
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create
        (
            getBatched: 'extra -> 'a[] -> Async<('a * 'b[])[]>,
            waitMs,
            ?maxSize
        ) : 'extra -> 'a -> Async<'b[]> =
        createThrottledWithExtra getBatched [||] waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least minWaitMs and at most maxWaitMs after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified). The returned
    /// non-batched function will return an empty array for items that are not found.
    static member Create
        (
            getBatched: 'a[] -> Async<('a * 'b[])[]>,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'a -> Async<'b[]> =
        createDebounced getBatched [||] minWaitAfterAddMs minWaitMs maxWaitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least minWaitMs and at most maxWaitMs after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified). The returned
    /// non-batched function will return an empty array for items that are not found.
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create
        (
            getBatched: 'extra -> 'a[] -> Async<('a * 'b[])[]>,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'extra -> 'a -> Async<'b[]> =
        createDebouncedWithExtra
            getBatched
            [||]
            minWaitAfterAddMs
            minWaitMs
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return notFound for items that are not found.
    static member Create(getBatched: 'a[] -> Async<('a * 'b)[]>, notFound, waitMs, ?maxSize) : 'a -> Async<'b> =
        createThrottled getBatched notFound waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return notFound for items that are not found.
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create
        (
            getBatched: 'extra -> 'a[] -> Async<('a * 'b)[]>,
            notFound,
            waitMs,
            ?maxSize
        ) : 'extra -> 'a -> Async<'b> =
        createThrottledWithExtra getBatched notFound waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least minWaitMs and at most maxWaitMs after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified). The returned
    /// non-batched function will return notFound for items that are not found.
    static member Create
        (
            getBatched: 'a[] -> Async<('a * 'b)[]>,
            notFound,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'a -> Async<'b> =
        createDebounced getBatched notFound minWaitAfterAddMs minWaitMs maxWaitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least minWaitMs and at most maxWaitMs after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified). The returned
    /// non-batched function will return notFound for items that are not found.
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create
        (
            getBatched: 'extra -> 'a[] -> Async<('a * 'b)[]>,
            notFound,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'extra -> 'a -> Async<'b> =
        createDebouncedWithExtra
            getBatched
            notFound
            minWaitAfterAddMs
            minWaitMs
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)
