module MaxSizeTests

open System

open BatchIt
open Faqt
open Faqt.Operators
open Xunit


[<Fact>]
let ``Full batches run at once`` () =
    task {
        let env = FakeEnvironment()
        let recorder = Recorder()
        let get = fakeBatcher env (Throttle(ms 1000)) 2 recorder.Batched

        let call1 = start (get 1)
        let call2 = start (get 2)

        let! result1 = result call1
        let! result2 = result call2

        %recorder.Items.Should().Be([ [| 1; 2 |] ])
        %result1.Should().Be(Some "1")
        %result2.Should().Be(Some "2")
    }


[<Fact>]
let ``Calls made after a batch became full wait for their own batch`` () =
    task {
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

        %itemsBeforeDue.Should().Be([ [| 1; 2 |] ])
        %recorder.Items.Should().Be([ [| 1; 2 |]; [| 3 |] ])
    }


[<Fact>]
let ``Repeated items count once toward maxSize`` () =
    task {
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

        %itemsBeforeFull.Should().BeEmpty()
        %recorder.Items.Should().Be([ [| 1; 2 |] ])
        %result1.Should().Be(Some "1")
        %result2.Should().Be(Some "1")
        %result3.Should().Be(Some "2")
    }


[<Fact>]
let ``maxSize counts the items of all extra values`` () =
    task {
        let env = FakeEnvironment()
        let recorder = Recorder<string>()
        let get = Batcher(env, recorder.Batched, None, Throttle(ms 1000), 2).Get

        let call1 = start (get "a" 1)
        let call2 = start (get "b" 1)

        let! _ = result call1
        let! _ = result call2

        %(List.sort recorder.Calls).Should().Be([ "a", [| 1 |]; "b", [| 1 |] ])
    }
