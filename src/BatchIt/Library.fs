namespace BatchIt

open System
open System.Collections.Generic
open System.Threading.Tasks


[<AutoOpen>]
module private Batch =


    /// Pipe-friendly name for max.
    let atLeast other this = max other this

    /// Pipe-friendly name for min.
    let atMost other this = min other this

    let msSince (dt: DateTime) =
        int (DateTime.Now - dt).TotalMilliseconds


    type private GetNonBatched<'extra, 'a, 'b> = 'extra -> 'a -> Async<'b>
    type private GetBatched<'extra, 'a, 'b> = 'extra -> 'a[] -> Async<('a * 'b)[]>

    type Batch<'extra, 'a, 'b> = {
        GetBatched: GetBatched<'extra, 'a, 'b>
        WaitMs: int
        MinWaitAfterAddMs: int voption
        MaxWaitMs: int
        MaxSize: int
        Items: IDictionary<struct ('extra * 'a), unit>
        Result: TaskCompletionSource<IDictionary<struct ('extra * 'a), 'b>>
        mutable FirstAdd: DateTime
        mutable LastAdd: DateTime
        mutable IsScheduled: bool
    }


    let msUntilExpiration batch =
        if batch.FirstAdd = DateTime.MinValue then
            ValueNone
        else
            match batch.MinWaitAfterAddMs with
            | ValueNone -> batch.WaitMs - msSince batch.FirstAdd
            | ValueSome minMsAfterAdd ->
                let msRemainingFromInitialWait = batch.WaitMs - msSince batch.FirstAdd
                let msRemainingFromMaxWait = batch.MaxWaitMs - msSince batch.FirstAdd
                let msRemainingFromLastAdd = minMsAfterAdd - msSince batch.LastAdd

                msRemainingFromLastAdd
                |> atLeast msRemainingFromInitialWait
                |> atMost msRemainingFromMaxWait
            |> ValueSome


    let run batch =
        async {
            let itemsByExtra =
                batch.Items.Keys
                |> Seq.groupBy (fun struct (extra, _) -> extra)
                |> Seq.map (fun (extra, extraAndItems) ->
                    extra, extraAndItems |> Seq.map (fun struct (_, item) -> item) |> Seq.toArray
                )
                |> Array.ofSeq

            // Clear captured items to release references before awaiting user code.
            batch.Items.Clear()

            try
                let! resultsByExtra =
                    itemsByExtra
                    |> Array.map (fun (extra, items) -> batch.GetBatched extra items)
                    |> Async.Parallel

                (itemsByExtra, resultsByExtra)
                ||> Array.zip
                |> Array.collect (fun ((extra, _), results) ->
                    results |> Array.map (fun (item, value) -> struct (extra, item), value)
                )
                |> dict
                |> batch.Result.SetResult
            with ex ->
                batch.Result.SetException ex
        }
        |> Async.Start


    let create getBatched waitMs minWaitAfterAddMs maxWaitMs maxSize = {
        GetBatched = getBatched
        WaitMs = waitMs
        MinWaitAfterAddMs = minWaitAfterAddMs
        MaxWaitMs = maxWaitMs
        MaxSize = maxSize
        Items = Dictionary()
        Result = TaskCompletionSource<IDictionary<struct ('extra * 'a), 'b>>()
        FirstAdd = DateTime.MinValue
        LastAdd = DateTime.MinValue
        IsScheduled = false
    }


    let addItem extra item batch =
        if batch.Items.Count = 0 then
            batch.FirstAdd <- DateTime.Now

        batch.LastAdd <- DateTime.Now
        batch.Items[struct (extra, item)] <- ()


    type Msg<'extra, 'a, 'b> =
        | Add of AsyncReplyChannel<GetNonBatched<'extra, 'a, 'b>> * extra: 'extra * item: 'a
        | RunOrReschedule


    let createBatchAgent
        (getBatched: GetBatched<'extra, 'a, 'b>)
        (notFound: 'b)
        waitMs
        minWaitAfterAddMs
        maxWaitMs
        maxSize
        : MailboxProcessor<Msg<'extra, 'a, 'b>> =

        /// Returns a non-batched function that returns a specified item in the batch
        let nonBatchedItemAwaiterFor batch : GetNonBatched<'extra, 'a, 'b> =
            fun extra x ->
                async {
                    let! res = batch.Result.Task |> Async.AwaitTask

                    match res.TryGetValue(struct (extra, x)) with
                    | false, _ -> return notFound
                    | true, x -> return x
                }

        MailboxProcessor.Start(fun inbox ->

            /// Runs the batch if it's due, or (re-)schedules it.
            let runOrSchedule batch =
                match msUntilExpiration batch with
                | ValueNone -> batch
                | ValueSome x when x <= 2 -> // Not necessary to reschedule if expiration is imminent
                    run batch
                    let newBatch = create getBatched waitMs minWaitAfterAddMs maxWaitMs maxSize
                    newBatch
                | ValueSome ms ->
                    batch.IsScheduled <- true

                    async {
                        do! Async.Sleep ms
                        inbox.Post RunOrReschedule
                    }
                    |> Async.Start

                    batch

            let rec loop (batch: Batch<'extra, 'a, 'b>) =
                async {
                    match! inbox.Receive() with

                    | Add(replyChannel, extra, item) ->
                        batch |> addItem extra item

                        replyChannel.Reply(nonBatchedItemAwaiterFor batch)

                        if batch.Items.Count >= batch.MaxSize then
                            // Batch full, run immediately and create new batch
                            run batch
                            let newBatch = create getBatched waitMs minWaitAfterAddMs maxWaitMs maxSize
                            return! loop newBatch
                        elif batch.IsScheduled then
                            // Already scheduled, we don't need to do anything here
                            return! loop batch
                        else
                            return! runOrSchedule batch |> loop

                    | RunOrReschedule -> return! runOrSchedule batch |> loop
                }

            loop (create getBatched waitMs minWaitAfterAddMs maxWaitMs maxSize)
        )


    let createBatched<'extra, 'a, 'b when 'a: equality and 'extra: equality>
        (getBatched: GetBatched<'extra, 'a, 'b>)
        (notFound: 'b)
        initialWaitMs
        minWaitAfterAddMs
        maxWaitMs
        maxSize
        : GetNonBatched<'extra, 'a, 'b> =
        let agent =
            createBatchAgent getBatched notFound initialWaitMs minWaitAfterAddMs maxWaitMs maxSize

        fun extra x ->
            async {
                let! nonBatched = agent.PostAndAsyncReply(fun replyChannel -> Add(replyChannel, extra, x))
                return! nonBatched extra x
            }


[<AbstractClass; Sealed>]
type Batch private () =

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return None for items that are not found.
    static member Create(getBatched: 'a[] -> Async<('a * 'b option)[]>, waitMs, ?maxSize) : 'a -> Async<'b option> =
        createBatched (fun () -> getBatched) None waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue) ()

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
        createBatched getBatched None waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least initialWaitMs and at most maxWaitMs  after the first call. The batch is
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
        createBatched
            (fun () -> getBatched)
            None
            minWaitMs
            (ValueSome minWaitAfterAddMs)
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)
            ()

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least initialWaitMs and at most maxWaitMs  after the first call. The batch is
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
        createBatched
            getBatched
            None
            minWaitMs
            (ValueSome minWaitAfterAddMs)
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified).
    static member Create(runBatched: 'a[] -> Async<unit>, waitMs, ?maxSize) : 'a -> Async<unit> =
        let getBatched () args =
            async {
                do! runBatched args
                return args |> Array.map (fun x -> x, ())
            }

        createBatched getBatched () waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue) ()

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

        createBatched getBatched () waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least initialWaitMs and at most maxWaitMs  after the first call. The batch is
    /// also run immediately when maxSize is reached (if specified).
    static member Create
        (
            runBatched: 'a[] -> Async<unit>,
            minWaitAfterAddMs,
            minWaitMs,
            maxWaitMs,
            ?maxSize
        ) : 'a -> Async<unit> =
        let getBatched () args =
            async {
                do! runBatched args
                return args |> Array.map (fun x -> x, ())
            }

        createBatched
            getBatched
            ()
            minWaitMs
            (ValueSome minWaitAfterAddMs)
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)
            ()

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least initialWaitMs and at most maxWaitMs  after the first call. The batch is
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

        createBatched
            getBatched
            ()
            minWaitMs
            (ValueSome minWaitAfterAddMs)
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return ValueNone for items that are not found.
    static member Create(getBatched: 'a[] -> Async<('a * 'b voption)[]>, waitMs, ?maxSize) : 'a -> Async<'b voption> =
        createBatched (fun () -> getBatched) ValueNone waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue) ()

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
        createBatched getBatched ValueNone waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least initialWaitMs and at most maxWaitMs  after the first call. The batch is
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
        createBatched
            (fun () -> getBatched)
            ValueNone
            minWaitMs
            (ValueSome minWaitAfterAddMs)
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)
            ()

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least initialWaitMs and at most maxWaitMs  after the first call. The batch is
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
        createBatched
            getBatched
            ValueNone
            minWaitMs
            (ValueSome minWaitAfterAddMs)
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return an empty list for items that are not
    /// found.
    static member Create(getBatched: 'a[] -> Async<('a * 'b list)[]>, waitMs, ?maxSize) : 'a -> Async<'b list> =
        createBatched (fun () -> getBatched) [] waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue) ()

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
        createBatched getBatched [] waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least initialWaitMs and at most maxWaitMs  after the first call. The batch is
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
        createBatched
            (fun () -> getBatched)
            []
            minWaitMs
            (ValueSome minWaitAfterAddMs)
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)
            ()

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least initialWaitMs and at most maxWaitMs  after the first call. The batch is
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
        createBatched
            getBatched
            []
            minWaitMs
            (ValueSome minWaitAfterAddMs)
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return an empty array for items that are not
    /// found.
    static member Create(getBatched: 'a[] -> Async<('a * 'b[])[]>, waitMs, ?maxSize) : 'a -> Async<'b[]> =
        createBatched (fun () -> getBatched) [||] waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue) ()

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
        createBatched getBatched [||] waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least initialWaitMs and at most maxWaitMs  after the first call. The batch is
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
        createBatched
            (fun () -> getBatched)
            [||]
            minWaitMs
            (ValueSome minWaitAfterAddMs)
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)
            ()

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least initialWaitMs and at most maxWaitMs  after the first call. The batch is
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
        createBatched
            getBatched
            [||]
            minWaitMs
            (ValueSome minWaitAfterAddMs)
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. The batch is executed waitMs
    /// after the first call, or immediately when maxSize is reached (if specified). The
    /// returned non-batched function will return notFound for items that are not found.
    static member Create(getBatched: 'a[] -> Async<('a * 'b)[]>, notFound, waitMs, ?maxSize) : 'a -> Async<'b> =
        createBatched (fun () -> getBatched) notFound waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue) ()

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
        createBatched getBatched notFound waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least initialWaitMs and at most maxWaitMs  after the first call. The batch is
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
        createBatched
            (fun () -> getBatched)
            notFound
            minWaitMs
            (ValueSome minWaitAfterAddMs)
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)
            ()

    /// Returns a non-batched version of the batched function. For each call, the batch
    /// execution is pushed forward to minWaitAfterAddMs after the call, but the batch waits
    /// at least initialWaitMs and at most maxWaitMs  after the first call. The batch is
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
        createBatched
            getBatched
            notFound
            minWaitMs
            (ValueSome minWaitAfterAddMs)
            maxWaitMs
            (defaultArg maxSize Int32.MaxValue)
