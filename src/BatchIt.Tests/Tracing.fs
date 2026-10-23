module TracingTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading.Tasks

open BatchIt
open Faqt
open Faqt.Operators
open Xunit


/// Records the spans from BatchIt's activity source that end while it's undisposed.
type private RecordedSpans() =

    let stopped = ConcurrentQueue<Activity>()

    let listener =
        new ActivityListener(
            ShouldListenTo = Func<ActivitySource, bool>(fun source -> source.Name = "BatchIt"),
            Sample = SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllDataAndRecorded),
            ActivityStopped = Action<Activity>(stopped.Enqueue)
        )

    do ActivitySource.AddActivityListener listener

    member _.Spans = List.ofSeq stopped

    interface IDisposable with
        member _.Dispose() = listener.Dispose()


/// Listens to BatchIt's spans like a parent-based sampler whose root sampler drops everything: it records only spans
/// with a recorded parent.
type private ParentBasedListener() =

    let listener =
        new ActivityListener(
            ShouldListenTo = Func<ActivitySource, bool>(fun source -> source.Name = "BatchIt"),
            Sample =
                SampleActivity<ActivityContext>(fun options ->
                    if options.Parent.TraceFlags.HasFlag ActivityTraceFlags.Recorded then
                        ActivitySamplingResult.AllDataAndRecorded
                    else
                        ActivitySamplingResult.None
                )
        )

    do ActivitySource.AddActivityListener listener

    interface IDisposable with
        member _.Dispose() = listener.Dispose()


/// Starts the computations as a separate caller (see startAsCaller) with its own current activity, and returns the
/// activity's context and the calls.
let private startWithActivity name (computations: Async<'a> list) =
    inContext
        name
        (fun () ->
            let activity = (new Activity(name)).Start()
            activity.Context, computations |> List.map start
        )


let private sizeOf (span: Activity) = span.GetTagItem "batchit.batch.size"


let private getThingsBatched (items: int[]) =
    async { return items |> Array.map (fun item -> item, Some item) }


let private doThingsBatched (_: int[]) = async { () }


let private createGetThings () = Batch.Create(getThingsBatched, 0)


let private createDoThings () = Batch.Create(doThingsBatched, 0)


let private getThingsWithExtraBatched (_: string) (items: int[]) = getThingsBatched items


let private doThingsWithExtraBatched (_: string) (items: int[]) = doThingsBatched items


/// Calls a non-batched function of each overload family, all created in this binding. (It avoids pipelines, which F#
/// compiles to bindings of their own.)
let private callEachOverloadFamily () = [
    Async.Ignore(Batch.Create (getThingsBatched, 0) 1)
    Async.Ignore(Batch.Create (getThingsBatched, 0, 0, 0) 1)
    Async.Ignore(Batch.Create (getThingsWithExtraBatched, 0) "e" 1)
    Async.Ignore(Batch.Create (getThingsWithExtraBatched, 0, 0, 0) "e" 1)
    Batch.Create (doThingsBatched, 0) 1
    Batch.Create (doThingsBatched, 0, 0, 0) 1
    Batch.Create (doThingsWithExtraBatched, 0) "e" 1
    Batch.Create (doThingsWithExtraBatched, 0, 0, 0) "e" 1
]


/// Listens to BatchIt's spans with a listener that throws when a span starts or, if throwOnStart is false, stops.
let private startThrowingListener throwOnStart =
    let fail =
        Action<Activity>(fun _ -> raise (InvalidOperationException "Listener failure"))

    let listener =
        new ActivityListener(
            ShouldListenTo = Func<ActivitySource, bool>(fun source -> source.Name = "BatchIt"),
            Sample = SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllDataAndRecorded)
        )

    if throwOnStart then
        listener.ActivityStarted <- fail
    else
        listener.ActivityStopped <- fail

    ActivitySource.AddActivityListener listener
    listener


type private NameInConstructor() =
    member val Name = BatchTracing.nameOf (box (fun (item: int) -> item))


