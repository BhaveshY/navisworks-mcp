# Candidate A: plugin-owned catalog, thin relay, one write tool

## Problem

An agent must drive Navisworks Manage 2024 for quick QA questions and for full coordination work: properties, clash, viewpoints and outputs, TimeLiner, Quantification. Every Navisworks call must run on the UI thread inside Roamer.exe. Clash runs and appends block that thread for minutes. Items have no stable ids. Several instances can run at once, and the user's own instance must never be touched by tests. MCP clients call `tools/list` before Navisworks may be running. The shape is not obvious because three forces pull apart: tool definitions want to sit next to the C# code that implements them, the MCP server must list tools without Navisworks, and the token budget favours few tools with dense output.

Constraints honoured: C# plugin on .NET 4.8 built with csc, `EventWatcherPlugin`, UI-thread marshalling through a hidden `Control`, named pipe, Node 24 TypeScript with erasable syntax only, end-to-end tests only. APIs named below were checked against `Autodesk.Navisworks.Api.xml` and `docs/api/*.txt` (reflection dumps). Unchecked ones are listed under risks.

## Usage (caller's view)

### Server instructions (what the agent reads once)

```
Navisworks Manage 2024. Start with nw_status. Lengths mm, areas m2, volumes m3, angles deg, dates ISO.
Read with nw_read. Lists return a handle r#: pass items:"r3", "r3#0", "selection", "last",
"set:Folder/Name", "id:Element ID=412233", "path:MEP.nwc/Level 1" instead of copying rows.
Totals are exact. Continue a list with nw_read {from:"r3", page:2}.
Change anything with ONE nw_apply holding all ops: pre-validated, one undo step,
"$0" is the result of op 0, preview:true is a dry run. Files out (image, NWD, NWF, reports,
exports) go through nw_export. Long work returns job j#: call nw_job. Never repeat a write
that is running or unknown; call nw_job. Every error ends with "next:": do exactly that.
nw_help {tool, op} prints the signature and a working example. nw_script runs C# for the rest.
```

### Task 1: QA. Ducts without a system name, colour, save, capture. 4 calls

```
> nw_status {}
nw #1 pid 18244 Hospital.nwf · units mm · models 3: Architecture.nwc MEP.nwc Structure.nwc
412,880 items · selection 0 · jobs none

> nw_read {what:"items", where:"Element.Category = Ducts and !has Mechanical.System Name",
           cols:"Element.Level", group:"Element.Level"}
r1 items 38 (exact) · by Element.Level: Level 1 21 · Level 2 17
#|name|Element.Level|source
0|Rectangular Duct|Level 1|Element ID 412233
1|Rectangular Duct|Level 1|Element ID 412240
[36 more rows in the real reply]

> nw_apply {ops:[
    {op:"color", items:"r1", rgb:"#ff0000"},
    {op:"set.save", name:"QA/Ducts no system", from:"r1", live:true},
    {op:"zoom", items:"r1"},
    {op:"viewpoint.save", name:"QA/Ducts no system"}]}
w1 applied 4 ops · 1 undo step
0 colour #FF0000 on 38 items (permanent)
1 search set "QA/Ducts no system" (live, 38 items now)
2 zoomed to 38 items
3 viewpoint "QA/Ducts no system"
undo: nw_apply {ops:[{op:"undo"}]}

> nw_export {out:[{what:"image", view:"QA/Ducts no system", w:1600, h:900}]}
image C:\Users\...\Temp\nwmcp\img-0001.png 1600x900   [+ MCP image content]
```

`live:true` works because handle r1 remembers the `where` that made it. The set is saved as a search set, not a frozen list.

### Task 2: clash. New test, run, group by level, assign, report, save. 5 calls

```
> nw_read {what:"sets", under:"Disciplines"}
sets 4 (exact) under Disciplines
#|name|kind
0|Disciplines/ARC|search
1|Disciplines/MEP|search
2|Disciplines/STR|search
3|Disciplines/Services exc. sleeves|selection

> nw_apply {ops:[
    {op:"clash.add", name:"MEP vs STR", a:"set:Disciplines/MEP", b:"set:Disciplines/STR",
     type:"hard", tol:10, rules:["same file"]},
    {op:"clash.run", test:"$0"}]}
w2 running as j1 · done: 0 clash test "MEP vs STR" (hard, 10 mm, ignore same file)
running: 1 clash.run "MEP vs STR" (started 25 s ago)
next: nw_job {id:"j1", wait:120}

> nw_job {id:"j1", wait:120}
j1 done in 74 s · w2 applied 2 ops · 1 undo step for op 0; clash.run is not undoable
1 "MEP vs STR": 1,284 results · new 1,284

> nw_apply {ops:[
    {op:"clash.group", test:"MEP vs STR", by:"item1.Element.Level"},
    {op:"clash.edit", items:"$0", status:"active", assign:"MEP Lead",
     comment:"Coordination 2026-10-29"}]}
w3 applied 2 ops · 1 undo step
0 grouped 1,284 results into 4 groups by item1 Element.Level: Level 1 402 · Level 2 377 · Level 3 311 · Roof 194
1 edited 4 groups (1,284 results): status active · assigned MEP Lead · +1 comment

> nw_export {out:[
    {what:"clash_report", test:"MEP vs STR", format:"html", path:"C:\\Coord\\MEPvsSTR.html"},
    {what:"nwf", path:"C:\\Coord\\Hospital.nwf"}]}
0 report C:\Coord\MEPvsSTR.html · 4 groups · 1,284 results · 4 images · 2.1 MB
1 saved C:\Coord\Hospital.nwf (NWF 2024)
```

