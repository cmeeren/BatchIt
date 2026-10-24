# AGENTS.md

BatchIt is a small F# library that turns batched async functions into non-batched ones by collecting calls from all callers into timed batches. Its only public API is the `Batch.Create` overloads in `src/BatchIt/Library.fs`. The package targets netstandard2.0 and net8.0 from the same source and supports FSharp.Core 4.6.2 or later.

## Commands

- Restore tools: `dotnet tool restore`
- Format check: `dotnet fantomas --check .`
- Test: `dotnet test -c Release`
- Pack when packaging changes matter: `dotnet pack -c Release src/BatchIt/BatchIt.fsproj`
- Release procedure: use the repo-local `release-batchit` skill.

## Design notes

- Keep `Batch.Create` source compatible, including its F# constraints, except in deliberate breaking changes. To check, compare the `type Batch` part of the signatures that `dotnet build src/BatchIt/BatchIt.fsproj -f netstandard2.0 --no-incremental "-p:OtherFlags=--sig:<file>"` generates before and after the change.
- Batched functions must run without any caller's execution context, and callers must continue in their own execution context and `SynchronizationContext`. FSharp.Core's `MailboxProcessor` and `PostAndAsyncReply` continue code in whichever context posted or replied, which is how 1.x leaked one request's context into others.
- Use only APIs available in netstandard2.0, FSharp.Core 4.6.2, and System.Diagnostics.DiagnosticSource 6.0.
- Span names come from the compiled closure type of the user's function passed to `Batch.Create`, so internal wrappers must pass that function, not themselves, as the name source.
- Test timing and batching logic with the tests' `FakeEnvironment` rather than real sleeps. Keep real-timer tests few, with waits generous enough for loaded CI runners.
- The consumer skill `.agents/skills/batchit/SKILL.md` is packed into the NuGet package as `skills/SKILL.md`. Update it when user-facing behavior or usage guidance changes.
- For user-relevant changes, add a concise entry in the `### Unreleased` section in `RELEASE_NOTES.md`, creating that section if it does not exist. Describe the observable effect from the user's perspective rather than internal mechanisms.
- Keep `README.md` and `RELEASE_NOTES.md` user-facing: behavior, migration impact, and usage guidance, without implementation details or internal-only rationale.
