Release notes
=============

### Unreleased

BatchIt is rewritten from scratch. The API is unchanged, so code using 1.x compiles as is, and batching works as before apart from the changes below.

#### Upgrading from 1.x

* **Don't rely on callers' ambient state in batched functions.** Batched functions now run without any caller's execution context, so ambient values such as `Activity.Current`, the culture and other `AsyncLocal` values have their defaults there, and their telemetry isn't correlated with the requests they serve. Pass anything a batched function needs as arguments or as the `'extra` value; only calls with equal `'extra` values share a batched call. Previously, batched functions ran in the context of the code that called `Batch.Create` or of an arbitrary caller, and callers could continue in another caller's context after a call. See [Execution context](https://github.com/cmeeren/BatchIt/blob/main/README.md#execution-context).
* **Update error handling that expects an `AggregateException`.** When the batched function throws, callers now get its exception itself instead of an `AggregateException` wrapping it.
* **Fix invalid configuration.** `Batch.Create` now throws `ArgumentException` for negative waits, `maxWaitMs` below `minWaitMs`, or `maxSize` below 1.

#### Fixes

* Callers are now canceled when the batched function is canceled, instead of waiting forever
* A call whose argument or `'extra` value throws when hashed or compared now fails alone; previously, it and all later calls waited forever
* When the batched function fails for one `'extra` value, only the callers with that value now fail
* A slow batched call for one `'extra` value no longer delays the callers with other values
* Arguments are now deduplicated with F# structural equality, which was already used to match them with results, so e.g. equal arrays are passed once
* Batches are now timed with a monotonic clock, so system clock changes no longer delay or hasten them, and waits of 1–2 ms are honored instead of running the batch at once

#### Documentation

* The [README](https://github.com/cmeeren/BatchIt/blob/main/README.md) is revised and now explains which calls share a batch and how to choose waits
* An [agent skill](https://github.com/cmeeren/BatchIt/blob/main/README.md#agent-skill) helps coding agents use BatchIt and is bundled in the package as `skills/SKILL.md`

### 1.2.1

* Fixed batches with different `'extra` values overwriting each other's results for otherwise identical arguments

### 1.2.0

* Added helper overloads for batched functions returning `Async<unit>`

### 1.1.0

* Add extra argument support (e.g. for passing connection strings)

### 1.0.2

* Embed symbols

### 1.0.0

* Initial release