### Task 3: 4D. Import programme, auto-attach, fix gaps, capture a date. 5 calls

```
> nw_apply {ops:[
    {op:"tl.import", csv:"C:\\Coord\\programme.csv",
     map:{id:"Task ID", name:"Task Name", start:"Start", end:"Finish", type:"Type"}},
    {op:"tl.attach", tasks:"$0", match:"name=set"}]}
w4 applied 2 ops · 1 undo step
0 imported 146 tasks from programme.csv (data source "programme.csv") → r2
1 attached 131 of 146 tasks to search sets by name · 15 unmatched → r3

> nw_read {from:"r3", cols:"start,end,type"}
r3 tasks 15 (exact)
#|name|start|end|type
0|L2 Slab pour|2026-02-02|2026-02-13|Construct
1|L2 Columns|2026-02-16|2026-02-27|Construct
[13 more rows]

> nw_read {what:"values", prop:"Element.Category", where:"Element.Level = Level 2"}
values Element.Category in 9,112 items (exact)
Floors 14 · Structural Columns 86 · Walls 412 · Ducts 1,021 · [5 more]

> nw_apply {ops:[
    {op:"tl.attach", tasks:"r3#0", where:"Element.Level = Level 2 and Element.Category = Floors"},
    {op:"tl.attach", tasks:"r3#1", where:"Element.Level = Level 2 and Element.Category = Structural Columns"}]}
w5 applied 2 ops · 1 undo step
0 "L2 Slab pour" ← search (14 items now)
1 "L2 Columns" ← search (86 items now)

> nw_export {out:[{what:"image", sim:"2026-02-20", w:1600, h:900},
                  {what:"nwf", path:"C:\\Coord\\Hospital-4D.nwf"}]}
0 image C:\...\nwmcp\img-0002.png 1600x900 · simulation at 2026-02-20 · 58 tasks active
1 saved C:\Coord\Hospital-4D.nwf (NWF 2024)
```

### Short reads

```
> nw_read {what:"props", items:"r1#0", tabs:"Element,Mechanical"}
r1#0 Rectangular Duct · MEP.nwc/Level 1/Ducts/Rectangular Duct
[Element] Name=Rectangular Duct · Category=Ducts · Id=412233 · Width=600 mm · Height=300 mm
[Mechanical] System Type=Supply Air · Flow=0.42 m3/s

> nw_read {what:"qto", level:"totals", by:"item"}
qto totals by item 6 (exact)
#|item|wbs|count|length|area|volume
0|Concrete/Slab 250|1.1|42|-|1,840.2 m2|460.05 m3
[5 more rows]
```

Call counts: task 1 = 4, task 2 = 5, task 3 = 5. A seasoned agent skips `nw_status` when the pin is unambiguous, which drops task 1 to 3.

## Shape

### 1. Single source of truth: the C# catalog

The plugin owns every tool, op, parameter, doc line and example in one array in `Catalog.cs`. Each op entry binds its signature to its handler in the same expression, so the list of ops and the list of handlers cannot drift:

```csharp
Op("items", "; where:where in:target cols:cols group:prop sort:prop limit:int page:int",
   Model.Items, "Search model items -> r#",
   ex: "{what:\"items\", where:\"Element.Category = Walls\", limit:5}")
```

The signature string is a tiny DSL (required before `;`, optional after; types `str int num bool len color date path target where cols prop enum(a|b) json ops`). One parser turns it into (a) the JSON Schema fragment for MCP, (b) the runtime validator that builds `Args`, (c) the `nw_help` line.

The TypeScript server needs the catalog before Navisworks runs, so the build derives it: `build.ps1` compiles the plugin, then compiles and runs `tools/CatalogDump.cs`, which loads the plugin DLL (with an `AssemblyResolve` hook to the Navisworks folder, the same way the reflection dump worked) and writes `server/catalog.json` = `{hash, tools[], help{}}`. `hash` is SHA-256 of the canonical catalog JSON and is also compiled into the plugin. On connect the plugin's hello carries its hash. A mismatch fails every call with `next: run build.ps1 and restart Navisworks`. There is no second list anywhere, and stale artifacts fail loudly (per single source of truth, hand-synced list).

