# Candidate B: Navisworks Manage 2024 MCP server

## Problem

An agent drives Navisworks through MCP. Two processes are fixed: a C# plugin on the Navisworks UI thread and a Node process speaking MCP over stdio, joined by a named pipe. Model items have no stable id. Long calls block the UI thread. Several Navisworks instances can run. Only end-to-end tests exist, so every seam has to be observable from the outside. The user's other CAD servers set the style: start with status, handles instead of copied ids, exact totals, one undo step per batch, every error ends with a fix hint, a script escape hatch.

The non-obvious part is where knowledge lives. The plugin is the only process that knows what Navisworks can do and what the result means. This design puts tool definitions, validation, execution, and rendering in the plugin, and makes the server generic. The server has no per-tool code.

## Usage (caller's view)

Server instructions the agent reads once:

> Start with `nw_status`. Lengths are mm, angles degrees. `nw_find` returns a handle `r#`; pass `r3`, `r3.4`, `r3[0..9]`, `sel`, `set:Name`, `path:0/12/3` or `?<where>` wherever an op takes `items`. Totals are exact; continue with `offset`. Writes apply at once and end with an undo line; `dry:true` previews. `nw_batch` runs several ops as one undo step (`$0` = first result). Long work returns `j#`; call `nw_status {op:"job", id}`. Never repeat a write whose job is running. Every error ends with `next:` do exactly that. `nw_script` runs C# for anything else.

Three production tasks as exact transcripts.

### Task 1: colour F90 walls red, save a viewpoint and an image (2 calls)

```
> nw_find {where:"Element.Category = Walls & Pset_WallCommon.FireRating = F90", show:["Element.Level"]}
r3 = 142 items (total 142) doc #1 Hospital.nwf
  r3.0  Basic Wall: Generic 200  Level 1
  r3.1  Basic Wall: Generic 200  Level 1
  ... 50 of 142, next offset 50

> nw_batch {ops:[
    {tool:"nw_items", op:"override", items:"r3", color:"#ff0000", scope:"permanent"},
    {tool:"nw_view",  op:"save", name:"F90 walls", items:"r3", fit:true},
    {tool:"nw_view",  op:"capture", width:1600, path:"C:/out/f90.png"}]}
batch ok (3 ops, 1 undo step "batch 2")
  $0 override 142 items color #ff0000 permanent
  $1 viewpoint v7 "F90 walls" saved, fitted to 142 items
  $2 image C:/out/f90.png 1600x900
undo: nw_status {op:"undo"}
```

### Task 2: clash ducts against structure, triage, report (4 calls)

```
> nw_clash {op:"create", name:"Ducts vs Structure", a:"set:MEP Ducts", b:"set:Structure", type:"hard", tolerance:10, run:true}
j4 running "Ducts vs Structure" (create + run). nw_status {op:"job", id:"j4"}

> nw_status {op:"job", id:"j4"}
j4 done 12.3s
test t3 "Ducts vs Structure" hard tol 10mm: 87 results (new 87) last run 2026-10-29 14:02

> nw_clash {op:"results", test:"t3", status:"new", by:"level", limit:5}
t3 87 results (total 87, new 87) grouped by level: 4 groups
  g1 Level 2   41  c1 c4 c7 c9 c12 ...
  g2 Level 1   30  c2 c3 c5 ...
  g3 Roof       9  c40 ...
  g4 Level 3    7  c80 ...
  c1 Duct 400x300 <-> Beam W12x26  -312mm  Level 2  B-3
  ...5 of 87, next offset 5

> nw_batch {ops:[
    {tool:"nw_clash", op:"update", results:"g1", status:"active", assign:"MEP", comment:"Reroute below beam"},
    {tool:"nw_clash", op:"report", test:"t3", format:"html", images:true, path:"C:/out/ducts.html"}]}
batch ok (2 ops, 1 undo step "batch 5")
  $0 41 results: status active, assigned MEP, comment added
  $1 report C:/out/ducts.html 87 results, 87 images
undo: nw_status {op:"undo"}
```

### Task 3: append the new structure model, stamp a user tab, save (3 calls)

