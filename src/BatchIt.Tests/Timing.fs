module TimingTests

open System

open BatchIt
open Faqt
open Faqt.Operators
open Xunit


[<Fact>]
let ``Throttled batches run waitMs after the first call`` () =
    task {
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

        %itemsBeforeDue.Should().BeEmpty()
        %recorder.Items.Should().Be([ [| 1; 2 |] ])
        %result1.Should().Be(Some "1")
        %result2.Should().Be(Some "2")
    }


[<Fact>]
let ``Calls made after a batch has run go into the next batch`` () =
    task {
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

        %itemsBeforeSecondDue.Should().Be([ [| 1 |] ])
        %recorder.Items.Should().Be([ [| 1 |]; [| 2 |] ])
        %result2.Should().Be(Some "2")
    }


[<Fact>]
let ``Debounced batches run minWaitAfterAddMs after the last call`` () =
    task {
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

        %itemsBeforeDue.Should().BeEmpty()
        %recorder.Items.Should().Be([ [| 1; 2 |] ])
    }


[<Fact>]
let ``Debounced batches wait at least minWaitMs after the first call`` () =
    task {
        let env = FakeEnvironment()
        let recorder = Recorder()

        let get =
            fakeBatcher env (Debounce(ms 10, ms 50, ms 1000)) Int32.MaxValue recorder.Batched

        let call = start (get 1)
        env.Advance 49
        let itemsBeforeDue = recorder.Items
        env.Advance 1

        let! _ = result call

        %itemsBeforeDue.Should().BeEmpty()
        %recorder.Items.Should().Be([ [| 1 |] ])
    }


[<Fact>]
let ``Debounced batches wait at most maxWaitMs after the first call`` () =
    task {
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

        %itemsBeforeDue.Should().BeEmpty()
        %recorder.Items.Should().Be([ [| 1..5 |] ])
    }


[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``With no wait, each call runs at once in its own batch`` (debounced: bool) =
    task {
        let env = FakeEnvironment()
        let recorder = Recorder()

        let timing =
            if debounced then
                Debounce(ms 0, ms 0, ms 0)
            else
                Throttle(ms 0)

        let get = fakeBatcher env timing Int32.MaxValue recorder.Batched

        let call1 = start (get 1)
        let call2 = start (get 2)

        let! result1 = result call1
        let! result2 = result call2

        %recorder.Items.Should().Be([ [| 1 |]; [| 2 |] ])
        %result1.Should().Be(Some "1")
        %result2.Should().Be(Some "2")
    }


[<Fact>]
let ``Waits of a few milliseconds are honored`` () =
    task {
        let env = FakeEnvironment()
        let recorder = Recorder()
        let get = fakeBatcher env (Throttle(ms 2)) Int32.MaxValue recorder.Batched

        let call = start (get 1)
        let itemsAtStart = recorder.Items
        env.Advance 1
        let itemsBeforeDue = recorder.Items
        env.Advance 1

        let! _ = result call

        %itemsAtStart.Should().BeEmpty()
        %itemsBeforeDue.Should().BeEmpty()
        %recorder.Items.Should().Be([ [| 1 |] ])
    }


[<Fact>]
let ``Timer callbacks before a batch is due don't run it`` () =
    task {
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

        %itemsBeforeDue.Should().BeEmpty()
        %recorder.Items.Should().Be([ [| 1 |] ])
    }