The e2e suite derives its smoke cases from the same catalog: every op's `ex` runs against the sample models. An op without a working example fails the build-time dump.

### 2. Wire protocol

Discovery. On load the plugin writes `%LOCALAPPDATA%\navisworks-mcp\instances\<pid>.json` = `{pid, pipe, tag, doc, started, hash}` and deletes it on unload. `tag` comes from env `NWMCP_TAG` (tests set it). Pipe name `navisworks-mcp-<pid>`. The server prunes files whose pid is dead.

Pinning. The server pins one instance per session: env `NWMCP_TARGET` (a tag) wins; else the only live instance; else the call fails with a list and `next: nw_status {target:"#2"}`. When more than one instance is live, every reply starts with `[#2 Hospital.nwf]`.

Framing. NDJSON over the pipe, UTF-8, one JSON object per line. JSON escapes newlines, so no length prefix is needed. Several concurrent connections are allowed (one per MCP server process).

```
→ {"id":7,"tool":"nw_apply","args":{...},"key":"s9f2:31","wait":25000}
→ {"cancel":7}
← {"id":7,"ok":true,"text":"w1 applied ...","image":"C:\\...\\img-0001.png"}
← {"id":7,"ok":true,"text":"w2 running as j1 ...","job":"j1"}
← {"id":7,"ok":false,"error":{"code":"NOT_FOUND","message":"no set \"Disciplines/MPE\"","next":"nw_read {what:\"sets\"} and use an exact name"}}
```

Every request is a job. The pipe thread wraps each request in a `Job`, posts it to the UI thread with `BeginInvoke`, and waits up to `wait` ms. Done in time: the reply is inline. Not done (still running, or queued behind a clash run): the reply carries `job`. One mechanism covers fast calls, long calls and queueing. `nw_job` and `nw_status` are answered on the pipe thread from the job table and an immutable `Snapshot`, so they work while the UI thread is blocked.

Cancellation. MCP `notifications/cancelled` becomes `{"cancel":id}`; `nw_job {cancel:true}` does the same. The flag is checked between batch ops, between clash tests in `clash.run {test:"all"}`, between files in `doc.append`, and every 1,000 items in scans. A single `TestsRunTest` cannot be interrupted; the reply says so.

Idempotency. Writes (`nw_apply`, `nw_export`, `nw_script`) carry `key` = `<session>:<mcp request id>`. The plugin's `Ledger` maps key to job. A resend after reconnect returns the stored job instead of running twice (per make-operations-idempotent).

Reconnect. A broken pipe marks the link down. The next call rescans discovery, prefers the same tag, and reconnects. Writes in flight at the break are reported as `unknown w7: Navisworks exited during the write. next: nw_status, then check the effect before repeating`. Handles carry the plugin's start time; a handle from a dead instance fails with `next: rerun the nw_read that made r3`.

Errors. `NwError(code, message, next)` is the only error type. Codes: `BAD_ARGS NOT_FOUND STALE_HANDLE BUSY NO_DOC MISMATCH API_FAILED CANCELLED`. Unexpected exceptions become `API_FAILED` with `next: nw_help {tool, op}` or a specific hint when the exception type is known.

### 3. Tool surface: 7 tools

Rule an agent can hold: `nw_read` never changes anything, `nw_apply` changes the document or view, `nw_export` writes files. No op exists in two tools (per two ways to do one task).

| tool | args | role |
|---|---|---|
| `nw_status` | `; target:str` | instances, pin, doc, models, units, counts, selection, running jobs |
| `nw_read` | `what:enum; ...` or `from:r#; page:int cols:cols` | every read; lists return handles |
| `nw_apply` | `ops:ops; preview:bool label:str` | every change, one undo step |
| `nw_export` | `out:json[]` | image, NWD, NWF, reports, CSV, FBX, DWFx |
| `nw_job` | `id:str; wait:int cancel:bool` | job status, wait, cancel |
| `nw_script` | `code:str; preview:bool` | C# escape hatch |
| `nw_help` | `; tool:str op:str` | signature + example, answered by the server from catalog.json |

Addressing (`target`, one grammar, one parser in `Targets.cs`): `r3`, `r3#5` (row 5), `r3#0-49`, `selection`, `last` (per connection), `$0` (result of op 0 in this batch), `set:Folder/Name`, `id:<Tab.Prop>=<value>` (source ids such as `id:Element ID=412233`, `id:IFC.GlobalId=2O2Fr$t4X7Zf8NOew3FLOH`), `guid:<InstanceGuid>`, `path:MEP.nwc/Level 1/Ducts`, `clash:<test>` (all results), and arrays of these (union). Handles have a kind (`items clashes tasks qto sets viewpoints`); an op that wants items given a `clashes` handle uses item1+item2 of each result, any other mismatch is `BAD_ARGS`.

