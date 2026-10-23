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
let ``Each batched-function run is a root span linked to the activities of the calls it serves`` () =
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


[<Fact>]
let ``Spans link to one activity per trace`` () =
    task {
        use spans = new RecordedSpans()
        let nonBatched = Batch.Create(getThingsBatched, 200)
        let request = inContext "request" (fun () -> (new Activity("request")).Start())

        // Like two resolvers of one request
        let startChildCall name item =
            inContext
                name
                (fun () ->
                    let child =
                        (new Activity(name)).SetParentId(request.TraceId, request.SpanId).Start()

                    child.Context, start (nonBatched item)
                )

        let firstContext, firstCall = startChildCall "first" 1
        let _, secondCall = startChildCall "second" 2
        let! _ = result firstCall
        let! _ = result secondCall

        let span = spans.Spans.Should().ContainExactlyOneItem().Whose

        %(span.Links |> Seq.map (fun link -> link.Context) |> List.ofSeq)
            .Should()
            .Be([ firstContext ])
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
