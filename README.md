BatchIt
==============

BatchIt is a very simple F# library that allows you to write batched async functions and consume them as if they were
non-batched.

It’s useful if you have DB queries that are executed very frequently. This may for example be the case when using
GraphQL resolvers or similar (e.g. JSON-API with [Felicity](https://github.com/cmeeren/Felicity)). BatchIt will also
transparently take care of deduplicating your input arguments, so that the batched function receives a distinct set of
inputs.

Installation
------------

Install the [BatchIt](https://www.nuget.org/packages/BatchIt) package from NuGet.

Quick start
-----------

BatchIt centers around these two function signatures:

* Normal: `'a -> Async<'b>`
  * Accepts a single argument `'a` (which can be a tuple or a record if you need multiple values) and returns a single
    value `'b`
* Batched: `'a [] -> Async<('a * 'b) []>'`
  * Accepts an array of `'a` (all the values to use for a single batch) and returns an array of output values `'b`
    together with their corresponding input values (so that BatchIt can correlate inputs with outputs).

Your job is to implement the batched function, and then you use BatchIt to configure timing and convert it to the normal
version. Any calls to the normal version will then be batched using your configuration and your batched function.

### Example

```f#
type Person = { Name: string }
type FindArgs = { PersonId: Guid; TenantId: Guid }

let getPersonBatched (args: FindArgs []) : Async<(FindArgs * Person option) []> =
  async {
    // BatchIt is not concerned with what actually happens inside this function.

    // Here you can e.g. query a DB with all the args. If using SQL, you might
    // use TVP or XML to get the args into the query and use INNER JOIN instead
    // of WHERE to filter your results.

    // Note that the query must return the input args for each row along with the
    // output values, so that you can create the FindArgs to return, too.

    // Example:

    let! rows = args |> ... // get rows for all input args

    return
      rows
      |> Seq.toArray
      |> Array.groupBy (fun r ->
          { PersonId = r.inputPersonId; TenantId = r.inputTenantId })
      |> Array.map (fun (batchKey, rows) ->
          batchKey,
	        rows |> Array.tryHead |> Option.map (fun r -> { Name = r.name }))
    }

// Now create the "normal" function using BatchIt:

open BatchIt

let getPerson : FindArgs -> Async<Person option> =
  Batch.Create(getPersonBatched, 50, 100, 1000)

// Now any calls to the seemingly normal getPerson function will be batched.
// Each call will reset wait time to 50ms, and it will wait at least 100ms
// and at most 1000ms.
```

### Don’t add arguments to the non-batched function

The non-batched function needs to be defined without arguments (as a module-level function-typed value), as shown above.
Don’t do the following:

```f#
// Don't do this!
let getPerson args = Batch.Create(getPersonBatched, 50, 100, 1000) args
```

The above would result in a new batched version to be created for every invocation, and no batching would take place (
all batch sizes would be 1, plus there’d be a big overhead for each call).

If you want to make the non-batched function prettier (e.g. to name the arguments), simply wrap it in another function:

```f#
let private getPerson' = Batch.Create(getPersonBatched, 50, 100, 1000)

let getPerson args = getPerson' args
```

Configuration
-------------

BatchIt’s only public API is the `Batch.Create` overloads. You have the following options:

* Throttle-like: Execute the batch X ms after the first call
  * Optionally also limit max batch size
* Debounce-like: Execute the batch X ms after the last call (timer resets when new calls come in before X ms), but wait
  at least Y ms and at most Z ms
  * Optionally also limit max batch size
* Specify the value the “normal” function should return if the batched output did not contain an item corresponding to
  the supplied input
  * For `option`, `voption`, `list`, and `array`, will automatically use `None`, `ValueNone`, `List.empty`,
    and `Array.empty`, respectively
  * For other types, you must supply the missing value yourself

Waits are in milliseconds and must be non-negative, `maxWaitMs` must be at least `minWaitMs`, and `maxSize` must be at
least 1; otherwise, `Batch.Create` throws `ArgumentException`. Arguments are deduplicated and matched with results using
F# structural equality, so e.g. equal arrays are the same argument.

Extra parameters to the batched function (e.g. connection strings)
------------------------------------------------------------------

BatchIt supports functions that accept one extra parameter in addition to the argument array. This can be used e.g. for
passing a connection string. (To pass multiple values, use a tuple or record.) The extra argument will be passed through
from the non-batched function returned by `Batch.Create`.

Example:

```f#
let getPersonBatched
		(connStr: string)
		(args: FindArgs array)
		: Async<(FindArgs * Person option) array> =
  ...  // like shown previously, but obviously uses the connection string somewhere


open BatchIt

let getPerson : string -> FindArgs -> Async<Person option> =
  Batch.Create(getPersonBatched, 50, 100, 1000)
```

If the non-batched function is called with different values for the extra argument, all values are still collected and
run as part of the same batch (regarding timing, max size, etc.), but the batched arguments are grouped by the
corresponding extra argument value and the batched function is then invoked once for each unique extra value (with the
corresponding batched arguments).

Errors and cancellation
-----------------------

If the batched function throws or is canceled, the calls it was invoked for throw the same exception or are canceled.
With extra arguments, this only affects the calls with the same extra value.

Execution context
-----------------

A batch serves several callers, so the batched function runs without any caller’s execution context: it sees default
ambient values (e.g. `Activity.Current` is `null` and the culture is the thread pool’s default), and its telemetry (e.g.
SQL dependencies) is not correlated with any of the requests it serves. Callers continue in their own execution context
and, if they have one, their `SynchronizationContext`.

Agent skill
-----------

The [BatchIt skill](https://github.com/cmeeren/BatchIt/tree/main/.agents/skills/batchit) helps coding agents use
BatchIt correctly.

To install it, copy the `.agents/skills/batchit` folder from this repository into your project's skill directory (for
Codex, `.agents/skills/batchit`), or ask your agent's skill installer to install that folder. Install only `batchit`;
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

Contributions and ideas are welcome! Please
see [Contributing.md](https://github.com/cmeeren/BatchIt/blob/main/.github/CONTRIBUTING.md) for details.