[<Fact>]
let ``A run's span has the batch's tags, is current in the batched function, and links to its calls' traces`` () =
    task {
        use spans = new RecordedSpans()
        let currentInBatch = ConcurrentQueue<Activity>()

        let batched (items: int[]) =
            async {
                currentInBatch.Enqueue Activity.Current
                return items |> Array.map (fun item -> item, Some item)
            }

        let nonBatched = Batch.Create(batched, 200)

        let contextA, callsA = startWithActivity "A" [ nonBatched 1; nonBatched 1 ]
        let contextB, callsB = startWithActivity "B" [ nonBatched 2 ]
        let callWithoutActivity = start (nonBatched 3)

        for call in callsA @ callsB @ [ callWithoutActivity ] do
            let! _ = result call
            ()

        let span = spans.Spans.Should().ContainExactlyOneItem().Whose
        %span.Kind.Should().Be(ActivityKind.Internal)
        %span.ParentSpanId.Should().Be(Unchecked.defaultof<ActivitySpanId>)
        %span.TraceId.Should().NotBe(contextA.TraceId)
        %span.TraceId.Should().NotBe(contextB.TraceId)
        %(sizeOf span).Should().Be(box 3)
        %(span.GetTagItem "batchit.batch.call_count").Should().Be(box 4)

        %(span.Links |> Seq.map (fun link -> link.Context) |> List.ofSeq)
            .Should()
            .HaveSameItemsAs([ contextA; contextB ])

        %span.Status.Should().Be(ActivityStatusCode.Unset)
        %(List.ofSeq currentInBatch).Should().Be([ span ])
    }


[<Fact>]
let ``Each extra value's run gets its own span`` () =
    task {
        use spans = new RecordedSpans()

        let batched (_: string) (items: int[]) =
            async { return items |> Array.map (fun item -> item, Some item) }

        let nonBatched = Batch.Create(batched, 200)

        let calls = [ start (nonBatched "a" 1); start (nonBatched "a" 2); start (nonBatched "b" 3) ]

        for call in calls do
            let! _ = result call
            ()

        %(spans.Spans |> List.map sizeOf |> List.sortBy unbox<int>)
            .Should()
            .Be([ box 1; box 2 ])
    }


/// Starts a call as a separate caller (see startAsCaller) whose current activity is a child of the request activity,
/// like a resolver of a GraphQL request, and returns that activity's context and the call.
let private startCallInRequest (request: Activity) name computation =
    inContext
        name
        (fun () ->
            let child =
                (new Activity(name)).SetParentId(request.TraceId, request.SpanId).Start()

            child.Context, start computation
        )


let private startRequest name =
    inContext name (fun () -> (new Activity(name)).Start())


[<Fact>]
let ``Spans of runs whose calls all come from one trace are children of the first call's activity`` () =
    task {
        use spans = new RecordedSpans()
        let nonBatched = Batch.Create(getThingsBatched, 200)
        let request = startRequest "request"

        let firstContext, firstCall = startCallInRequest request "first" (nonBatched 1)
        let _, secondCall = startCallInRequest request "second" (nonBatched 2)
        let! _ = result firstCall
        let! _ = result secondCall

        let span = spans.Spans.Should().ContainExactlyOneItem().Whose
        %span.TraceId.Should().Be(request.TraceId)
        %span.ParentSpanId.Should().Be(firstContext.SpanId)
        %span.Links.Should().BeEmpty()
    }


[<Fact>]
let ``Spans of runs whose calls come from several traces link to one activity per trace`` () =
    task {
        use spans = new RecordedSpans()
        let nonBatched = Batch.Create(getThingsBatched, 200)
        let request = startRequest "request"
        let otherRequest = startRequest "other request"

        let firstContext, firstCall = startCallInRequest request "first" (nonBatched 1)
        let _, secondCall = startCallInRequest request "second" (nonBatched 2)
        let otherContext, otherCall = startCallInRequest otherRequest "other" (nonBatched 3)

        for call in [ firstCall; secondCall; otherCall ] do
            let! _ = result call
            ()

        let span = spans.Spans.Should().ContainExactlyOneItem().Whose
        %span.ParentSpanId.Should().Be(Unchecked.defaultof<ActivitySpanId>)

        %(span.Links |> Seq.map (fun link -> link.Context) |> List.ofSeq)
            .Should()
            .HaveSameItemsAs([ firstContext; otherContext ])
    }


