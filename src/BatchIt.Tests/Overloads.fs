module OverloadsTests

open System
open System.Collections.Concurrent

open BatchIt
open Faqt
open Faqt.Operators
open Xunit


[<Fact>]
let ``Each overload returns the notFound value of its result type for missing items`` () =
    task {
        // Returns value for item 1 only.
        let onlyFirst value (items: int[]) =
            async { return items |> Array.filter ((=) 1) |> Array.map (fun item -> item, value) }

        let withExtra value (_: string) items = onlyFirst value items

        let check name (get: int -> Async<'b>) (found: 'b) (missing: 'b) =
            async {
                let! foundResult = get 1
                let! missingResult = get 2

                return
                    if (foundResult, missingResult) = (found, missing) then
                        None
                    else
                        Some name
            }

        let unitCalls = ConcurrentQueue<int[]>()
        let runUnit (items: int[]) = async { unitCalls.Enqueue items }
        let runUnitWithExtra (_: string) items = runUnit items

        let! failures =
            [
                check "option" (Batch.Create(onlyFirst (Some "x"), 0)) (Some "x") None
                check "voption" (Batch.Create(onlyFirst (ValueSome "x"), 0)) (ValueSome "x") ValueNone
                check "list" (Batch.Create(onlyFirst [ "x" ], 0)) [ "x" ] []
                check "array" (Batch.Create(onlyFirst [| "x" |], 0)) [| "x" |] [||]
                check "notFound" (Batch.Create(onlyFirst 5, notFound = -1, waitMs = 0)) 5 -1
                check "unit" (Batch.Create(runUnit, 0)) () ()

                check "option, extra" (Batch.Create (withExtra (Some "x"), 0) "e") (Some "x") None
                check "voption, extra" (Batch.Create (withExtra (ValueSome "x"), 0) "e") (ValueSome "x") ValueNone
                check "list, extra" (Batch.Create (withExtra [ "x" ], 0) "e") [ "x" ] []
                check "array, extra" (Batch.Create (withExtra [| "x" |], 0) "e") [| "x" |] [||]
                check "notFound, extra" (Batch.Create (withExtra 5, notFound = -1, waitMs = 0) "e") 5 -1
                check "unit, extra" (Batch.Create (runUnitWithExtra, 0) "e") () ()

                check "option, debounced" (Batch.Create(onlyFirst (Some "x"), 0, 0, 0)) (Some "x") None

                check "voption, debounced" (Batch.Create(onlyFirst (ValueSome "x"), 0, 0, 0)) (ValueSome "x") ValueNone

                check "list, debounced" (Batch.Create(onlyFirst [ "x" ], 0, 0, 0)) [ "x" ] []
                check "array, debounced" (Batch.Create(onlyFirst [| "x" |], 0, 0, 0)) [| "x" |] [||]

                check
                    "notFound, debounced"
                    (Batch.Create(onlyFirst 5, notFound = -1, minWaitAfterAddMs = 0, minWaitMs = 0, maxWaitMs = 0))
                    5
                    -1

                check "unit, debounced" (Batch.Create(runUnit, 0, 0, 0)) () ()

                check "option, extra, debounced" (Batch.Create (withExtra (Some "x"), 0, 0, 0) "e") (Some "x") None

                check
                    "voption, extra, debounced"
                    (Batch.Create (withExtra (ValueSome "x"), 0, 0, 0) "e")
                    (ValueSome "x")
                    ValueNone

                check "list, extra, debounced" (Batch.Create (withExtra [ "x" ], 0, 0, 0) "e") [ "x" ] []
                check "array, extra, debounced" (Batch.Create (withExtra [| "x" |], 0, 0, 0) "e") [| "x" |] [||]

                check
                    "notFound, extra, debounced"
                    (Batch.Create (withExtra 5, notFound = -1, minWaitAfterAddMs = 0, minWaitMs = 0, maxWaitMs = 0) "e")
                    5
                    -1

                check "unit, extra, debounced" (Batch.Create (runUnitWithExtra, 0, 0, 0) "e") () ()
            ]
            |> Async.Sequential

        %(Array.choose id failures).Should().BeEmpty()
        %(unitCalls |> Seq.sumBy Array.length).Should().Be(8)
    }


[<Fact>]
let ``Invalid configurations are rejected`` () =
    let batched (items: int[]) =
        async { return items |> Array.map (fun item -> item, Some item) }

    let batchedWithExtra (_: string) (items: int[]) = batched items

    let rejectedParam (create: unit -> 'a) =
        try
            create () |> ignore
            "(accepted)"
        with :? ArgumentException as ex ->
            ex.ParamName

    let rejected = [
        rejectedParam (fun () -> Batch.Create(batched, -1))
        rejectedParam (fun () -> Batch.Create(batched, 10, maxSize = 0))
        rejectedParam (fun () -> Batch.Create(batched, -1, 10, 20))
        rejectedParam (fun () -> Batch.Create(batched, 5, -1, 20))
        rejectedParam (fun () -> Batch.Create(batched, 5, 20, 10))
        rejectedParam (fun () -> Batch.Create(batched, 5, 0, -1))
        rejectedParam (fun () -> Batch.Create(batched, 5, 10, 20, maxSize = 0))
        rejectedParam (fun () -> Batch.Create(batchedWithExtra, -1))
        rejectedParam (fun () -> Batch.Create(batchedWithExtra, 5, 20, 10))
    ]

    %rejected
        .Should()
        .SequenceEqual(
            [
                "waitMs"
                "maxSize"
                "minWaitAfterAddMs"
                "minWaitMs"
                "maxWaitMs"
                "maxWaitMs"
                "maxSize"
                "waitMs"
                "maxWaitMs"
            ]
        )
