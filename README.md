BatchIt
=======

BatchIt is a small F# library that lets you write a function that takes an array of arguments, such as a database query
for many IDs at once, and call it with a single argument at a time. Calls made at about the same time, from anywhere in
your app, are collected into batches, and each batch runs your function once with the distinct arguments.

This helps when many independent pieces of code each need one item, for example GraphQL resolvers, JSON:API
relationships (e.g. with [Felicity](https://github.com/cmeeren/Felicity)), or many concurrent requests that each look up
the same kind of data.

Installation
------------

Install the [BatchIt](https://www.nuget.org/packages/BatchIt) package from NuGet.

Upgrading from 1.x? See the [release notes](https://github.com/cmeeren/BatchIt/blob/main/RELEASE_NOTES.md#upgrading-from-1x).

Quick start
-----------

BatchIt works with two kinds of functions:

* The **batched function**, which you write, takes all the arguments of a batch and returns each argument together with
  its result: `'a array -> Async<('a * 'b) array>`.
* The **non-batched function**, which `Batch.Create` returns, takes a single argument and returns its result:
  `'a -> Async<'b>`. This is the function the rest of your code calls.

If you need several values per call, use a tuple or record as the argument.

```f#
open System
open BatchIt

type Person = { Id: Guid; Name: string }

let getPeopleBatched (ids: Guid array) : Async<(Guid * Person option) array> =
    async {
        // Get all the people in one go, e.g. with a single SQL query.
        let! people = Db.getPeopleByIds ids
        return people |> Array.map (fun person -> person.Id, Some person)
    }

let getPerson: Guid -> Async<Person option> =
    Batch.Create(getPeopleBatched, minWaitAfterAddMs = 10, minWaitMs = 10, maxWaitMs = 500)
```

Calls to `getPerson` that are made at about the same time now share a batch, and IDs that `getPeopleBatched` doesn't
return a result for give `None`.

Only calls that are in flight at the same time can share a batch. Calls that wait for each other, such as calls awaited
one at a time in a loop, can't share a batch with each other, and each waits for its own batch, so awaiting 100 calls
in a loop adds 100 waits. To look up several items in one place, make the calls concurrently:

```f#
let! people = ids |> Array.map getPerson |> Async.Parallel
```

### Create the non-batched function once

Only calls to the same non-batched function share batches, and each call to `Batch.Create` returns a new one. Create
the non-batched function once, for example as a module-level `let` as shown above or a `static let`, and use it
everywhere. Don't create it per call or per request:

```f#
// ❌ Don't do this! Each call creates a new non-batched function, so every batch has a single argument.
let getPerson id =
    Batch.Create(getPeopleBatched, minWaitAfterAddMs = 10, minWaitMs = 10, maxWaitMs = 500) id
```

The same happens with a property such as `member _.GetPerson = Batch.Create(...)`, which calls `Batch.Create` each time
it's accessed.

To name the argument or add documentation, wrap the non-batched function:

```f#
let private getPerson' =
    Batch.Create(getPeopleBatched, minWaitAfterAddMs = 10, minWaitMs = 10, maxWaitMs = 500)

/// Gets the person with the specified ID.
let getPerson (id: Guid) = getPerson' id
```

When batches run
----------------

Each call waits until its batch runs, so the waits you configure add latency to every call. Choose them with that in
mind:

* **Debounce:** `Batch.Create(getBatched, minWaitAfterAddMs, minWaitMs, maxWaitMs)` runs a batch `minWaitAfterAddMs`
  after its latest call, but at least `minWaitMs` and at most `maxWaitMs` after its first call. While calls keep
  arriving less than `minWaitAfterAddMs` apart, each one pushes the batch back, so under load calls can wait up to
  `maxWaitMs`.
* **Throttle:** `Batch.Create(getBatched, waitMs)` runs a batch `waitMs` after its first call.
* **Maximum size:** Both accept an optional `maxSize`, which runs a batch as soon as it has that many distinct
  arguments. Use it when your batched function has an input limit, such as an API that accepts at most 100 IDs per
  request, or to keep each query's size and duration in check.

A short `minWaitAfterAddMs` and `minWaitMs`, such as 10 ms, is usually enough to collect calls that happen
concurrently. Set `maxWaitMs` to the most latency you accept under load, such as 500 ms; `maxSize` can also end
batches early. Longer waits give fewer, larger batches at the cost of latency. With no wait, each call runs in a batch
of its own.

Waits are in milliseconds. `Batch.Create` throws `ArgumentException` for negative waits, a `maxWaitMs` below
`minWaitMs`, or a `maxSize` below 1.

Arguments and results
---------------------

The batched function receives each distinct argument of the batch once, in the order first called. Arguments are
deduplicated and matched with results using F# structural equality, so return each argument as you
received it or an equal value. The results can be in any order.

If the results don't contain the argument itself, such as for composite arguments, have the query return the argument
with each row, for example by joining a table-valued parameter of the arguments. For one-to-many lookups, group the
rows by argument and return list or array results.

Arguments that the batched function returns no result for get a not-found value, chosen by the result type:

* `None`, `ValueNone`, `[]` and `[||]` for `option`, `voption`, `list` and array results
* for other result types, the value you pass as `notFound`:

```f#
let getOrderCount: Guid -> Async<int> =
    Batch.Create(getOrderCountsBatched, notFound = 0, minWaitAfterAddMs = 10, minWaitMs = 10, maxWaitMs = 500)
```

If the batched function returns an argument more than once, the last result is used.

For work that returns nothing, such as saving or notifying, the batched function can return `Async<unit>`; the
non-batched function then also returns `Async<unit>`.

Extra argument (e.g. a connection string)
-----------------------------------------

The batched function can take one extra argument before the argument array, such as a connection string. (To pass
several values, use a tuple or record.) The non-batched function then takes the same extra argument:

```f#
let getPeopleBatched (connectionString: string) (ids: Guid array) : Async<(Guid * Person option) array> =
    ... // like above, but using the connection string

let getPerson: string -> Guid -> Async<Person option> =
    Batch.Create(getPeopleBatched, minWaitAfterAddMs = 10, minWaitMs = 10, maxWaitMs = 500)
```

Calls with different extra values still share a batch's timing and `maxSize`, but the batched function runs once for
each distinct extra value (compared with F# structural equality), with the arguments of the calls that used it. Values
that differ per request, such as an `HttpContext` or a request-scoped service, therefore prevent batching across
requests.

Errors and cancellation
-----------------------

If the batched function throws or is canceled, the calls it was run for throw the same exception or are canceled. With
an extra argument, this only affects the calls with the same extra value.

The batched function runs without a cancellation token, since it serves several calls, so canceling a call doesn't
cancel it.

Execution context
-----------------

A batch serves several callers, so the batched function runs without any caller's execution context: it sees default
ambient values (e.g. `Activity.Current` and `IHttpContextAccessor.HttpContext` are `null`, and the culture is the thread
pool's default), and its telemetry (e.g. SQL dependencies) is not correlated with any of the requests it serves. Pass
anything the batched function needs as arguments or as the extra argument. To observe batch sizes, record them inside
the batched function.

Callers continue in their own execution context and, if they have one, their `SynchronizationContext`.

Agent skill
-----------

The [BatchIt skill](https://github.com/cmeeren/BatchIt/tree/main/.agents/skills/batchit) helps coding agents use
BatchIt correctly.

To install it, copy the `.agents/skills/batchit` folder from this repository into your project's skill directory (for
Codex, `.agents/skills/batchit`; for Claude Code, `.claude/skills/batchit`), or ask your agent's skill installer to
install that folder. Install only `batchit`;
`release-batchit` is for maintainers of this library.

The skill is available starting with BatchIt 2.0. Use the Git tag matching your package version (`v/<version>`), or
`main` for development builds. Review and commit the installed skill with your project, and update it when upgrading
BatchIt. Installation is optional and explicit; the NuGet package does not install agent instructions during builds.

The same skill is bundled as `skills/SKILL.md` in the NuGet package. Agents and package-aware tools can read it from
`<global-packages-folder>/batchit/<version>/skills/SKILL.md` after restore. Run `dotnet nuget locals global-packages
--list` to locate that folder. This copy matches the installed package version; bundling it does not automatically make
it discoverable by your agent.

Contributing
------------

Contributions and ideas are welcome! Please see the
[contributing guidelines](https://github.com/cmeeren/BatchIt/blob/main/.github/CONTRIBUTING.md) for details.
