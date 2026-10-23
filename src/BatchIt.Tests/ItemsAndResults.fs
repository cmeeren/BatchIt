module ItemsAndResultsTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks

open BatchIt
open Faqt
open Faqt.Operators
open Xunit


[<Fact>]
let ``Each distinct item is passed once, in the order first called`` () =
    task {
        let env = FakeEnvironment()
        let recorder = Recorder()
        let get = fakeBatcher env (Throttle(ms 100)) Int32.MaxValue recorder.Batched

        let calls = [ 3; 1; 3; 2 ] |> List.map (get >> start)
        env.Advance 100

        let! results = calls |> List.map result |> Task.WhenAll

        %recorder.Items.Should().Be([ [| 3; 1; 2 |] ])
        %results.Should().Be([| Some "3"; Some "1"; Some "3"; Some "2" |])
    }


[<Fact>]
let ``Items missing from the results get the notFound value`` () =
    task {
        let env = FakeEnvironment()

        let batched () (items: int[]) =
            async {
                return
                    items
                    |> Array.filter (fun item -> item % 2 = 1)
                    |> Array.map (fun item -> item, "odd")
            }

        let get = Batcher(env, batched, "missing", Throttle(ms 100), Int32.MaxValue).Get()

        let call1 = start (get 1)
        let call2 = start (get 2)
        env.Advance 100

        let! result1 = result call1
        let! result2 = result call2

        %result1.Should().Be("odd")
        %result2.Should().Be("missing")
    }


[<Fact>]
let ``When the results contain an item more than once, the last value is used`` () =
    task {
        let env = FakeEnvironment()

        let batched () (_: int[]) =
            async { return [| 1, Some "first"; 1, Some "last" |] }

        let get = fakeBatcher env (Throttle(ms 0)) Int32.MaxValue batched
        let! result1 = start (get 1) |> result

        %result1.Should().Be(Some "last")
    }


[<Fact>]
let ``Items are matched with F# structural equality`` () =
    task {
        let env = FakeEnvironment()
        let calls = ConcurrentQueue<int[][]>()

        let batched () (items: int[][]) =
            async {
                calls.Enqueue items
                // Return equal but different array instances
                return items |> Array.map (fun item -> Array.copy item, Some(Array.sum item))
            }

        let get = Batcher(env, batched, None, Throttle(ms 100), Int32.MaxValue).Get()

        let call1 = start (get [| 1; 2 |])
        let call2 = start (get [| 1; 2 |])
        env.Advance 100

        let! result1 = result call1
        let! result2 = result call2

        %(List.ofSeq calls).Should().Be([ [| [| 1; 2 |] |] ])
        %result1.Should().Be(Some 3)
        %result2.Should().Be(Some 3)
    }


[<Fact>]
let ``Null items and extra values are supported`` () =
    task {
        let env = FakeEnvironment()

        let batched (extra: string) (items: string[]) =
            async { return items |> Array.map (fun item -> item, Some $"%s{extra}/%s{item}") }

        let get = Batcher(env, batched, None, Throttle(ms 100), Int32.MaxValue).Get

        let call1 = start (get null null)
        let call2 = start (get null "x")
        let call3 = start (get "e" null)
        env.Advance 100

        let! result1 = result call1
        let! result2 = result call2
        let! result3 = result call3

        %result1.Should().Be(Some "/")
        %result2.Should().Be(Some "/x")
        %result3.Should().Be(Some "e/")
    }
