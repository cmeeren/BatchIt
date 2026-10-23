module ExtraTests

open System
open System.Threading.Tasks

open BatchIt
open Faqt
open Faqt.Operators
open Xunit


[<Fact>]
let ``Calls with different extra values share a batch, with one batched call per extra value`` () =
    task {
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

        %callsBeforeDue.Should().BeEmpty()
        %(List.sort recorder.Calls).Should().Be([ "a", [| 1; 3 |]; "b", [| 2 |] ])
    }


[<Fact>]
let ``Results are kept separate per extra value`` () =
    task {
        let env = FakeEnvironment()

        let batched (extra: string) (items: int[]) =
            async { return items |> Array.map (fun item -> item, Some $"%s{extra}%i{item}") }

        let get = Batcher(env, batched, None, Throttle(ms 100), Int32.MaxValue).Get

        let callA = start (get "a" 1)
        let callB = start (get "b" 1)
        env.Advance 100

        let! resultA = result callA
        let! resultB = result callB

        %resultA.Should().Be(Some "a1")
        %resultB.Should().Be(Some "b1")
    }


[<Fact>]
let ``A slow batched call for one extra value doesn't hold up callers with other values`` () =
    task {
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

        %fastResult.Should().Be(Some "fast")
        %slowCompletedBeforeRelease.Should().BeFalse()
        %slowResult.Should().Be(Some "slow")
    }
