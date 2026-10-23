module ExecutionContextTests

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

open BatchIt
open Faqt
open Faqt.Operators
open Xunit


[<Fact>]
let ``Callers continue in their own execution context`` () =
    task {
        let batched (items: int[]) =
            async { return items |> Array.map (fun item -> item, Some item) }

        let nonBatched =
            inContext "creator" (fun () -> Batch.Create(batched, 10000, maxSize = 2))

        let callAndGetAmbient item =
            async {
                let! _ = nonBatched item
                return ambient.Value
            }

        let callA = startAsCaller "A" (callAndGetAmbient 1)
        let callB = startAsCaller "B" (callAndGetAmbient 2)

        let! ambientA = result callA
        let! ambientB = result callB

        %ambientA.Should().Be("A")
        %ambientB.Should().Be("B")
    }


[<Fact>]
let ``Callers continue in their own SynchronizationContext`` () =
    task {
        let batched (items: int[]) =
            async { return items |> Array.map (fun item -> item, Some item) }

        let nonBatched = Batch.Create(batched, 10)
        use context = new SingleThreadContext()
        let threadAfterCall = TaskCompletionSource<int>()

        context.Post(
            (fun _ ->
                Async.StartImmediate(
                    async {
                        let! _ = nonBatched 1
                        threadAfterCall.SetResult Thread.CurrentThread.ManagedThreadId
                    }
                )
            ),
            null
        )

        let! threadAfterCall = result threadAfterCall.Task
        %threadAfterCall.Should().Be(context.ThreadId)
    }


[<Theory>]
[<InlineData("scheduled")>]
[<InlineData("full")>]
[<InlineData("without wait")>]
let ``The batched function runs without the callers' execution context`` (batch: string) =
    task {
        let ambientInBatch = ConcurrentQueue<string>()

        let batched (items: int[]) =
            async {
                ambientInBatch.Enqueue ambient.Value
                return items |> Array.map (fun item -> item, Some item)
            }

        let create () =
            match batch with
            | "scheduled" -> Batch.Create(batched, 10)
            | "full" -> Batch.Create(batched, 10000, maxSize = 1)
            | "without wait" -> Batch.Create(batched, 0)
            | _ -> failwith $"Unknown batch kind %s{batch}"

        let nonBatched = inContext "creator" create
        let! _ = startAsCaller "caller" (nonBatched 1) |> result

        %(List.ofSeq ambientInBatch).Should().Be([ null ])
    }