`where` grammar maps one to one onto `Search`: `Tab.Prop OP value` with `= != ~(contains) *(wildcard) > < >= <=`, `has Tab.Prop`, `!` negation, joined by `and`/`or`. No parentheses: `or` starts a new `SearchCondition.StartGroup`, which is exactly the API's or-of-ands. Tab and property are display names; quote them when they contain a dot. Values with units accept `3 m`, `250mm`. For clashes and tasks the left side is a field (`status`, `assign`, `distance`, `start`) or `item1.Tab.Prop`.

Paging. Default `limit` 50, max 500. Totals are exact: a handle stores its full result list, so `page` is stable. `group` and `values` scan properties of every item in the handle; above 20,000 items they become a job automatically.

Units. Inputs: lengths mm, or a unit string. Outputs: lengths mm, areas m2, volumes m3, angles deg, dates ISO local. `VariantData` lengths are converted from `Document.Units`. Strings and non-unit doubles print as Navisworks displays them.

`nw_read` whats:

| what | args | returns |
|---|---|---|
| `items` | `; where in cols group sort limit page` | r# items |
| `selection` | `; cols` | r# items |
| `tree` | `; under:target depth:int` | children with descendant counts |
| `props` | `items:target; tabs:strs` | all properties, max 20 items |
| `values` | `prop:prop; in:target where:where top:int` | distinct-value histogram |
| `stats` | `; by:prop` | per model: file, units, items, geometry items, bbox, category counts |
| `sets` / `viewpoints` | `; under:str` | tree with kinds |
| `tests` | `; ` | clash tests: type, tol, status, last run, counts by status |
| `clashes` | `test:str; where group limit page` | r# clashes with group counts |
| `tasks` | `; where under cols limit page` | r# tasks |
| `qto` | `level:enum(catalog\|takeoffs\|totals); by:enum(item\|wbs\|group) where limit page` | r# qto |

`nw_apply` ops (signature after the name; `;` marks optional):

```
model     color items rgb; temp            transparency items pct; temp
          hide items                       show items           isolate items
          reset ; items what:enum(color|transparency|hidden|all)
          select items; mode:enum(set|add|remove)
props     props.set items tab props:json      (user-defined tab, COM)
          props.delete items tab; props:strs
sets      set.save name; from items live:bool   set.rename name to   set.delete name
views     viewpoint.save name                  viewpoint.show name   viewpoint.rename name to
          viewpoint.delete name                zoom items
          section planes:json | box:target; pad:len | off:bool
clash     clash.add name a b; type:enum(hard|hardc|clearance|duplicate) tol:len rules:strs
          clash.update test; a b type tol rules name     clash.delete test
          clash.run test; ("all" allowed)                clash.group test by; status
          clash.ungroup test                             clash.edit items; status assign comment approve:bool
timeliner tl.add name start end; type parent items where   tl.update tasks; name start end type pct
          tl.delete tasks     tl.import csv map:json; replace:bool     tl.attach tasks; match items where set
          tl.sim at:date
qto       qto.import catalog:path               qto.item.add group name; wbs formulas:json
          qto.item.update item; name wbs formulas qto.item.delete item
          qto.takeoff items item                qto.delete items
document  doc.open path    doc.append paths    doc.merge paths    doc.refresh    doc.remove model
undo      undo
```

Batching and undo. `nw_apply` runs in two phases with a type boundary between them. Phase 1 calls every op's `Prepare`, which parses args, resolves targets, checks names, and returns a `Step`. Nothing in the document changes. Any failure stops the batch with the op index and `next`. `$n` refs are checked in phase 1 against the earlier step's declared `Produces` kind. Phase 2 opens one `Document.BeginTransaction(label)`, runs the steps, commits. One Navisworks undo entry covers the batch. `Transaction` has no rollback in 2024 (its doc says "in the future may support rollback"), so on a phase-2 failure the plugin commits and calls `Document.TryUndo()` once, then reports the batch as rolled back. Quantification steps run inside a `NavisworksTransaction` on `DocumentTakeoff.Database`, which does roll back. Steps marked `Undoable=false` (`clash.run`, `doc.*`, `tl.import`, `qto.import`, possibly `props.*`) are listed in the preview with `[no-undo]` and must come after all undoable steps; phase 1 rejects other orders with `next` showing the reordered batch. `{op:"undo"}` undoes the last MCP write only if no user edit was observed since (see risks).

