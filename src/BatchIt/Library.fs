namespace BatchIt

open System
open System.Collections.Generic
open System.Threading.Tasks


module private Batch =


  /// Pipe-friendly name for max.
  let atLeast other this =
    max other this

  /// Pipe-friendly name for min.
  let atMost other this =
    min other this

  let msSince (dt: DateTime) =
    int (DateTime.Now - dt).TotalMilliseconds


  type GetNonBatched<'a, 'b> = 'a -> Async<'b>
  type GetBatched<'a, 'b> = 'a array -> Async<('a * 'b) array>

  [<Struct>]
  type NullSafeDictKey<'a> = K of 'a with
    member this.Inner = let (K x) = this in x


  type Batch<'a, 'b> = {
    GetBatched: GetBatched<'a, 'b>
    WaitMs: int
    MinWaitAfterAddMs: int voption
    MaxWaitMs: int
    MaxSize: int
    Items: IDictionary<NullSafeDictKey<'a>, unit>
    Result: TaskCompletionSource<IDictionary<NullSafeDictKey<'a>, 'b>>
    mutable FirstAdd: DateTime
    mutable LastAdd: DateTime
    mutable IsScheduled: bool
  }


  let msUntilExpiration batch =
    if batch.FirstAdd = DateTime.MinValue then ValueNone
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
      let items =
        batch.Items.Keys
        |> Seq.map (fun k -> k.Inner)
        |> Seq.toArray
      try
        let! res = batch.GetBatched items
        res
        |> Array.map (fun (k, v) -> K k, v)
        |> dict
        |> batch.Result.SetResult
      with ex ->
        batch.Result.SetException ex
    } |> Async.Start


  let create getBatched waitMs minWaitAfterAddMs maxWaitMs maxSize =
    { GetBatched = getBatched
      WaitMs = waitMs
      MinWaitAfterAddMs = minWaitAfterAddMs
      MaxWaitMs = maxWaitMs
      MaxSize = maxSize
      Items = Dictionary()
      Result = TaskCompletionSource()
      FirstAdd = DateTime.MinValue
      LastAdd = DateTime.MinValue
      IsScheduled = false
    }


  let addItem item batch =
    if batch.Items.Count = 0 then
      batch.FirstAdd <- DateTime.Now
    batch.LastAdd <- DateTime.Now
    batch.Items.[K item] <- ()


  type Msg<'a, 'b> =
    | Add of AsyncReplyChannel<GetNonBatched<'a, 'b>> * item: 'a
    | RunOrReschedule


  let createBatchAgent
      (getBatched: GetBatched<'a, 'b>)
      (notFound: 'b)
      waitMs
      minWaitAfterAddMs
      maxWaitMs
      maxSize
      : MailboxProcessor<Msg<'a, 'b>> =

    /// Returns a unbatched function that returns a specified item in the batch
    let unbatchedItemAwaiterFor batch : GetNonBatched<'a,'b> =
      fun x ->
        async {
          let! res = batch.Result.Task |> Async.AwaitTask
          match res.TryGetValue (K x) with
          | false, _ -> return notFound
          | true, x -> return x
        }

    MailboxProcessor.Start(fun inbox ->

      /// Runs the batch if it's due, or (re-)schedules it.
      let runOrSchedule batch =
        match msUntilExpiration batch with
        | ValueNone ->
            batch
        | ValueSome x when x <= 2 ->  // Not necessary to reschedule if expiration is imminent
            run batch
            let newBatch = create getBatched waitMs minWaitAfterAddMs maxWaitMs maxSize
            newBatch
        | ValueSome ms ->
            batch.IsScheduled <- true
            async {
              do! Async.Sleep ms
              inbox.Post RunOrReschedule
            } |> Async.Start
            batch

      let rec loop (batch: Batch<'a, 'b>) =
        async {
          match! inbox.Receive() with

          | Add (replyChannel, item) ->
              batch |> addItem item

              replyChannel.Reply (unbatchedItemAwaiterFor batch)

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

          | RunOrReschedule ->
              return! runOrSchedule batch |> loop
        }

      loop (create getBatched waitMs minWaitAfterAddMs maxWaitMs maxSize)
    )


  let createBatched<'a, 'b when 'a : equality>
      (getBatched: GetBatched<'a, 'b>) (notFound: 'b)
      initialWaitMs minWaitAfterAddMs maxWaitMs maxSize
      : GetNonBatched<'a, 'b> =
    let agent = createBatchAgent getBatched notFound initialWaitMs minWaitAfterAddMs maxWaitMs maxSize
    fun x ->
      async {
        let! nonBatched = agent.PostAndAsyncReply (fun replyChannel -> Add (replyChannel, x))
        return! nonBatched x
      }


open Batch


[<AbstractClass; Sealed>]
type Batch private () =

  /// Returns a non-batched version of the batched function. The batch is executed
  /// waitMs after the first call, or immediately when maxSize is reached (if specified).
  /// The returned non-batched function will return None for items that are not found.
  static member Create(getBatched: GetBatched<'a, 'b option>, waitMs, ?maxSize) =
    createBatched getBatched None waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

  /// Returns a non-batched version of the batched function. For each call,
  /// the batch execution is pushed forward to minWaitAfterAddMs after the call,
  /// but the batch waits at least initialWaitMs and at most maxWaitMs  after the first
  /// call. The batch is also run immediately when maxSize is reached (if specified).
  /// The returned non-batched function will return None for items that are not found.
  static member Create(getBatched: GetBatched<'a, 'b option>, minWaitAfterAddMs, minWaitMs, maxWaitMs, ?maxSize) =
    createBatched getBatched None minWaitMs (ValueSome minWaitAfterAddMs) maxWaitMs (defaultArg maxSize Int32.MaxValue)

  /// Returns a non-batched version of the batched function. The batch is executed
  /// waitMs after the first call, or immediately when maxSize is reached (if specified).
  /// The returned non-batched function will return ValueNone for items that are not found.
  static member Create(getBatched: GetBatched<'a, 'b voption>, waitMs, ?maxSize) =
    createBatched getBatched ValueNone waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

  /// Returns a non-batched version of the batched function. For each call,
  /// the batch execution is pushed forward to minWaitAfterAddMs after the call,
  /// but the batch waits at least initialWaitMs and at most maxWaitMs  after the first
  /// call. The batch is also run immediately when maxSize is reached (if specified).
  /// The returned non-batched function will return ValueNone for items that are not found.
  static member Create(getBatched: GetBatched<'a, 'b voption>, minWaitAfterAddMs, minWaitMs, maxWaitMs, ?maxSize) =
    createBatched getBatched ValueNone minWaitMs (ValueSome minWaitAfterAddMs) maxWaitMs (defaultArg maxSize Int32.MaxValue)

  /// Returns a non-batched version of the batched function. The batch is executed
  /// waitMs after the first call, or immediately when maxSize is reached (if specified).
  /// The returned non-batched function will return an empty list for items that are not found.
  static member Create(getBatched: GetBatched<'a, 'b list>, waitMs, ?maxSize) =
    createBatched getBatched [] waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

  /// Returns a non-batched version of the batched function. For each call,
  /// the batch execution is pushed forward to minWaitAfterAddMs after the call,
  /// but the batch waits at least initialWaitMs and at most maxWaitMs  after the first
  /// call. The batch is also run immediately when maxSize is reached (if specified).
  /// The returned non-batched function will return an empty list for items that are not found.
  static member Create(getBatched: GetBatched<'a, 'b list>, minWaitAfterAddMs, minWaitMs, maxWaitMs, ?maxSize) =
    createBatched getBatched [] minWaitMs (ValueSome minWaitAfterAddMs) maxWaitMs (defaultArg maxSize Int32.MaxValue)

  /// Returns a non-batched version of the batched function. The batch is executed
  /// waitMs after the first call, or immediately when maxSize is reached (if specified).
  /// The returned non-batched function will return an empty array for items that are not found.
  static member Create(getBatched: GetBatched<'a, 'b array>, waitMs, ?maxSize) =
    createBatched getBatched [||] waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

  /// Returns a non-batched version of the batched function. For each call,
  /// the batch execution is pushed forward to minWaitAfterAddMs after the call,
  /// but the batch waits at least initialWaitMs and at most maxWaitMs  after the first
  /// call. The batch is also run immediately when maxSize is reached (if specified).
  /// The returned non-batched function will return an empty array for items that are not found.
  static member Create(getBatched: GetBatched<'a, 'b array>, minWaitAfterAddMs, minWaitMs, maxWaitMs, ?maxSize) =
    createBatched getBatched [||] minWaitMs (ValueSome minWaitAfterAddMs) maxWaitMs (defaultArg maxSize Int32.MaxValue)

  /// Returns a non-batched version of the batched function. The batch is executed
  /// waitMs after the first call, or immediately when maxSize is reached (if specified).
  /// The returned non-batched function will return notFound for items that are not found.
  static member Create(getBatched: GetBatched<'a, 'b>, notFound, waitMs, ?maxSize) =
    createBatched getBatched notFound waitMs ValueNone waitMs (defaultArg maxSize Int32.MaxValue)

  /// Returns a non-batched version of the batched function. For each call,
  /// the batch execution is pushed forward to minWaitAfterAddMs after the call,
  /// but the batch waits at least initialWaitMs and at most maxWaitMs  after the first
  /// call. The batch is also run immediately when maxSize is reached (if specified).
  /// The returned non-batched function will return notFound for items that are not found.
  static member Create(getBatched: GetBatched<'a, 'b>, notFound, minWaitAfterAddMs, minWaitMs, maxWaitMs, ?maxSize) =
    createBatched getBatched notFound minWaitMs (ValueSome minWaitAfterAddMs) maxWaitMs (defaultArg maxSize Int32.MaxValue)