```
> nw_file {op:"append", path:"C:/in/STR_v12.nwc"}
appended STR_v12.nwc: 1 model, 18,422 items, units mm. doc #1 now 4 models, 96,110 items
undo: nw_status {op:"undo"}

> nw_items {op:"set_props", items:"?Item.Source File ~ STR_v12", tab:"Coordination", props:{Status:"Checked", CheckedBy:"BY", Date:"2026-10-29"}}
set 3 props on tab "Coordination" for 18,422 items
undo: nw_status {op:"undo"}

> nw_file {op:"save", path:"C:/out/Hospital.nwf"}
saved C:/out/Hospital.nwf (nwf, 4 models)
```

### Error shape

```
> nw_items {op:"props", items:"r9"}
error unknown_handle: r9 does not exist (Navisworks restarted since it was made)
next: run nw_find again, then use the new handle
```

## Shape

### 1. Source of truth: C# op classes, catalog derived at build

Every tool op is one C# class with a typed args record and an `[Op]` attribute that carries the docs. The MCP tool list, input schemas, and descriptions are derived from those classes by reflection. There is no list in TypeScript.

```csharp
[Op("nw_clash", "create", Doc = "Create a clash test; run:true also runs it",
    Example = "{op:\"create\", name:\"Ducts vs Structure\", a:\"set:Ducts\", b:\"set:Structure\", type:\"hard\", tolerance:10}")]
sealed class ClashCreate : WriteOp<ClashCreate.Args>
{
    public record Args(string name, ItemRef a, ItemRef b, string type = "hard", double tolerance = 1, bool run = false);
    public override Plan Plan(Ctx c, Args a) => throw new NotImplementedException();
    public override Out Apply(Ctx c, Plan p) => throw new NotImplementedException();
}
```

`plugin/build.ps1` compiles the plugin with csc, then compiles and runs `plugin/tools/GenCatalog.cs`, which reflection-only-loads the DLL and writes `server/catalog.json`. `Catalog.cs` is compiled into both the DLL and the generator, so the JSON schema builder and the catalog hash are one piece of code. At connect the plugin sends its own hash in `hello`; the server refuses a mismatch with `next: run plugin/build.ps1`. The derived list cannot drift silently.

The server reads `catalog.json` at startup, so `tools/list` works before Navisworks is running. Each MCP tool gets `op` (enum) plus the union of its ops' arg properties, with a description that lists one compact line per op: `create {name, a, b; type=hard, tolerance=1, run=false}` (args after `;` optional). `help {tool, op}` returns the example from the attribute.

### 2. Wire protocol

Transport: named pipe `navisworks-mcp-<pid>`, newline-delimited UTF-8 JSON, one object per line. Images travel base64 inside the line.

```ts
// server/src/pipe.ts (private to the server; never re-exported)
type Req  = { id: number; tool: string; op: string; args: Record<string, unknown>; wait_ms?: number };
type Resp =
  | { id: number; ok: true;  text: string; image?: { png_b64: string } }
  | { id: number; ok: true;  job: string; text: string }                      // exceeded wait_ms
  | { id: number; ok: false; code: ErrCode; message: string; next: string };
type Hello = { pid: number; catalog_hash: string; epoch: string; doc: string | null };
type ErrCode = 'bad_args' | 'unknown_handle' | 'not_found' | 'busy' | 'navisworks' | 'script' | 'disconnected';
```

Request handling in the plugin: the pipe thread parses the line, `BeginInvoke`s the op onto the UI thread, and waits up to `wait_ms` (default 20 s). If the op finishes in time the reply carries its text. If not, the reply carries a job id and the result lands in the job table when the UI thread returns. There is no per-op "long" flag: every op can become a job, which keeps the client responsive under any model size. `nw_status {op:"job"}` is answered by the pipe thread from the job table without touching the UI thread. `cancel` sets `Progress.Cancel` on the job's progress object (best effort; Navisworks honours it for file loads, clash runs are not cancellable and the reply says so).

Multiple instances: the plugin writes `%LOCALAPPDATA%\navisworks-mcp\instances\<pid>.json` (`pid`, `started`, `epoch`, `doc`) at load and deletes it on unload. The server picks `NAVISWORKS_MCP_PID` if set (tests), else the newest instance with a document, else the newest. `nw_status` lists all and `{op:"use", pid}` pins one.

Reconnect: the server keeps one pipe open and reconnects on failure. A request that was in flight when the pipe dropped returns `disconnected` with `next: call nw_status; if the write did not land, repeat it`. The plugin's `epoch` (a GUID per process start) is embedded in every handle (`r3` is `r3` to the agent, `epoch:r3` on the wire), so handles from a dead instance fail with `unknown_handle` instead of resolving to wrong items.

