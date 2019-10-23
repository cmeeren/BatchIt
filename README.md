BatchIt
==============

BatchIt is a very simple F# library that allows you to write batched async functions and consume them as if they were non-batched.

It’s useful if you have DB queries that are executed very frequently. This may for example be the case when using GraphQL resolvers or similar (e.g. JSON-API with [FSharp.JsonApi](https://github.com/cmeeren/FSharp.JsonApi)).

Installation
------------

Install the [BatchIt](https://www.nuget.org/packages/BatchIt) package from NuGet.

Quick start
-----------

BatchIt centers around these two function signatures:

* Normal: `'a -> Async<'b>`
  * Accepts a single argument `'a` (which can be a tuple or a record if you need multiple values) and returns a single value `'b`
* Batched: `'a array -> Async<('a * 'b) array>'`
  * Accepts an array of `'a` (all the values to use for a single batch) and returns an array of output values `'b` together with their corresponding input values (so that BatchIt can correlate inputs with outputs).

Your job is to implement the batched function, and then you use BatchIt to configure timing and convert it to the normal version. Any calls to the normal version will then be batched using your configuration and your batched function.

### Example

```f#
type Person = { Name: string }
type FindArgs = { PersonId: Guid; TenantId: Guid }

let getPersonBatched (args: FindArgs array) : Async<(FindArgs * Person option) array> =
  async {
    // BatchIt is not concerned with what actually happens inside this function.
  
    // Here you can e.g. query a DB with all the args. If using SQL, you might
    // use TVP or XML to get the args into the query and use INNER JOIN instead
    // of WHERE to filter your results.
  
    // Note that the query must return the input args for each row along with the
    // output values, so that you can create the FindArgs to return, too.
  
    // Example:
  
    let! rows =  // matching rows for all input args
  
    return
      rows
      |> Seq.toArray
      |> Array.groupBy (fun r -> 
        { PersonId = r.inputPersonId; TenantId = r.inputTenantId }
      )
      |> Array.map (fun (batchKey, rows) -> 
        batchKey,
	      rows |> Array.tryHead |> Option.map (fun r -> { Name = r.name })
      )
    }
  
// Now create the "normal" function using BatchIt:
  
open BatchIt

let getPerson : PersonArgs -> Async<Person option> =
  Batch.Create(getPersonBatched, 50, 100, 1000)
  
// Now any calls to the seemingly normal getPerson function will be batched.
// Each call will reset wait time to 50ms, and it will wait at least 100ms
// and at most 1000ms.
```

Configuration
-------------

BatchIt’s only public API is the `Batch.Create` overloads. You have the following options:

* Execute the batch X ms after the first call
  * Optionally also limit max batch size
* Execute the batch X ms after the last call (timer resets when new calls come in before X ms), but wait at least Y ms and at most Z ms
  * Optionally also limit max batch size
* Specify the value the “normal” function should return if the batched output did not contain an item corresponding to the supplied input
  * For `option`, `voption`, `list`, and `array`, will automatically use `None`, `ValueNone`, `List.empty`, and `Array.empty`, respectively
  * For other types, you must supply the missing value yourself

Contributing
------------

Contributions and ideas are welcome! Please see [Contributing.md](https://github.com/cmeeren/FSharp.JsonApi/blob/master/.github/CONTRIBUTING.md) for details.

Release notes
-------------

### 1.0.2

* Embed symbols

### 1.0.0

* Initial release
