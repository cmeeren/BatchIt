module Tests

open Expecto
open Swensen.Unquote
open BatchIt


// Like testCaseAsync, but runs the test in parallel n times (useful for non-deterministic
// tests)
let parallelTestCaseAsync n name test =
  List.replicate n test
  |> Async.Parallel
  |> Async.Ignore<unit []>
  |> testCaseAsync name



[<Tests>]
let tests =

  testSequenced <| testList "Tests" [


    testList "waitMs" [


      parallelTestCaseAsync 1000 "Batches for waitMs after the first add" <| async {
        let mutable calledWith = []
        let batched (args: int []) =
          async {
            lock calledWith (fun () ->
              calledWith <- calledWith @ [Array.sort args]
            )
            return args |> Array.map (fun a -> a, Some "")
          }
        let nonBatched = Batch.Create(batched, 50)

        do!
          [1..8]
          |> List.map nonBatched
          |> Async.Parallel
          |> Async.Ignore

        do! Async.Sleep 50

        do!
          [9..16]
          |> List.map nonBatched
          |> Async.Parallel
          |> Async.Ignore

        do! Async.Sleep 50

        do! nonBatched 17 |> Async.Ignore

        let calledWith' = calledWith
        test <@ calledWith' = [ [| 1..8 |]; [| 9..16 |]; [| 17 |]] @>
      }


      parallelTestCaseAsync 1000 "Batches do not exceed maxSize even if arguments are added within waitMs" <| async {
        let mutable calledWith = []
        let batched (args: int []) =
          async {
            lock calledWith (fun () ->
              calledWith <- calledWith @ [Array.sort args]
            )
            return args |> Array.map (fun a -> a, Some "")
          }
        let nonBatched = Batch.Create(batched, 50, maxSize = 5)

        do!
          [1..8]
          |> List.map nonBatched
          |> Async.Parallel
          |> Async.Ignore

        do! Async.Sleep 50

        do!
          [9..16]
          |> List.map nonBatched
          |> Async.Parallel
          |> Async.Ignore

        do! Async.Sleep 50

        do! nonBatched 17 |> Async.Ignore

        let calledWith' = calledWith
        test <@ calledWith' |> List.forall (fun a -> Array.length a <= 5) @>
      }


    ]


    testList "unit return type" [


      parallelTestCaseAsync 1000 "overloads for unit return type work" <| async {
        let mutable calledWith = []
        let batched (args: int []) =
          async {
            lock calledWith (fun () ->
              calledWith <- calledWith @ [Array.sort args]
            )
          }
        let nonBatched = Batch.Create(batched, 50)

        do!
          [1..8]
          |> List.map nonBatched
          |> Async.Parallel
          |> Async.Ignore

        do! Async.Sleep 50

        do!
          [9..16]
          |> List.map nonBatched
          |> Async.Parallel
          |> Async.Ignore

        do! Async.Sleep 50

        do! nonBatched 17 |> Async.Ignore

        let calledWith' = calledWith
        test <@ calledWith' = [ [| 1..8 |]; [| 9..16 |]; [| 17 |]] @>
      }


    ]


    testList "minWaitAfterAddMs/minWaitMs/maxWaitMs" [


      parallelTestCaseAsync 1000 "Extends batching by minWaitAfterAddMs after each add" <| async {
        let mutable calledWith = []
        let batched (args: int []) =
          async {
            lock calledWith (fun () ->
              calledWith <- calledWith @ [Array.sort args]
            )
            return args |> Array.map (fun a -> a, Some "")
          }
        let nonBatched = Batch.Create(batched, 250, 0, 5000)

        for i in [1..8] do
          do! Async.Sleep 10
          nonBatched i |> Async.Ignore |> Async.Start
        

        do! Async.Sleep 1000

        for i in [9..16] do
          do! Async.Sleep 10
          nonBatched i |> Async.Ignore |> Async.Start

        do! Async.Sleep 1000

        do! nonBatched 17 |> Async.Ignore

        let calledWith' = calledWith
        test <@ calledWith' = [ [| 1..8 |]; [| 9..16 |]; [| 17 |]] @>
      }


      parallelTestCaseAsync 1000 "Waits at least minWaitMs regardless of minWaitAfterAddMs" <| async {
        let mutable calledWith = []
        let batched (args: int []) =
          async {
            lock calledWith (fun () ->
              calledWith <- calledWith @ [Array.sort args]
            )
            return args |> Array.map (fun a -> a, Some "")
          }
        let nonBatched = Batch.Create(batched, 0, 5000, 5000)

        for i in [1..8] do
          do! Async.Sleep 10
          nonBatched i |> Async.Ignore |> Async.Start
        

        do! Async.Sleep 500

        for i in [9..16] do
          do! Async.Sleep 10
          nonBatched i |> Async.Ignore |> Async.Start

        do! Async.Sleep 500

        do! nonBatched 17 |> Async.Ignore

        let calledWith' = calledWith
        test <@ calledWith' = [ [| 1..17 |]] @>
      }


      parallelTestCaseAsync 1000 "Waits at most maxWaitMs regardless of minWaitAfterAddMs" <| async {
        let mutable calledWith = []
        let batched (args: int []) =
          async {
            lock calledWith (fun () ->
              calledWith <- calledWith @ [Array.sort args]
            )
            return args |> Array.map (fun a -> a, Some "")
          }
        let nonBatched = Batch.Create(batched, 0, 50, 100)

        for i in [1..16] do
          do! Async.Sleep 10
          nonBatched i |> Async.Ignore |> Async.Start

        let calledWith' = calledWith
        test <@ calledWith'.Length > 1 && calledWith'.Length < 16 @>
      }


      parallelTestCaseAsync 1000 "Batches do not exceed maxSize even if arguments are added within minWaitAfterAddMs/minWaitMs/maxWaitMs" <| async {
        let mutable calledWith = []
        let batched (args: int []) =
          async {
            lock calledWith (fun () ->
              calledWith <- calledWith @ [Array.sort args]
            )
            return args |> Array.map (fun a -> a, Some "")
          }
        let nonBatched = Batch.Create(batched, 100, 500, 1000, maxSize = 5)

        for i in [1..16] do
          do! Async.Sleep 10
          nonBatched i |> Async.Ignore |> Async.Start

        let calledWith' = calledWith
        test <@ calledWith' |> List.forall (fun a -> Array.length a <= 5) @>
      }


    ]


    testList "extra" [


      parallelTestCaseAsync 1000 "When sending extra, batches are split by extra value" <| async {
        let mutable calledWith = []
        let batched (extra: string) (args: int []) =
          async {
            lock calledWith (fun () ->
              calledWith <- calledWith @ [extra, Array.sort args]
            )
            return args |> Array.map (fun a -> a, Some "")
          }
        let nonBatched = Batch.Create(batched, 50)

        do!
          [
            "a", 1
            "a", 2
            "a", 3
            "a", 4
            "other", 5
            "a", 6
            "a", 7
            "a", 8
          ]
          |> List.map (fun (extra, i) -> nonBatched extra i)
          |> Async.Parallel
          |> Async.Ignore

        let calledWith' = List.sort calledWith
        test <@ calledWith' = [ "a", [| 1; 2; 3; 4; 6; 7; 8 |]; "other", [| 5 |]] @>
      }


    ]

  ]
