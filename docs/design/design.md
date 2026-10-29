# Design: Navisworks MCP

This is the merged design. `candidate-a.md` (Opus) and `candidate-b.md` (Fable) are the two independent sketches it came from. `grounding.md` has the verified facts.

## What both candidates agreed on, kept as is

- The C# plugin owns every op: its signature, doc line, example and handler. The Node server has no per-tool code.
- The server and plugin talk over a named pipe `navisworks-mcp-<pid>`, one JSON object per line.
- Every request runs as a job. If it finishes within `wait` ms the reply is inline. Otherwise the reply carries `j#`, and `job` polls it from the pipe thread while the UI thread is busy.
- Errors have `code`, `message` and `next`. The agent is told to do exactly what `next` says.
- Handles (`r#`) live in the plugin with exact totals and stable paging. A `where` string maps onto native `Search`, and `or` maps to `SearchCondition` groups.
- The agent sees compact text rendered in the plugin, never JSON.
- Writes are prepared before they apply. `preview:true` returns the prepared plan. A batch is one Navisworks transaction, so it is one undo step.
- Lengths are mm, areas m2, volumes m3, angles degrees, dates ISO.
- The plugin writes clash reports itself, as HTML or CSV with per-result images.

## Synthesis decisions

**Tools are split by effect (from A).** `status`, `read`, `apply`, `export`, `job`, `script`, `help`. `read` never changes anything, so clients can auto-approve it. Every change goes through `apply`, which takes a list of ops, so batching needs no extra tool. B's domain tools plus `nw_batch` gave each write two entry points, which is the "two ways to do one task" red flag. Tool names carry no `nw_` prefix, because MCP clients already namespace them.

**Signatures are a small string DSL (from A).** `Op("color", "items:target rgb:color; temp:bool", Model.Color, ...)`. One parser turns each signature into the JSON Schema, the argument validator and the `help` line. B's typed record per op is safer per op but costs a class per op across about 60 ops. The DSL validates at the boundary, and handlers read already-checked values.

**The catalog reaches the server at runtime, not through a build-time dumper (new).** Both candidates generated `catalog.json` at build time by loading the plugin DLL outside Navisworks. That needs mixed-mode Navisworks assemblies to load out of process, plus a hash check to catch drift. Instead, the plugin writes `%LOCALAPPDATA%\navisworks-mcp\catalog.json` every time it loads, and sends the same catalog in its `hello`. The server lists tools from that file at startup. If the catalog it receives in `hello` differs, the server swaps the catalog and sends `notifications/tools/list_changed`. The file is always written by the code that runs the ops, so nothing can drift and no hash is needed. Cost: before Navisworks has ever loaded the plugin on a machine, the server lists only `status`, and `status` says to start Navisworks.

**A shim plugin loads the core (new).** The installed bundle holds a small `NavisworksMcp.dll`, which is the `EventWatcherPlugin`. On load it calls `Assembly.LoadFrom` on `NavisworksMcp.Core.dll`. By default the core loads from the bundle, and the env var `NAVISWORKS_MCP_CORE` overrides that path. Tests launch Navisworks with that variable pointing at their own build. As a result, several worktrees can test at once, and rebuilding never fights a DLL that a running Navisworks has locked.

**One handle space with kinds (from A).** `r#` handles hold items, clash results, tasks or takeoff rows. An op that wants items accepts a clash handle and uses both items of each result. B's separate `g#/c#/t#/v#` prefixes made the agent learn six namespaces.

**No idempotency ledger (dropped from A).** The server never retries on its own. A write that was in flight when the pipe broke is reported as unknown, with `next: status, then check before repeating`. A ledger only pays off when the server retries automatically.

**Script uses CodeDom (from B).** `CSharpCodeProvider` ships with .NET Framework, so production machines need no Build Tools. The cost is C# 5 syntax.

**Clash grouping is native where the API allows it.** First try to persist groups with `TestsAddCopy(parent, ClashResultGroup)`, so they show in Clash Detective and save with the NWF. If that fails, fall back to grouping only when results are read (B), and record which one shipped here.

## Module map

```
plugin/shim/Shim.cs              EventWatcherPlugin; loads Core; forwards OnLoaded/OnUnloading
plugin/core/Host.cs              UI marshaller, pipe server, jobs, instance + catalog files
plugin/core/Catalog.cs           Op table, signature DSL -> schema/validator/help, Run (errors -> code/next)
plugin/core/Targets.cs           target grammar, handle table, where DSL -> Search
plugin/core/Text.cs              renderer and units
plugin/core/Ops/*.cs             one file per domain: Model, Sets, Views, Clash, Timeliner, Takeoff, Document, Export, Script
server/main.ts                   MCP over stdio; tools from catalog; forwards calls
server/link.ts                   instance discovery, connection, hello, reconnect
test/*.test.ts                   node:test e2e; each file launches its own Navisworks
```

A call goes `main.ts` to `link.ts` in the server, then `Host.cs` to `Catalog.cs` to `Ops/<Domain>.cs` in the plugin.
