---
name: batchit
description: Write, review, or debug F# code that uses BatchIt's Batch.Create to batch async calls across callers.
---

# BatchIt

This skill describes BatchIt 2.x. Use guidance matching the project's package version; for released versions, read the [README](https://github.com/cmeeren/BatchIt/blob/main/README.md) at Git tag `v/<version>` instead of `main`.

## Creating batched functions

- Create each non-batched function once, as a module-level value or `static let`, and call it from anywhere. Creating it per call or per request gives batches of one with extra overhead, for example `let getPerson id = Batch.Create(getPersonBatched, 10) id`, or a property such as `member _.GetPerson = Batch.Create(...)`, which calls `Batch.Create` on each access.
- Only calls in flight at the same time share a batch. Make independent calls concurrently, for example with `Async.Parallel`; awaiting them one at a time gives batches of one and adds the wait to each call.
- Every call waits for its batch, so the wait adds latency to each call. Prefer short waits, such as `minWaitAfterAddMs = 10, minWaitMs = 10, maxWaitMs = 500`, and lengthen them only when fewer, larger batches are worth the latency. Under steady traffic, debounced calls keep extending the wait, so `maxWaitMs` is the latency a call can get under load. Set `maxSize` when the batched function has an input limit, such as an API that accepts at most 100 IDs per request, or to keep each query's size and duration in check.
- The throttle overloads (`waitMs`) run a batch a fixed time after its first call. The debounce overloads (`minWaitAfterAddMs`, `minWaitMs`, `maxWaitMs`) run it `minWaitAfterAddMs` after its latest call, but at least `minWaitMs` and at most `maxWaitMs` after its first. With no wait, each call runs alone.
- The overload follows from the result type: `option`, `voption`, `list`, and array results give `None`, `ValueNone`, `[]`, and `[||]` for missing arguments; functions returning `Async<unit>` need no results; other result types need an explicit `notFound` value.
- `Batch.Create` throws `ArgumentException` for negative waits, `maxWaitMs` below `minWaitMs`, or `maxSize` below 1.

## Writing the batched function (the one you pass to `Batch.Create`)

- It receives each distinct argument once, in the order first called, and returns `(argument, result)` pairs. Arguments are deduplicated and matched with results using F# structural equality, so return each argument as received or an equal value. Arguments without a result get the not-found value; for an argument returned more than once, the last result is used.
- Use the `'extra` overloads for values that are passed through rather than batched, such as a connection string. Calls with different `'extra` values share a batch's timing and `maxSize`, but the batched function runs once per distinct `'extra` value (compared with F# equality), and a failure or slow run for one value doesn't affect callers with other values. Values that differ per request, such as an `HttpContext` or a request-scoped service, prevent batching across requests.
- If it throws or is canceled, the calls it was run for throw the same exception or are canceled. It runs without a cancellation token, so canceling a call doesn't cancel it.
- It runs without any caller's execution context, because it serves several callers: `Activity.Current` is `null` (or BatchIt's span when tracing is on), the culture is the thread pool's default, and request-scoped `AsyncLocal` values, such as `IHttpContextAccessor.HttpContext`, are absent. Pass anything it needs as arguments or as the `'extra` value. Its telemetry, such as SQL dependencies, isn't part of any request's trace.
- With tracing enabled (listening to the `BatchIt` activity source, for example with OpenTelemetry's `.AddSource("BatchIt")`), each run is a span named after the F# binding that creates the function passed to `Batch.Create` (usually the one that calls it), tagged with `batchit.batch.size` and `batchit.batch.call_count`, linked to one caller activity per trace, and the parent of the batched function's telemetry. Use these spans to see how large batches get. Each span roots its own trace, so samplers decide on it independently of the requests it links to.
- Callers continue in their own execution context and `SynchronizationContext` after a call.
