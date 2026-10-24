module FailuresTests

open System
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

open BatchIt
open Faqt
open Faqt.Operators
open Xunit


[<Fact>]
let ``Callers get the exception that the batched function raised`` () =
    task {
        let env = FakeEnvironment()

        let batched () (_: int[]) : Async<(int * string option)[]> =
            async { return raise (InvalidOperationException "Boom") }

        let get = fakeBatcher env (Throttle(ms 0)) Int32.MaxValue batched
        let! callOutcome = start (get 1) |> outcome

        %callOutcome
            .Should()
            .BeOfCase(Faulted)
            .Whose.Should(())
            .BeOfType<InvalidOperationException>()
            .Whose.Message.Should(())
            .Be("Boom")
    }


[<Fact>]
let ``Callers get exceptions that the batched function throws before returning its async`` () =
    task {
        let env = FakeEnvironment()

        let batched () (_: int[]) : Async<(int * string option)[]> =
            raise (InvalidOperationException "Boom")

        let get = fakeBatcher env (Throttle(ms 0)) Int32.MaxValue batched
        let! callOutcome = start (get 1) |> outcome

        %callOutcome
            .Should()
            .BeOfCase(Faulted)
            .Whose.Should(())
            .BeOfType<InvalidOperationException>()
            .Whose.Message.Should(())
            .Be("Boom")
    }


[<Fact>]
let ``A failure for one extra value only fails the callers with that value`` () =
    task {
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

        %goodOutcome.Should().Be(Completed(Some 1))

        %badOutcome
            .Should()
            .BeOfCase(Faulted)
            .Whose.Should(())
            .BeOfType<InvalidOperationException>()
            .Whose.Message.Should(())
            .Be("Boom")
    }


[<Fact>]
let ``When the batched function is canceled, its callers are canceled`` () =
    task {
        let env = FakeEnvironment()

        let batched () (_: int[]) : Async<(int * string option)[]> =
            Async.FromContinuations(fun (_, _, onCancel) -> onCancel (OperationCanceledException()))

        let get = fakeBatcher env (Throttle(ms 0)) Int32.MaxValue batched
        let! callOutcome = start (get 1) |> outcome

        %callOutcome.Should().Be(Canceled)
    }


[<Fact>]
let ``A canceled call stops waiting at once`` () =
    task {
        let env = FakeEnvironment()
        let recorder = Recorder()
        let get = fakeBatcher env (Throttle(ms 100)) Int32.MaxValue recorder.Batched
        use cts = new CancellationTokenSource()

        let call = Async.StartImmediateAsTask(get 1, cancellationToken = cts.Token)
        cts.Cancel()
        let! callOutcome = outcome call

        %callOutcome.Should().Be(Canceled)
        %recorder.Calls.Should().BeEmpty()
    }


[<Fact>]
let ``Canceled calls' arguments stay in their batch, which still runs for the other calls`` () =
    task {
        let env = FakeEnvironment()
        let recorder = Recorder()
        let get = fakeBatcher env (Throttle(ms 100)) Int32.MaxValue recorder.Batched
        use cts = new CancellationTokenSource()

        let canceledCall = Async.StartImmediateAsTask(get 1, cancellationToken = cts.Token)
        let sameItemCall = start (get 1)
        let otherItemCall = start (get 2)

        let canceledOnlyCall =
            Async.StartImmediateAsTask(get 3, cancellationToken = cts.Token)

        cts.Cancel()
        env.Advance 100

        let! canceledOutcome = outcome canceledCall
        let! canceledOnlyOutcome = outcome canceledOnlyCall
        let! sameItemResult = result sameItemCall
        let! otherItemResult = result otherItemCall

        %canceledOutcome.Should().Be(Canceled)
        %canceledOnlyOutcome.Should().Be(Canceled)
        %recorder.Items.Should().Be([ [| 1; 2; 3 |] ])
        %sameItemResult.Should().Be(Some "1")
        %otherItemResult.Should().Be(Some "2")
    }


/// Cancels the only call of a batch whose batched function then fails, and returns the call and whether the batched
/// function ran. Kept in its own method so that nothing on the test's stack keeps the batch's results alive.
[<MethodImpl(MethodImplOptions.NoInlining)>]
let private failBatchOfCanceledCall message =
    let env = FakeEnvironment()
    let batchedRan = ref false

    let batched () (_: int[]) : Async<(int * string option)[]> =
        async {
            batchedRan.Value <- true
            return raise (InvalidOperationException message)
        }

    let get = fakeBatcher env (Throttle(ms 100)) Int32.MaxValue batched
    use cts = new CancellationTokenSource()
    let call = Async.StartImmediateAsTask(get 1, cancellationToken = cts.Token)
    cts.Cancel()
    env.Advance 100
    call, batchedRan.Value


[<Fact>]
let ``A failure that no call waits for isn't reported as an unobserved task exception`` () =
    task {
        let message = "No call waits for this failure"
        let unobserved = ConcurrentQueue<AggregateException>()

        let onUnobserved =
            EventHandler<UnobservedTaskExceptionEventArgs>(fun _ args -> unobserved.Enqueue args.Exception)

        TaskScheduler.UnobservedTaskException.AddHandler onUnobserved

        try
            let call, batchedRan = failBatchOfCanceledCall message
            let! callOutcome = outcome call
            %callOutcome.Should().Be(Canceled)
            %batchedRan.Should().BeTrue()

            GC.Collect()
            GC.WaitForPendingFinalizers()
        finally
            TaskScheduler.UnobservedTaskException.RemoveHandler onUnobserved

        let unobservedMessages =
            unobserved
            |> Seq.collect (fun ex -> ex.InnerExceptions)
            |> Seq.map (fun ex -> ex.Message)
            |> List.ofSeq

        %unobservedMessages.Should().NotContain(message)
    }


[<Fact>]
let ``Calls whose item can't be hashed fail alone and don't start a batch`` () =
    task {
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

        for callOutcome in [ failedOutcome; failedAgainOutcome ] do
            %callOutcome
                .Should()
                .BeOfCase(Faulted)
                .Whose.Should(())
                .BeOfType<InvalidOperationException>()
                .Whose.Message.Should(())
                .Be("Unhashable")

        %callsBeforeDue.Should().BeEmpty()
        %(List.ofSeq calls).Should().Be([ [ 1; 2 ] ])
        %result1.Should().Be(Some 1)
        %result2.Should().Be(Some 2)
    }


[<Fact>]
let ``Calls whose extra value can't be hashed fail alone, and groups left empty aren't run`` () =
    task {
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

        for callOutcome in [ unhashableExtraOutcome; unhashableItemOutcome ] do
            %callOutcome
                .Should()
                .BeOfCase(Faulted)
                .Whose.Should(())
                .BeOfType<InvalidOperationException>()
                .Whose.Message.Should(())
                .Be("Unhashable")

        %(List.ofSeq calls).Should().Be([ 1, [ 1 ] ])
        %callResult.Should().Be(Some 1)
    }