Errors: the plugin maps exceptions to `code, message, next` in one place (`Catalog.Run`). `bad_args` comes from binding the typed record (missing or mistyped field, with the op line from the catalog as `next`). The server renders `error <code>: <message>\nnext: <next>` and never retries on its own.

### 3. Tool surface

Eleven tools. Each takes `op` and flat args. Lengths mm, angles degrees, colors `#rrggbb`, transparency 0..1, dates ISO.

| tool | ops |
|---|---|
| `nw_status` | `status`, `use {pid}`, `help {tool, op}`, `job {id}`, `cancel {id}`, `undo`, `redo` |
| `nw_find` | `find {where; from, show, limit=50, offset=0, prune=true}`, `count {where; from}`, `props_in {where; from, limit}` (which categories/props exist in the result, for query building) |
| `nw_items` | `list {items; show, limit, offset}`, `props {items; tabs, limit}`, `set_props {items, tab, props}`, `remove_tab {items, tab}`, `tree {items; depth=1}`, `bbox {items}`, `select {items; mode=replace}`, `override {items; color, transparency, hidden, scope=temporary}`, `reset {items; scope}`, `stats {; by=model}` |
| `nw_sets` | `list {; folder}`, `get {set; limit, offset}`, `create {name, items; folder}` (explicit) or `create {name, where; folder}` (search set), `update {set; name, items, where}`, `delete {set}`, `folder {name; parent}` |
| `nw_clash` | `tests {}`, `get {test}`, `create {name, a, b; type, tolerance, self, rules, run}`, `update {test; name, a, b, type, tolerance, rules}`, `delete {test}`, `run {test|all}`, `clear {test}`, `results {test; status, assigned, by, limit, offset, sort}`, `result {id}` (both items with key props, center, distance, image), `update {results, status, assign, comment, approve}`, `report {test; format=csv|html|json, images, path}` |
| `nw_view` | `list {; folder}`, `go {view}`, `save {name; items, fit, folder, overrides=true}`, `update {view; name, from_current}`, `delete {view}`, `camera {; eye, target, up, fov, ortho}`, `fit {items}`, `section {planes:[{axis|normal, offset, enabled}] | box {min,max} | off}`, `capture {; width=1280, height, path, style=scene}` |
| `nw_file` | `info {}`, `open {path}`, `append {paths}`, `merge {paths}`, `save {path; version}`, `publish {path; title, author, password, expiry}`, `export {format=dwf|fbx|csv|xml, path; items}`, `close {}` |
| `nw_timeliner` | `tasks {; limit, offset}`, `create {tasks:[{name, start, end, type, items, parent}]}`, `update {task; ...}`, `delete {task}`, `import_csv {path; map}`, `attach {task, items}`, `types {}`, `at {date; capture, path}` (apply the schedule state at a date as temporary overrides, optionally capture) |
| `nw_takeoff` | `catalog {; limit}`, `catalog_add {groups:[...], items:[{name, group, resources}]}`, `items {; group, limit, offset}`, `takeoff {items, catalog_item}`, `quantities {; by=item|group|resource, filter}`, `sql {query}` |
| `nw_batch` | `{ops:[{tool, op, ...args}]; dry, name}` one transaction, one undo step, `$n` references |
| `nw_script` | `{code; refs}` C# body of `object Run(Document doc, Ctx ctx)`; `ctx.Items("r3")`, `ctx.Handle(collection)`, `ctx.Out` |

Item references (`ItemRef`, parsed in `Refs.cs`, the only place that knows the grammar):

- `r3` handle, `r3.4` one row, `r3[0..9]` slice, `r3+r5` union
- `sel` current selection, `set:Name` selection or search set, `path:0/12/3` model index path (`DocumentModels.ResolveIndexPath`)
- `guid:<InstanceGuid>`, `rvt:123456` (search `Element ID` property), `ifc:<GlobalId>` (search `IFC.GlobalId`)
- `?<where>` inline search, same grammar as `nw_find`
- `g1`, `c12`, `t3`, `v7`, `s2`, `k5` are clash group, clash result, test, viewpoint, set, task handles; `c12.1` and `c12.2` are the two clashing items

