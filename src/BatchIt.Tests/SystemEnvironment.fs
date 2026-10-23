module SystemEnvironmentTests

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
let ``Throttled batches run with real timers`` () =
    task {
        let recorder = Recorder()
        let nonBatched = Batch.Create(recorder.Batched(), 200)

        let calls = [ 1..5 ] |> List.map (nonBatched >> start)
        let! results = calls |> List.map result |> Task.WhenAll

        %recorder.Items.Should().Be([ [| 1..5 |] ])
        %results.Should().Be([| for i in 1..5 -> Some(string i) |])
    }


[<Fact>]
let ``Debounced batches run with real timers`` () =
    task {
        let recorder = Recorder()
        let nonBatched = Batch.Create(recorder.Batched(), 100, 200, 2000)

        let calls = [ 1..5 ] |> List.map (nonBatched >> start)
        let! results = calls |> List.map result |> Task.WhenAll

        %recorder.Items.Should().Be([ [| 1..5 |] ])
        %results.Should().Be([| for i in 1..5 -> Some(string i) |])
    }


[<Fact>]
let ``Concurrent calls from many threads each get their own result`` () =
    task {
        let maxSize = 7
        let batches = ConcurrentQueue<int[]>()

        let batched (extra: string) (items: int[]) =
            async {
                batches.Enqueue items
                return items |> Array.map (fun item -> item, Some $"%s{extra}%i{item}")
            }

        let nonBatched = Batch.Create(batched, 1, 2, 5, maxSize = maxSize)

        let startCalls thread =
            Task.Run<(string * int * Task<string option>) list>(
                Func<_>(fun () -> [
                    for i in 1..200 do
                        let extra = if i % 2 = 0 then "a" else "b"
                        // Overlapping items across threads, so that calls share and repeat items.
                        let item = (thread * 31 + i) % 50
                        extra, item, Async.StartAsTask(nonBatched extra item)
                ])
            )

        let! callsPerThread = [ for thread in 1..16 -> startCalls thread ] |> Task.WhenAll
        let calls = List.concat callsPerThread
        let! results = calls |> List.map (fun (_, _, call) -> result call) |> Task.WhenAll

        let wrongResults =
            List.zip calls (List.ofArray results)
            |> List.filter (fun ((extra, item, _), result) -> result <> Some $"%s{extra}%i{item}")

        let invalidBatches =
            batches
            |> Seq.filter (fun items -> items.Length > maxSize || (Array.distinct items).Length < items.Length)
            |> List.ofSeq

        %wrongResults.Should().BeEmpty()
        %invalidBatches.Should().BeEmpty()
    }


[<Fact>]
let ``Callers resume concurrently, not one after another on the batch's thread`` () =
    task {
        use secondResumed = new ManualResetEventSlim()

        let batched (items: int[]) =
            async { return items |> Array.map (fun item -> item, Some item) }

        let nonBatched = Batch.Create(batched, 10000, maxSize = 2)

        let first =
            start (
                async {
                    let! _ = nonBatched 1
                    // Blocks the thread, so the second caller can only resume on another thread.
                    return secondResumed.Wait 5000
                }
            )

        let second =
            start (
                async {
                    let! _ = nonBatched 2
                    secondResumed.Set()
                }
            )

        let! firstSawSecondResume = result first
        do! result second

        %firstSawSecondResume.Should().BeTrue()
    }


/// Starts a call with a batched function that is created and then dropped in this method, so that only the pending
/// batch can keep the batcher alive.
[<MethodImpl(MethodImplOptions.NoInlining)>]
let private startCallWithUnreferencedBatcher () =
    let nonBatched =
        Batch.Create((fun (items: int[]) -> async { return items |> Array.map (fun item -> item, Some item) }), 100)

    start (nonBatched 1)


[<Fact>]
let ``Pending batches run even if nothing else references the batched function`` () =
    task {
        let call = startCallWithUnreferencedBatcher ()

        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()

        let! callResult = result call
        %callResult.Should().Be(Some 1)
    }
