Release notes
=============

### 2.0.0

Rewritten from scratch. The API is unchanged, so code using 1.x compiles as is, but the behavior changes as follows:

* Batched functions now run without any caller's execution context (e.g. `Activity.Current`), and callers continue in their own execution context. Previously, callers could continue in another caller's context, and batched functions ran in the context of the code that called `Batch.Create` or of an arbitrary caller
* When the batched function fails for one `'extra` value, only the callers with that value now fail, and they get the original exception instead of an `AggregateException`
* A slow batched call for one `'extra` value no longer delays the callers with other values
* Callers are now canceled when the batched function is canceled, instead of waiting forever
* A call whose argument or `'extra` value throws when hashed or compared now fails alone; previously, it and all later calls waited forever
* Arguments are now deduplicated with F# structural equality, which was already used to match them with results, so e.g. equal arrays are passed once
* Invalid configuration (negative waits, `maxWaitMs` below `minWaitMs`, or `maxSize` below 1) now throws `ArgumentException`
* Batches are now timed with a monotonic clock, so system clock changes no longer delay or hasten them, and waits of 1–2 ms are honored instead of running the batch at once

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