`where` grammar: `cond (& cond)* (| cond (& cond)*)*`. `cond` is `Category.Property op value`. Ops: `=`, `!=`, `~` (contains), `~*` (wildcard), `>`, `<`, `>=`, `<=`, `has` (property exists, value omitted), `!has`. Values: bare word, `"quoted"`, number with optional unit (`300mm`, `12in`), `true`. Names are display names; `@LcOaNode.LcOaNodeName` uses internal names. `|` maps to `SearchConditionOptions.StartGroup`; everything else is one `SearchCondition`. `from` restricts `Search.Locations`/`Selection` to an ItemRef.

Handles: `r#` is a `ModelItemCollection` kept in the plugin per process, numbered from 1, capped at 200 (oldest evicted; the error says so). Rows show `r3.i`, display name, class, then `show` properties. Paging: `limit`/`offset` with `Count` as exact total.

Units: every length is mm on the wire and in text. `Out.Len` converts from `Document.Units` with `UnitConversion`. Property values typed `DoubleLength`/`DoubleArea`/`DoubleVolume` render as `312mm`, `4.2m2`, `0.81m3`. Others render verbatim.

Writes and undo: each `WriteOp` runs inside `doc.BeginTransaction(opName)`, so one call is one undo step; `nw_batch` opens one transaction around all ops. `nw_status {op:"undo"}` calls `Document.Undo()` and reports `NextUndo` before and after. Every write result ends with `undo: nw_status {op:"undo"}`. Dry run: `dry:true` runs `Plan` only and renders the plan (`would override 142 items ...`). `Plan` resolves every ref and validates every argument, so an `Apply` that fails is a Navisworks fault, not a typo. In a batch, all plans run before any apply.

Property writes: `set_props` goes through COM (`ComApiBridge.State.GetGUIPropertyNode(path, true)` then `InwGUIPropertyNode2.SetUserDefined`), one tab per item, merging existing tab values. `props` reads `ModelItem.PropertyCategories` (fast path: search sets, not iteration, decide which items; iteration only over the page requested).

Clash: tests are `ClashTest` under `doc.GetClash().TestsData`; `create`/`update` edit a copy and call `TestsEditTestFromCopy`; `run` calls `TestsRunTest`. Results come from `test.Children`; `results` groups in the plugin by `level` (`GridIntersection.Level`), `grid`, `item1`, `item2`, `status`, `assigned` and hands out `g#` handles over result lists. Status, assignee, comments, approval use `TestsEditResultStatus`, `TestsEditResultAssignedTo`, `TestsEditResultComments`, `TestsEditResultApprovedBy`. `rules` accepts names of rules on an existing test (`copy_rules_from`); authoring new rules is not exposed (no public constructor). Reports are written by the plugin: CSV, JSON, or HTML with one PNG per result from `TestsViewpointForResult` and `View.GenerateImage`.

Views: `SavedViewpoint` through `doc.SavedViewpoints`; `save` with `items` sets the current selection and calls `ActiveView.FocusOnCurrentSelection` first; `overrides` captures hide/color state. `section` writes `View.SetClippingPlanes(json)`; the ClipPlaneSet JSON is built in `Ops.View.cs` from axis or box inputs. `capture` is `ActiveView.GenerateImage(ImageGenerationStyle.Scene, w, h, ...)`, saved to `path` or returned as an MCP image.

Files: `OpenFile`, `AppendFiles`, `MergeFiles`, `SaveFile(path, version)`, `PublishFile(path, PublishProperties)`, `ExportAsDwf`. `csv`/`xml` exports are plugin-written property dumps of `items`. `fbx` goes through COM `DriveIOPlugin` (plugin id to verify).

TimeLiner: `TaskAddCopy`, `TaskEdit`, `TaskRemoveAt`; `attach` sets `task.Selection.CopyFrom(SelectionSourceCollection)` with `doc.SelectionSets.CreateSelectionSource(set)` so the link survives model updates. `import_csv` is parsed by the plugin (`map` names the columns) and creates tasks, so there is one path for task creation. `at {date}` computes task state from planned/actual dates and task types (`SimulationTaskTypes`) and applies temporary hide/color overrides, then optionally captures. Animation export is not in scope.