[<Fact>]
let ``Spans of runs that also serve calls without an activity start their own trace`` () =
    task {
        use spans = new RecordedSpans()
        let nonBatched = Batch.Create(getThingsBatched, 200)
        let request = startRequest "request"

        let callerContext, call = startCallInRequest request "caller" (nonBatched 1)
        let callWithoutActivity = start (nonBatched 2)
        let! _ = result call
        let! _ = result callWithoutActivity

        let span = spans.Spans.Should().ContainExactlyOneItem().Whose
        %span.ParentSpanId.Should().Be(Unchecked.defaultof<ActivitySpanId>)
        %span.TraceId.Should().NotBe(request.TraceId)

        %(span.Links |> Seq.map (fun link -> link.Context) |> List.ofSeq)
            .Should()
            .Be([ callerContext ])
    }


[<Fact>]
let ``If the calls' trace isn't sampled, the batched function's telemetry stays in it, unrecorded`` () =
    task {
        use _ = new ParentBasedListener()
        let currentInBatch = ConcurrentQueue<Activity>()

        let batched (items: int[]) =
            async {
                currentInBatch.Enqueue Activity.Current
                return items |> Array.map (fun item -> item, Some item)
            }

        // An activity started without a sampler isn't recorded.
        let callerContext, calls =
            startWithActivity "request" [ Batch.Create (batched, 0) 1 ]

        let! _ = result calls.Head

        let current = currentInBatch.Should().ContainExactlyOneItem().Whose
        %current.Should().NotBeNull()
        %current.TraceId.Should().Be(callerContext.TraceId)
        %current.ParentSpanId.Should().Be(callerContext.SpanId)
        %current.Recorded.Should().BeFalse()
    }


[<Fact>]
let ``If a run's own trace isn't sampled, the batched function's telemetry belongs to it, unrecorded`` () =
    task {
        use _ = new ParentBasedListener()
        let currentInBatch = ConcurrentQueue<Activity>()

        let batched (items: int[]) =
            async {
                currentInBatch.Enqueue Activity.Current
                return items |> Array.map (fun item -> item, Some item)
            }

        let! _ = start (Batch.Create (batched, 0) 1) |> result

        let current = currentInBatch.Should().ContainExactlyOneItem().Whose
        %current.Should().NotBeNull()
        %current.ParentSpanId.Should().Be(Unchecked.defaultof<ActivitySpanId>)
        %current.Recorded.Should().BeFalse()
    }


[<Fact>]
let ``Spans link to at most 128 activities`` () =
    task {
        use spans = new RecordedSpans()
        let nonBatched = Batch.Create(getThingsBatched, 200)

        let calls = [
            for i in 1..130 do
                let _, calls = startWithActivity $"caller %i{i}" [ nonBatched i ]
                yield! calls
        ]

        for call in calls do
            let! _ = result call
            ()

        let span = spans.Spans.Should().ContainExactlyOneItem().Whose
        %(Seq.length span.Links).Should().Be(128)
    }