Preview. `preview:true` runs phase 1 only and prints each step's `Plan` line: `0 colour #FF0000 on 38 items (permanent)`. This is the same string the apply prints, in future tense, so preview and result cannot disagree.

Exports (`nw_export {out:[...]}`, run in order, one job): `image {; view sim:date w h style:enum(scene|overlay|raytrace) path section:bool}` via `GenerateImage`; `nwd {path}` via `PublishFile`/`SaveFile`; `nwf {path}` via `SaveFile`; `clash_report {test; format:enum(html|csv) images:bool path}` rendered by the plugin from results plus `TestsImageForResult`; `tasks_csv`, `qto_csv` rendered by the plugin; `fbx`, `dwfx` via COM `InwOpState10.DriveIOPlugin` and `Document.ExportAsDwf`. Images are returned as MCP image content when `inline:true`, else as a path.

Script. `nw_script {code}` compiles a method body `object Run(Nw nw)` with the VS Build Tools `csc.exe` (path recorded by `build.ps1`) on the pipe thread, loads the bytes, and runs it on the UI thread inside a transaction. `Nw` exposes `Doc`, `Items(target)`, `Handle(items)` (returns a new r#), `Table(cols, rows)`. The return value goes through the same renderer. `preview:true` compiles only and returns diagnostics with line numbers. A script is a write with a w# and ledger key.

### 4. Output format: compact text, rendered in the plugin

The agent sees text, never JSON. Shapes:

- Head line: `<handle> <kind> <total> (exact)[ · by <prop>: k n · k n]`.
- Table: header `#|col|col`, rows pipe-separated, no padding. `|` in values becomes `\|`. Empty cells are `-`.
- Footer only when more exist: `more: nw_read {from:"r3", page:2}`.
- Write report: `w# applied N ops · 1 undo step`, then `<i> <plan line, past tense>`, then the undo hint.
- Error: `error <CODE>: <message>\nnext: <exact call or action>`.

Pipe tables cost about half the tokens of aligned columns and a third of JSON arrays of objects. Rendering lives in the plugin because only the plugin knows units, kinds and handles; the server returns `text` verbatim (per information hiding: the server knows nothing about Navisworks).

### 5. Module map and type sketches

```
plugin/src/Plugin.cs      EventWatcherPlugin, UI marshaller, pipe server, discovery, jobs, ledger, snapshot
plugin/src/Wire.cs        Request, Reply, NwError, NDJSON frames (private to Plugin.cs callers)
plugin/src/Catalog.cs     ToolSpec/OpSpec table, signature DSL, schema + help gen, Args, Dispatch, Apply phases
plugin/src/Targets.cs     Target + Where grammars, Handles store, Resolve
plugin/src/Text.cs        Table, Counts, Value formatting, Error
plugin/src/Ops/Model.cs   items tree props values stats selection, colour/hide/props/sets
plugin/src/Ops/Views.cs   viewpoints, zoom, section, image
plugin/src/Ops/Clash.cs   tests, clashes, clash.* , clash_report
plugin/src/Ops/Timeliner.cs  tasks, tl.*, tasks_csv
plugin/src/Ops/Qto.cs     qto reads, qto.*, qto_csv
plugin/src/Ops/Files.cs   doc.*, nwd, nwf, fbx, dwfx
plugin/src/Ops/Script.cs  nw_script compile and run
tools/CatalogDump.cs      build-time: writes server/catalog.json
server/main.ts            MCP JSON-RPC over stdio, tools/list from catalog.json, help, tool calls
server/link.ts            discovery, pin, pipe, NDJSON, reconnect, keys, cancel
build.ps1                 csc plugin, csc + run CatalogDump, write bundle
```

Call chain for any tool: `Plugin.cs` (frame, job) → `Catalog.cs` (validate, dispatch, apply phases) → `Ops/<Domain>.cs`. `Targets.cs` and `Text.cs` are leaf libraries called from Ops. Three files to trace. Ops classes are `internal static`; nothing in Ops can be reached from outside the assembly, and Ops never touch `Wire.cs` types (per boundary-discipline).

```csharp
// Wire.cs
namespace System.Runtime.CompilerServices { internal static class IsExternalInit {} }
internal sealed record Request(long Id, string Tool, JObject Args, string Key, int WaitMs);
internal sealed record Reply(long Id, bool Ok, string Text, string Image, string Job, ErrorBody Error);
internal sealed record ErrorBody(string Code, string Message, string Next);
public sealed class NwError : Exception {
    public string Code { get; } public string Next { get; }
    public NwError(string code, string message, string next) : base(message) => throw new NotImplementedException();
}
internal static class Frames {
    /// Reads one NDJSON line. Returns null at end of stream. A {"cancel":n} line yields Cancel.
    public static Frame Read(StreamReader r) => throw new NotImplementedException();
    public static void Write(StreamWriter w, Reply reply) => throw new NotImplementedException();
}
internal abstract record Frame; internal sealed record Call(Request R) : Frame; internal sealed record Cancel(long Id) : Frame;

// Plugin.cs
[Plugin("NavisworksMcp", "NMCP")]
public sealed class McpPlugin : EventWatcherPlugin {
    public override void OnLoaded() => throw new NotImplementedException();   // ui control, Host.Start
    public override void OnUnloading() => throw new NotImplementedException(); // delete discovery file
}
internal sealed class Host {
    readonly Control ui; readonly Jobs jobs = new Jobs(); readonly Ledger ledger = new Ledger();
    /// Written only on the UI thread after each request and on document events; read lock-free.
    volatile Snapshot snapshot;
    public static Host Start(Control ui) => throw new NotImplementedException();
    void Serve(NamedPipeServerStream pipe) => throw new NotImplementedException(); // one thread per connection
    /// Every call becomes a Job. Inline reply if done within WaitMs, else a job reply.
    Reply Handle(Request r, Connection c) => throw new NotImplementedException();
}
internal sealed record Snapshot(string Doc, string Units, string[] Models, int Items, int Selected, string[] Jobs);
internal sealed class Connection { public int? Last; public string Session; }   // per-actor state: "last" never leaks across clients
internal sealed class Job {
    public string Id; public string Title; public volatile bool CancelRequested;
    public volatile string Progress; public Reply Result; public readonly ManualResetEventSlim Done = new(false);
    public void ThrowIfCancelled() => throw new NotImplementedException();
}
internal sealed class Jobs { public Job Start(string title, Func<Job, Reply> work, Control ui) => throw new NotImplementedException();
                             public bool TryGet(string id, out Job j) => throw new NotImplementedException(); }
internal sealed class Ledger { public Job GetOrAdd(string key, Func<Job> start) => throw new NotImplementedException(); }

// Catalog.cs
internal enum PType { Str, Int, Num, Bool, Len, Color, Date, Path, Target, Where, Cols, Prop, Enum, Json, Ops }
internal sealed record Param(string Name, PType Type, bool Optional, string[] Choices);
internal abstract record Handler;
internal sealed record ReadHandler(Func<Ctx, Args, string> Run) : Handler;     // returns rendered text
internal sealed record WriteHandler(Func<Ctx, Args, Step> Prepare) : Handler;  // no side effects
internal sealed record OpSpec(string Tool, string Name, Param[] Params, string Doc, string Example, Handler Handler);
internal sealed record ToolSpec(string Name, string Summary, OpSpec[] Ops);
/// A prepared write. Run is the only code that mutates Navisworks.
internal sealed record Step(string Plan, bool Undoable, Kind? Produces, Func<RunCtx, StepResult> Run);
internal sealed record StepResult(string Line, int? Handle);
internal sealed class Ctx { public Document Doc; public Handles Handles; public Connection Conn; public Job Job; }
internal sealed class RunCtx { public Ctx Ctx; public IReadOnlyList<StepResult> Earlier; }
internal sealed class Args {
    public string Str(string n) => throw new NotImplementedException();
    public double Len(string n) => throw new NotImplementedException();   // mm
    public Target Target(string n) => throw new NotImplementedException();
    public Where Where(string n) => throw new NotImplementedException();
    public bool Has(string n) => throw new NotImplementedException();
}
internal static class Catalog {
    internal static readonly ToolSpec[] Tools = { /* the single list */ };
    public static readonly string Hash;
    static Param[] ParseSig(string sig) => throw new NotImplementedException();
    public static string ToJson() => throw new NotImplementedException();      // MCP schemas + help + hash
    public static Args Validate(OpSpec op, JObject raw) => throw new NotImplementedException();
    public static Reply Dispatch(Ctx ctx, Request r) => throw new NotImplementedException();
    /// Phase 1 all Prepare; phase 2 one Transaction; TryUndo on failure. Preview stops after phase 1.
    static string Apply(Ctx ctx, OpSpec[] ops, Args[] args, bool preview, string label) => throw new NotImplementedException();
}

// Targets.cs
internal enum Kind { Items, Clashes, Tasks, Qto, Sets, Viewpoints }
internal abstract record Target;
internal sealed record HandleRef(int N, int From, int To) : Target;  internal sealed record SelectionRef : Target;
internal sealed record LastRef : Target;  internal sealed record StepRef(int Index) : Target;
internal sealed record SetRef(string Path) : Target;  internal sealed record SourceId(string Tab, string Prop, string Value) : Target;
internal sealed record GuidRef(Guid Id) : Target;  internal sealed record TreePath(string[] Parts) : Target;
internal sealed record ClashTestRef(string Test) : Target;  internal sealed record Union(Target[] Parts) : Target;
internal sealed record Cond(string Tab, string Prop, string Op, string Value, bool Negate);
internal sealed record Where(Cond[][] AnyOfAll) { public string Source; }   // or-of-ands, matches StartGroup
/// One owner: the plugin. Append-only; invalidated wholesale when the active document changes.
internal sealed class Handles {
    public int Add(Kind kind, string title, IReadOnlyList<object> rows, Where query) => throw new NotImplementedException();
    public HandleData Get(int n) => throw new NotImplementedException();   // STALE_HANDLE with next
}
internal sealed record HandleData(int N, Kind Kind, string Title, IReadOnlyList<object> Rows, Where Query, long DocGen);
internal static class Resolve {
    public static Target Parse(string s) => throw new NotImplementedException();
    public static Where ParseWhere(string s) => throw new NotImplementedException();
    public static Search ToSearch(Where w, ModelItemCollection scope) => throw new NotImplementedException();
    public static ModelItemCollection Items(Ctx c, Target t) => throw new NotImplementedException();
    public static IReadOnlyList<IClashResult> Clashes(Ctx c, Target t) => throw new NotImplementedException();
    public static IReadOnlyList<TimelinerTask> Tasks(Ctx c, Target t) => throw new NotImplementedException();
}

// Text.cs
internal sealed record Page(int Offset, int Limit);
internal static class Text {
    public static string Table(string head, string[] cols, IEnumerable<string[]> rows, int total, Page p, int handle) => throw new NotImplementedException();
    public static string Counts(string label, IEnumerable<KeyValuePair<string, int>> counts) => throw new NotImplementedException();
    public static string Value(VariantData v, Units model) => throw new NotImplementedException();   // mm, m2, m3, deg, ISO
    public static string Error(NwError e) => throw new NotImplementedException();
}

// Ops/Clash.cs (pattern for every Ops file)
internal static class Clash {
    public static string Tests(Ctx c, Args a) => throw new NotImplementedException();
    public static string Clashes(Ctx c, Args a) => throw new NotImplementedException();
    public static Step Add(Ctx c, Args a) => throw new NotImplementedException();
    public static Step Run(Ctx c, Args a) => throw new NotImplementedException();   // Undoable=false, checks Job between tests
    public static Step Group(Ctx c, Args a) => throw new NotImplementedException();
    public static Step Edit(Ctx c, Args a) => throw new NotImplementedException();  // TestsEditResultStatus/AssignedTo/Comments
    public static StepResult Report(RunCtx c, Args a) => throw new NotImplementedException();
}
```

```ts
// server/link.ts. Wire types stay in this file.
export type Instance = { n: number; pid: number; tag: string | null; doc: string; hash: string };
export type Outcome = { text: string; image?: string; isError: boolean };
type WireReply = { id: number; ok: boolean; text?: string; image?: string; job?: string;
                   error?: { code: string; message: string; next: string } };
export class Link {
  constructor(opts: { catalogHash: string; targetTag: string | null }) { throw new Error('not implemented'); }
  instances(): Instance[] { throw new Error('not implemented'); }             // scans discovery dir, prunes dead pids
  pin(selector: string): Instance { throw new Error('not implemented'); }      // "#2", pid, or tag
  /** Sends one call. key is set for writes. Reconnects once on a broken pipe; never resends a keyless write. */
  call(tool: string, args: unknown, key: string | null, signal: AbortSignal): Promise<Outcome> {
    throw new Error('not implemented');
  }
}

// server/main.ts
type Catalog = { hash: string; tools: { name: string; description: string; inputSchema: object }[];
                 help: Record<string, string>; writes: string[] };
function loadCatalog(path: string): Catalog { throw new Error('not implemented'); }
/** Hand-rolled MCP over stdio: initialize, tools/list, tools/call, notifications/cancelled. */
function serve(cat: Catalog, link: Link): void { throw new Error('not implemented'); }
async function callTool(cat: Catalog, link: Link, name: string, args: unknown,
                        mcpId: string | number, signal: AbortSignal): Promise<object> {
  // nw_help: answered from cat.help. nw_status with no live instance: answered here with install hint.
  // Otherwise link.call(name, args, cat.writes.includes(name) ? `${session}:${mcpId}` : null, signal)
  throw new Error('not implemented');
}
```

Data flow for task 2, op 1: `main.ts` gets `tools/call nw_apply` → `link.call` frames it with key → `Host.Handle` creates job, ledger entry, `BeginInvoke` → `Catalog.Dispatch` → `Validate` per op → `Clash.Add.Prepare`, `Clash.Run.Prepare` (resolves `set:` refs via `Resolve.Items`, checks `$0` kind) → `Transaction` → `Step.Run` → pipe thread wait hits 25 s → job reply → later `nw_job` reads `Jobs` on the pipe thread.

Tests. A test sets `NWMCP_TAG=t-<uuid>`, launches `Roamer.exe` with a sample model, starts `node server/main.ts` with `NWMCP_TARGET` set to the same tag, and drives stdio. The user's instance is never pinned because its tag differs. One test per catalog example, plus the three transcripts above as scripted scenarios with exact-text assertions on head lines.

## Synthesis decision

Filled in by arena.

## Tradeoffs accepted

- We accept a build step that runs a dumper exe in exchange for a catalog with one owner, next to the handlers, and a server that never needs editing when an op is added.
- We accept one large `nw_apply` op vocabulary (about 45 ops) in exchange for one write path, one undo story, one preview mechanism and batch-level round-trip savings. Tool schema stays generic (`ops: object[]`); the op list lives in the description as signature lines, about 900 tokens.
- We accept that rendering lives in C# in exchange for a server with zero Navisworks knowledge. Text tweaks need a plugin rebuild and a Navisworks restart.
- We accept "every request is a job" overhead (a job record per call, about microseconds) in exchange for one code path for fast calls, long calls and calls queued behind a blocked UI thread.
- We accept rollback by commit-then-`TryUndo` for document parts in exchange for not waiting on a rollback API that 2024 lacks. Non-undoable ops are forced to the end of a batch.
- We accept a hand-rolled MCP layer (about 150 lines) in exchange for zero npm dependencies and no build on the Node side. The protocol surface used is four messages.
- We accept handles that hold `ModelItem` references and die with the document in exchange for exact totals and stable paging without re-running searches.
- We accept our own clash report renderer (HTML/CSV with `TestsImageForResult` images) over the native `LcClClashReport` interop in exchange for control over format and no dependency on internal report drivers.

## Alternatives considered

- Catalog in TypeScript, plugin generic. The server would own schemas and docs; the plugin would expose `(op, args)` handlers keyed by string. It lost because the op list then exists twice (TS schema list and C# handler table) and only a runtime check catches drift. It also forces parsing knowledge (units, targets) into two languages.
- Catalog fetched live from the plugin at connect. Shallowest build, but `tools/list` would be empty or stale before Navisworks starts, and clients cache tool lists. Rejected for that reason, though the hash check borrows its idea.
- One tool per domain with reads and writes mixed (`nw_clash`, `nw_timeliner`, `nw_qto`, like the Revit server's family of tools). Fewer ops per tool, but a coordination task that touches sets, clash and viewpoints needs one round trip per domain and loses single-step undo. It also creates two ways to save a viewpoint (from a clash tool and from a view tool).
- Length-prefixed binary framing. No gain over NDJSON at these sizes; harder to debug by hand.
- JSON output to the agent. Easier to parse for programs, about 2 to 3 times the tokens for tables. The agent is the only consumer.

## Open questions and risks

- Does COM `InwGUIPropertyNode2.SetUserDefined` participate in Navisworks undo? If not, `props.*` is `[no-undo]` and must move to the end of batches. Spike first.
- `ClashResultGroup` exists, but is `TestsInsertCopy` with a group plus moved results the right way to build groups through the API? Not verified. If not, `clash.group` needs the COM or `LcClClash*` interop path.
- How should `clash.add rules` map to `ClashTest.IgnoreRules` (`RuleCollection`)? Rule names come from `LcClClashRules.LoadBaseRuleSet`, which is interop. Is an interop dependency acceptable here?
- `tl.sim` and `image {sim}` rely on interop `LcTlSimulationHelper.SetSimulationTime` (present in the dump, undocumented). Acceptable?
- `tl.import` needs the CSV data source provider id and field mapping through `TimelinerDataSource` + `TimelinerDataSourceProvider.ImportTasks`. The CSV provider's id string is not yet known.
- Box sectioning goes through `View.SetClippingPlanes(json)`. The JSON schema for box mode must be captured from `GetClippingPlanes` after setting a box in the GUI.
- `{op:"undo"}` must know that no user edit happened since the last MCP write. Is there a document-level transaction event good enough to detect user edits, or should undo only be offered inside the same reply?
- `BeginInvoke` work runs when the UI thread pumps, including inside modal dialogs. Should the plugin refuse work while a modal dialog is open, and can it detect that?
- Appends of missing or corrupt files may raise GUI dialogs that block the UI thread forever. Is `TryAppendFile` dialog-free?
- Is about 900 tokens of `nw_apply` description acceptable, or should rarely used ops (qto formulas, doc.remove) be visible only through `nw_help`?

## Next implementation step

Write `Catalog.cs` with the signature DSL parser, `ToJson`, and three real ops (`items`, `color`, `set.save`), plus `tools/CatalogDump.cs` and `build.ps1`, and prove one e2e test can call `nw_read {what:"items"}` against `Architecture.nwc` through the generated catalog.