Quantification: `doc.GetTakeoff()` tables. `catalog` reads `ItemGroups`, `Items`, `Resources`; `takeoff` calls `Objects.InsertModelItemTakeoff(itemRowId, modelItem)` and `MapModelItemPropertiesToInputVariables`; `quantities` runs SQL over `LcTkUtility.ObjectTableName` through `doc.Database` and sums by `by`. `sql` is the raw escape hatch for the takeoff database.

Script: `nw_script` compiles with `CSharpCodeProvider` (built into .NET Framework, C# 5 syntax) against the Navisworks assemblies, caches by code hash, and runs on the UI thread inside a transaction. `ctx.Handle(ModelItemCollection)` returns a new `r#`, so scripts compose with every other tool.

### 4. Output format

Compact text, rendered in the plugin next to the op. JSON is never shown to the agent except in `report {format:"json"}` files. `Out` is a small builder with fixed conventions, so every op reads the same:

```csharp
// plugin/src/Out.cs
sealed class Out
{
    public static Out Head(string text) => throw new NotImplementedException();       // first line: handle, count, total, doc
    public Out Row(params object[] cells) => throw new NotImplementedException();      // two-space indent, cells joined by two spaces
    public Out More(int shown, int total, int nextOffset) => throw new NotImplementedException(); // "... 50 of 142, next offset 50"
    public Out Undo() => throw new NotImplementedException();                           // "undo: nw_status {op:\"undo\"}"
    public Out Image(byte[] png) => throw new NotImplementedException();                // attaches MCP image content
    public static string Len(double docUnits, Document doc) => throw new NotImplementedException(); // "312mm"
    public static string Prop(DataProperty p, Document doc) => throw new NotImplementedException();
    public string Text { get; }
}
```

Rules: numbers with thousands separators, no trailing zeros, names unquoted unless they contain two spaces, one item per row, never more than `limit` rows, `More` whenever `shown < total`. The server passes `text` through as one `text` content block and `image` as one `image` block. Error text is rendered by the server from the three fields.

### 5. Module map

```
plugin/src/
  Plugin.cs        EventWatcherPlugin; UI marshaller; pipe server thread; instance file; job table
  Catalog.cs       [Op] attribute; op discovery; arg binding; JSON schema + hash; Run (errors -> code/next)
  Refs.cs          ItemRef grammar; handle table; where DSL -> Search
  Out.cs           text rendering, units
  Ops.Status.cs  Ops.Find.cs  Ops.Items.cs  Ops.Sets.cs  Ops.Clash.cs  Ops.View.cs
  Ops.File.cs    Ops.Timeliner.cs  Ops.Takeoff.cs  Ops.Batch.cs  Ops.Script.cs
plugin/tools/GenCatalog.cs   console exe: reflection-only load -> server/catalog.json
plugin/build.ps1             csc plugin; csc + run GenCatalog; copy bundle to %APPDATA%
server/src/main.ts           MCP stdio server from catalog.json; maps tool call -> pipe request -> content
server/src/pipe.ts           instance discovery, connect, reconnect, request/response, hello check
server/catalog.json          generated, committed, checked by hash at connect
tests/*.test.ts              node:test; launches Roamer.exe with NAVISWORKS_MCP_PID pinning; MCP client over stdio
```

Call chain for any tool call: `main.ts` to `pipe.ts` to `Plugin.cs` to `Catalog.cs` to `Ops.X.cs`. Three files per process. `Refs` and `Out` are leaves.

Type sketch, plugin:

```csharp
// Catalog.cs
[AttributeUsage(AttributeTargets.Class)]
sealed class OpAttribute : Attribute
{
    public OpAttribute(string tool, string op) { Tool = tool; Op = op; }
    public string Tool { get; } public string Op { get; }
    public string Doc { get; set; } public string Example { get; set; }
}

/// Everything an op may touch. Doc is never null inside an op (Catalog.Run checks and errors first).
sealed class Ctx
{
    public Document Doc { get; }
    public Refs Refs { get; }
    public bool Dry { get; }
    public Progress Progress { get; }      // for cancel
    public ModelItemCollection Items(ItemRef r) => throw new NotImplementedException();
    public string Handle(ModelItemCollection items) => throw new NotImplementedException();
}

abstract class ReadOp<TArgs> { public abstract Out Run(Ctx c, TArgs a); }

/// Plan resolves refs and validates; Apply performs. Dry = Plan only. Batch = all Plans, then all Applies, one transaction.
abstract class WriteOp<TArgs>
{
    public abstract Plan Plan(Ctx c, TArgs a);
    public abstract Out Apply(Ctx c, Plan p);
}
sealed class Plan { public string Summary; public object State; public int ItemCount; }

static class Catalog
{
    /// Reflects over the assembly once. Order: tool, then op, stable for hashing.
    public static IReadOnlyList<OpInfo> Ops() => throw new NotImplementedException();
    public static string ToJson() => throw new NotImplementedException();   // tools, ops, schemas, docs
    public static string Hash() => throw new NotImplementedException();     // sha256 of ToJson
    /// Bind args to the record, run, map exceptions. Never throws.
    public static Reply Run(Ctx c, string tool, string op, JObject args) => throw new NotImplementedException();
}
sealed class OpInfo { public string Tool, Op, Doc, Example; public Type Args; public bool IsWrite; }
sealed class Reply { public bool Ok; public string Text; public byte[] Png; public string Code, Message, Next; }

// Refs.cs
/// Parsed once at binding time; wrong syntax is bad_args before the op runs.
abstract class ItemRef
{
    public static ItemRef Parse(string s) => throw new NotImplementedException();
    public abstract ModelItemCollection Resolve(Ctx c);
}
sealed class Handle : ItemRef { public int N; public int? Row; public (int, int)? Slice; public override ModelItemCollection Resolve(Ctx c) => throw new NotImplementedException(); }
sealed class SelRef : ItemRef { public override ModelItemCollection Resolve(Ctx c) => throw new NotImplementedException(); }
sealed class SetRef : ItemRef { public string Name; public override ModelItemCollection Resolve(Ctx c) => throw new NotImplementedException(); }
sealed class PathRef : ItemRef { public int[] Path; public override ModelItemCollection Resolve(Ctx c) => throw new NotImplementedException(); }
sealed class SourceIdRef : ItemRef { public string Kind, Id; public override ModelItemCollection Resolve(Ctx c) => throw new NotImplementedException(); }
sealed class WhereRef : ItemRef { public Where Where; public override ModelItemCollection Resolve(Ctx c) => throw new NotImplementedException(); }
sealed class UnionRef : ItemRef { public ItemRef[] Parts; public override ModelItemCollection Resolve(Ctx c) => throw new NotImplementedException(); }

sealed class Where
{
    public static Where Parse(string dsl) => throw new NotImplementedException();
    public Search ToSearch(Document doc, ItemRef from) => throw new NotImplementedException();   // SearchCondition per cond, StartGroup at '|'
}

/// Owner of every handle in the process. Epoch is a GUID per process start, part of the wire form.
sealed class Refs
{
    public string Epoch { get; }
    public string Add(ModelItemCollection items) => throw new NotImplementedException();            // "r7"; evicts beyond 200
    public ModelItemCollection Get(string handle) => throw new NotImplementedException();           // throws UnknownHandle
    public string AddClashGroup(IReadOnlyList<ClashResult> results) => throw new NotImplementedException(); // "g1"
}

// Plugin.cs
[Plugin("NavisworksMcp", "NMCP")]
sealed class Plugin : EventWatcherPlugin
{
    Control ui; Refs refs; Jobs jobs;
    public override void OnLoaded() => throw new NotImplementedException();     // control, instance file, pipe thread
    public override void OnUnloading() => throw new NotImplementedException();  // delete instance file
    /// Pipe thread: read line, BeginInvoke Catalog.Run on ui, wait wait_ms, reply result or job id.
    void Serve(NamedPipeServerStream pipe) => throw new NotImplementedException();
}
/// One table, locked, read by the pipe thread, written by the UI thread.
sealed class Jobs
{
    public string Start(Func<Reply> work, Progress progress) => throw new NotImplementedException();   // "j4"
    public Reply TryGet(string id, int waitMs) => throw new NotImplementedException();                   // null while running
    public bool Cancel(string id) => throw new NotImplementedException();
}
```

Type sketch, server:

```ts
// server/src/pipe.ts
export class Navisworks {
  static discover(): Instance[] { throw new Error('not implemented'); }           // instances dir, newest first
  constructor(private pick: (all: Instance[]) => Instance | undefined) {}
  /** Connects lazily, checks hello.catalog_hash against the bundled catalog, reconnects on EPIPE. */
  call(req: Omit<Req, 'id'>): Promise<Resp> { throw new Error('not implemented'); }
  use(pid: number): void { throw new Error('not implemented'); }
}
export type Instance = { pid: number; started: string; doc: string | null };

// server/src/main.ts
import catalog from '../catalog.json' with { type: 'json' };
/** One MCP tool per catalog tool; description = one line per op; schema = op enum + union of args. */
function toolsFromCatalog(c: Catalog): Tool[] { throw new Error('not implemented'); }
/** Resp -> MCP content: text block, optional image block; errors become "error code: msg\nnext: ...". */
function render(r: Resp): CallToolResult { throw new Error('not implemented'); }
```

Invariants encoded in types: an op cannot read `args` as JSON (it gets a record); a write cannot skip `Plan`; a handle cannot be resolved without the `Refs` of the live process; transport types never leave `pipe.ts`; the catalog cannot be edited by hand in a way the plugin accepts.

Interface depth: eleven tools hide the Search API, COM property tabs, transaction and undo plumbing, UI-thread marshalling, job timeouts, unit conversion, and clash result grouping. The server exposes nothing of its own.

## Synthesis decision

Filled in by arena.

## Tradeoffs accepted

- We accept that the server cannot run without a committed generated `catalog.json` in exchange for one source of truth and a working `tools/list` before Navisworks starts.
- We accept rendering in C# (slower edit loop than TypeScript) in exchange for a server with zero per-tool code and no second place that knows op names.
- We accept `?where` inline refs beside `nw_find` (two ways to pick items) in exchange for one-call writes. The instructions state the rule: find when you will reuse the result, `?where` when you will not.
- We accept that every op may turn into a job (the agent must handle `j#` anywhere) in exchange for no "long op" list and no UI stall longer than `wait_ms`.
- We accept C# 5 in `nw_script` (CodeDom compiler) in exchange for no Roslyn dependency inside Navisworks.
- We accept plugin-written clash reports instead of the native report in exchange for a verified API path and stable formats.
- We accept 200 live handles per process and eviction, so memory is bounded on large models.

## Alternatives considered

- TypeScript as source of truth with generated C# arg records. Loses: the plugin still needs hand-written binding per op, so the generated side becomes a second list, and the hash check has nothing independent to compare.
- Plugin returns domain JSON, server renders text. Loses: a renderer per op in TypeScript is a hand-synced list keyed by op name, and transport JSON shapes leak into a second codebase.
- One `nw` tool with `op` as `tool.op`. Fewer tool descriptions but one giant schema; MCP clients show one tool with 80 ops and the agent loses the grouping that makes the ops discoverable. Eleven tools cost about 1.5k tokens total.
- Structured `where` (arrays of `{category, property, op, value}`). Unambiguous but three times the tokens of the string DSL for every search, and the agent writes the string form correctly from the examples.
- Per-request pipe connect (as in the spike). Simpler state but no place to receive `hello` and no epoch check; a persistent connection with reconnect gives both.

## Open questions and risks

- Does `Progress.Cancel` set from the pipe thread stop `AppendFiles` or `TestsRunTest` on the UI thread, or must cancellation stay "not supported" for those two?
- Are native clash result groups (`ClashResultGroup`) creatable through `TestsAddCopy(GroupItem, SavedItem)`? If yes, `by` could persist groups into the NWF; if not, plugin-side `g#` groups stay session-only.
- Which COM `DriveIOPlugin` ids exist in 2024 for FBX and 3D DWF export? If none, `export` keeps `dwf`, `csv`, `xml` only.
- Is the Revit `Element ID` stored as category `Element ID`, property `Value`, across all Revit exporters, or should `rvt:` search several candidates?
- Should `set_props` default to replacing the tab (clean state) or merging into it (safer for shared tabs)? Design says merge.
- `BeginTransaction` docs say rollback "may" come later. If a batch op fails mid-way, the current plan is `Document.Undo()` once and report which ops landed. Is that acceptable for the first release?

## Next implementation step

Write `Catalog.cs` with `OpAttribute`, `Ctx`, `ReadOp`, `WriteOp`, binding, and `ToJson`/`Hash`, then `GenCatalog.cs` and `build.ps1`, and an end-to-end test that starts Navisworks, asserts `nw_status` works, and asserts `tools/list` equals the catalog.