[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``Spans of failed runs have error status`` (throwBeforeAsync: bool) =
    task {
        use spans = new RecordedSpans()

        let batched (_: int[]) : Async<(int * int option)[]> =
            if throwBeforeAsync then
                raise (InvalidOperationException "Boom")
            else
                async { return raise (InvalidOperationException "Boom") }

        let! _ = start (Batch.Create (batched, 0) 1) |> outcome

        let span = spans.Spans.Should().ContainExactlyOneItem().Whose
        %span.Status.Should().Be(ActivityStatusCode.Error)
        %span.StatusDescription.Should().Be("Boom")

        %(span.GetTagItem "error.type")
            .Should()
            .Be(box "System.InvalidOperationException")
    }


[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``Listeners that throw don't affect callers`` (throwOnStart: bool) =
    task {
        use _ = startThrowingListener throwOnStart

        let succeeding (items: int[]) =
            async { return items |> Array.map (fun item -> item, Some item) }

        let failing (_: int[]) : Async<(int * int option)[]> =
            async {
                do! Async.Sleep 1
                return raise (InvalidOperationException "Boom")
            }

        let! succeeded = start (Batch.Create (succeeding, 0) 1) |> outcome
        let! failed = start (Batch.Create (failing, 0) 1) |> outcome

        %succeeded.Should().Be(Completed(Some 1))
        %failed.Should().BeOfCase(Faulted).Whose.Message.Should(()).Be("Boom")
    }


[<Fact>]
let ``Spans end when the batched function's async completes`` () =
    task {
        use spans = new RecordedSpans()

        let started =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let release =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let batched (items: int[]) =
            async {
                started.SetResult()
                do! release.Task |> Async.AwaitTask
                return items |> Array.map (fun item -> item, Some item)
            }

        let call = start (Batch.Create (batched, 0) 1)
        do! started.Task
        let spansWhileRunning = spans.Spans
        release.SetResult()
        let! _ = result call

        %spansWhileRunning.Should().BeEmpty()
        %spans.Spans.Should().ContainExactlyOneItem()
    }


[<Fact>]
let ``Without a listener, nothing is traced and the batched function has no current activity`` () =
    task {
        let currentInBatch = ConcurrentQueue<Activity>()

        let batched (items: int[]) =
            async {
                currentInBatch.Enqueue Activity.Current
                return items |> Array.map (fun item -> item, Some item)
            }

        let _, calls = startWithActivity "caller" [ Batch.Create (batched, 0) 1 ]
        let! _ = result calls.Head

        %(List.ofSeq currentInBatch).Should().Be([ null ])
    }


[<Fact>]
let ``Spans are named after the F# binding that creates the function passed to Batch.Create`` () =
    task {
        use spans = new RecordedSpans()

        let! _ = start (createGetThings () 1) |> result
        do! start (createDoThings () 1) |> result

        %(spans.Spans |> List.map (fun span -> span.DisplayName))
            .Should()
            .Be([ "TracingTests.createGetThings"; "TracingTests.createDoThings" ])
    }


[<Fact>]
let ``Spans of every overload family are named after the binding that creates the function`` () =
    task {
        use spans = new RecordedSpans()

        for call in callEachOverloadFamily () do
            do! start call |> result

        %(spans.Spans |> List.map (fun span -> span.DisplayName))
            .Should()
            .Be(List.replicate 8 "TracingTests.callEachOverloadFamily")
    }


[<Theory>]
[<InlineData("MyApp.Db+getPerson@12", "MyApp.Db.getPerson")>]
[<InlineData("MyApp.Db+People+getPerson@12-3", "MyApp.Db.People.getPerson")>]
[<InlineData("MyApp.Db+-ctor@12", "MyApp.Db")>]
[<InlineData("Microsoft.FSharp.Core.FuncConvert+FromFunc@123", "BatchIt.Batch")>]
[<InlineData("MyApp.Db+Pipe #1 input at line 12@12", "MyApp.Db")>]
[<InlineData("<StartupCode$MyApp>.$Program", "BatchIt.Batch")>]
[<InlineData("@12", "BatchIt.Batch")>]
let ``Span names come from the closure type's name, with fallbacks`` (typeName: string, expected: string) =
    %(BatchTracing.nameOfType typeName).Should().Be(expected)


[<Fact>]
let ``Closures created in constructors are named after their module`` () =
    %NameInConstructor().Name.Should().Be("TracingTests")
