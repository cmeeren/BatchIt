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
    /// The number of calls, including those with repeated items.
    mutable CallCount: int
    /// The number of calls that had a current activity while BatchIt's spans were being listened to.
    mutable TracedCallCount: int
    /// The current activity of the first call from each trace, recorded while BatchIt's spans are being listened to.
    CallerActivities: Dictionary<ActivityTraceId, ActivityContext>
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


/// Traces each run of a batched function as a span (activity) from the "BatchIt" activity source.
module internal BatchTracing =

    [<Literal>]
    let SourceName = "BatchIt"

    let source =
        new ActivitySource(SourceName, Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString())

    let defaultName = "BatchIt.Batch"

    /// The most links a span gets. Requests can have many activities, such as one per GraphQL resolver, so spans link
    /// to one activity per trace, and exporters may keep only some links (the OpenTelemetry SDK's default limit is 128).
    let maxLinks = 128

    /// Returns the span name for a batcher whose function has the given type name: the F# binding that created the
    /// function. F# compiles a function value to a closure class named after that binding, such as
    /// `MyApp.Db+getPerson@12` for `let getPerson = Batch.Create(getPeopleBatched, ...)` in the module `MyApp.Db`.
    let nameOfType (fullTypeName: string) =
        match fullTypeName with
        | null -> defaultName
        // For example functions that FuncConvert creates for C# callers
        | name when name.StartsWith("Microsoft.FSharp.", StringComparison.Ordinal) -> defaultName
        // Closures created in classes declared in namespaces belong to compiler-generated types like <StartupCode$...>.
        | name when name.StartsWith("<", StringComparison.Ordinal) -> defaultName
        | name ->
            let parts =
                name.Split('@').[0].Split('+')
                // Closures created in constructors are named -ctor or -cctor, and closures created in a pipeline after a
                // compiler-generated binding like "Pipe #1 input at line 12".
                |> Array.filter (fun part ->
                    part.Length > 0
                    && not (part.StartsWith("-", StringComparison.Ordinal))
                    && not (part.StartsWith("Pipe #", StringComparison.Ordinal))
                )

            if parts.Length = 0 then
                defaultName
            else
                String.Join(".", parts)

    /// Returns the span name for a batcher created from the function f (see nameOfType).
    let nameOf (f: obj) = nameOfType (f.GetType().FullName)

    /// Returns the context of the current activity if BatchIt's spans are being listened to.
    let currentCallerActivity () =
        if source.HasListeners() then
            match Activity.Current with
            | null -> ValueNone
            | activity -> ValueSome activity.Context
        else
            ValueNone

    /// Records the caller's activity for the group's span, unless one from the same trace is recorded already.
    let addCallerActivity (group: BatchGroup<'extra, 'a, 'b>) (callerActivity: ActivityContext voption) =
        match callerActivity with
        | ValueSome context ->
            group.TracedCallCount <- group.TracedCallCount + 1

            if
                group.CallerActivities.Count < maxLinks
                && not (group.CallerActivities.ContainsKey context.TraceId)
            then
                group.CallerActivities[context.TraceId] <- context
        | ValueNone -> ()

    // Tracing must never fail callers, so exceptions from listeners below are ignored.

    /// Starts the span for a run of a batched function and makes it the current activity, or returns null if nothing
    /// listens to BatchIt's spans. If all the calls it serves come from one trace, such as a single request, the span
    /// is a child of the first call's activity. Otherwise, it starts its own trace and links to the activities of the
    /// calls; call this with no current activity for that.
    let startRun (name: string) (group: BatchGroup<'extra, 'a, 'b>) : Activity =
        if source.HasListeners() then
            try
                let tags = [|
                    KeyValuePair<string, obj>("batchit.batch.size", box group.Items.Count)
                    KeyValuePair<string, obj>("batchit.batch.call_count", box group.CallCount)
                |]

                let parent =
                    if group.CallerActivities.Count = 1 && group.TracedCallCount = group.CallCount then
                        ValueSome(Seq.head group.CallerActivities.Values)
                    else
                        ValueNone

                let span =
                    match parent with
                    | ValueSome parent -> source.StartActivity(name, ActivityKind.Internal, parent, tags)
                    | ValueNone ->
                        let links =
                            group.CallerActivities.Values
                            |> Seq.map (fun context -> ActivityLink(context))
                            |> Seq.toArray

                        source.StartActivity(
                            name,
                            ActivityKind.Internal,
                            Unchecked.defaultof<ActivityContext>,
                            tags,
                            links
                        )

                match span with
                | null ->
                    // A sampler didn't record the span, as when the request's trace isn't sampled. An unrecorded
                    // placeholder in the same trace keeps the batched function's own telemetry, such as SQL calls,
                    // from starting traces of their own, so samplers treat it like the span.
                    let placeholder = new Activity(name)

                    match parent with
                    | ValueSome parent ->
                        placeholder.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags)
                        |> ignore

                        placeholder.TraceStateString <- parent.TraceState
                    | ValueNone -> ()

                    placeholder.Start()
                | span -> span
            with _ ->
                // The activity may be current even if a listener threw.
                Activity.Current <- null
                null
        else
            null

    /// Ends the span, marking it as failed if error is not null.
    let stopRun (activity: Activity) (error: exn) =
        if not (isNull activity) then
            try
                if not (isNull error) then
                    activity
                        .SetStatus(ActivityStatusCode.Error, error.Message)
                        .SetTag("error.type", error.GetType().FullName)
                    |> ignore

                activity.Dispose()
            with _ ->
                ()


/// Collects calls into batches and runs the batched function for them.
[<Sealed>]
type internal Batcher<'extra, 'a, 'b when 'extra: equality and 'a: equality>
    (
        env: IBatchEnvironment,
        getBatched: 'extra -> 'a[] -> Async<('a * 'b)[]>,
        notFound: 'b,
        timing: BatchTiming,
        maxSize: int,
        ?name: string
    ) =

    let name = defaultArg name BatchTracing.defaultName
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
        // Each run's span takes its parent from its calls rather than from whichever activity this thread has, and
        // stays current only for the batched function.
        let previousActivity = Activity.Current
        Activity.Current <- null

        try
            let activity = BatchTracing.startRun name group

            let setError (ex: exn) =
                BatchTracing.stopRun activity ex
                group.Results.TrySetException ex |> ignore

            let setResults (results: ('a * 'b)[]) =
                try
                    let resultsByItem = Dictionary(results.Length)

                    for item, value in results do
                        resultsByItem[Key item] <- value

                    BatchTracing.stopRun activity null
                    group.Results.TrySetResult resultsByItem |> ignore
                with ex ->
                    setError ex

            let setCanceled _ =
                BatchTracing.stopRun activity null
                group.Results.TrySetCanceled() |> ignore

            try
                Async.StartWithContinuations(
                    getBatched group.Extra (group.Items.ToArray()),
                    setResults,
                    setError,
                    setCanceled,
                    CancellationToken.None
                )
            with ex ->
                setError ex
        finally
            Activity.Current <- previousActivity

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
    let add (extra: 'extra) (item: 'a) (callerActivity: ActivityContext voption) =
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
                                CallCount = 0
                                TracedCallCount = 0
                                CallerActivities = Dictionary()
                                Results = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                            }

                            batch.Groups[Key extra] <- group
                            group

                    if group.ItemKeys.Add(Key item) then
                        group.Items.Add item
                        batch.ItemCount <- batch.ItemCount + 1

                    group.CallCount <- group.CallCount + 1
                    BatchTracing.addCallerActivity group callerActivity

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
            let callerActivity = BatchTracing.currentCallerActivity ()
            let! resultsByItem = add extra item callerActivity |> awaitTask

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

    let private createBatched name getBatched notFound timing maxSize : 'extra -> 'a -> Async<'b> =
        Batcher(SystemBatchEnvironment(), getBatched, notFound, timing, maxSize, name)
            .Get

    // The functions below take the user's own function as source, so that span names come from it rather than from
    // the wrappers created here.

    let private throttled source getBatched notFound waitMs maxSize =
        if waitMs < 0 then
            invalidArg "waitMs" "Must be non-negative."

        validateMaxSize maxSize
        createBatched (BatchTracing.nameOf (box source)) getBatched notFound (Throttle(fromMs waitMs)) maxSize

    let private debounced source getBatched notFound minWaitAfterAddMs minWaitMs maxWaitMs maxSize =
        if minWaitAfterAddMs < 0 then
            invalidArg "minWaitAfterAddMs" "Must be non-negative."

        if minWaitMs < 0 then
            invalidArg "minWaitMs" "Must be non-negative."

        if maxWaitMs < minWaitMs then
            invalidArg "maxWaitMs" "Must be at least minWaitMs."

        validateMaxSize maxSize

        createBatched
            (BatchTracing.nameOf (box source))
            getBatched
            notFound
            (Debounce(fromMs minWaitAfterAddMs, fromMs minWaitMs, fromMs maxWaitMs))
            maxSize

    let private withUnitResults (runBatched: 'extra -> 'a[] -> Async<unit>) extra items =
        async {
            do! runBatched extra items
            return items |> Array.map (fun item -> item, ())
        }

    let createThrottledWithExtra getBatched notFound waitMs maxSize =
        throttled getBatched getBatched notFound waitMs maxSize

    let createDebouncedWithExtra getBatched notFound minWaitAfterAddMs minWaitMs maxWaitMs maxSize =
        debounced getBatched getBatched notFound minWaitAfterAddMs minWaitMs maxWaitMs maxSize

    let createThrottled (getBatched: 'a[] -> Async<('a * 'b)[]>) notFound waitMs maxSize : 'a -> Async<'b> =
        throttled getBatched (fun () -> getBatched) notFound waitMs maxSize ()

    let createDebounced
        (getBatched: 'a[] -> Async<('a * 'b)[]>)
        notFound
        minWaitAfterAddMs
        minWaitMs
        maxWaitMs
        maxSize
        : 'a -> Async<'b> =
        debounced getBatched (fun () -> getBatched) notFound minWaitAfterAddMs minWaitMs maxWaitMs maxSize ()

    let createThrottledUnitWithExtra (runBatched: 'extra -> 'a[] -> Async<unit>) waitMs maxSize =
        throttled runBatched (withUnitResults runBatched) () waitMs maxSize

    let createDebouncedUnitWithExtra
        (runBatched: 'extra -> 'a[] -> Async<unit>)
        minWaitAfterAddMs
        minWaitMs
        maxWaitMs
        maxSize
        =
        debounced runBatched (withUnitResults runBatched) () minWaitAfterAddMs minWaitMs maxWaitMs maxSize

    let createThrottledUnit (runBatched: 'a[] -> Async<unit>) waitMs maxSize : 'a -> Async<unit> =
        throttled runBatched (withUnitResults (fun () -> runBatched)) () waitMs maxSize ()

    let createDebouncedUnit (runBatched: 'a[] -> Async<unit>) minWaitAfterAddMs minWaitMs maxWaitMs maxSize =
        debounced
            runBatched
            (withUnitResults (fun () -> runBatched))
            ()
            minWaitAfterAddMs
            minWaitMs
            maxWaitMs
            maxSize
            ()


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
        createThrottledUnit runBatched waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified).
    ///
    /// The 'extra arg allows you to pass e.g. a connection string. If the non-batched
    /// function is called with different 'extra arguments, all values are still collected
    /// and run as part of the same batch (regarding timing, max size, etc.), but the
    /// batched function is invoked once for each unique 'extra value.
    static member Create(runBatched: 'extra -> 'a[] -> Async<unit>, waitMs, ?maxSize) : 'extra -> 'a -> Async<unit> =
        createThrottledUnitWithExtra runBatched waitMs (defaultArg maxSize Int32.MaxValue)

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
        createDebouncedUnit runBatched minWaitAfterAddMs minWaitMs maxWaitMs (defaultArg maxSize Int32.MaxValue)

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
        createDebouncedUnitWithExtra
            runBatched
            minWaitAfterAddMs
            minWaitMs
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)

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
