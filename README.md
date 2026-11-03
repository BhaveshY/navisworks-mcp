<img src="assets/icon.svg" width="64" height="64" alt="">

# navisworks

An MCP server that drives Autodesk Navisworks Manage 2024 and 2027: model QA, search and properties, clash detection, viewpoints and sectioning, NWD/NWF/CSV/image output, TimeLiner 4D and Quantification.

A plugin inside Navisworks runs every call on the Navisworks UI thread. A small Node process speaks MCP over stdio and talks to the plugin over a named pipe. A typical read or write takes 5 to 100 ms.

## Install

You need Navisworks Manage 2024 or 2027 (or both), Node 24 or later, and Visual Studio 2022 Build Tools, which provide `csc.exe`. You don't need a .NET SDK.

1. Build the plugin and install it as an Autodesk bundle in `%APPDATA%\Autodesk\ApplicationPlugins\NavisworksMcp.bundle`. The install builds the plugin once for each installed Navisworks, against that version's API, and puts every build in the one bundle:

	```
	npm install
	npm run install-plugin
	```

2. Restart Navisworks. The plugin starts with it. It logs startup failures to `%LOCALAPPDATA%\navisworks-mcp\plugin.log`.

3. Register the server with your MCP client. For Claude Code:

	```
	claude mcp add navisworks -- node C:\path\to\navisworks\server\main.ts
	```

The server lists its full tool catalog once Navisworks has started with the plugin at least once. Before that, it offers only `status`.

## Use

The agent sees seven tools:

| Tool | Does |
| --- | --- |
| `status` | Shows the instance, document, models, units, selection and running jobs. `status {pid}` switches between running Navisworks instances. |
| `read` | Searches items, reads properties, the tree, distinct values and stats. Also reads sets, viewpoints, sectioning, clash tests and results, TimeLiner tasks and quantities. It never changes anything. |
| `apply` | Runs a list of changes as one undo step. `preview:true` plans the changes without making them. |
| `export` | Writes images, NWD, NWF, CSV, clash reports, task CSV and quantity CSV. |
| `job` | Waits for, or cancels, a call that ran longer than 25 s. |
| `script` | Runs C# 5 against the Navisworks API for anything the other tools lack. |
| `help` | Prints an op's signature and a working example. |

Lists return a handle such as `r3` with an exact total. Pass the handle, a slice of it (`r3#0-9`), `selection`, `set:Folder/Name`, `id:0:0/2/1` or an inline search (`?Item.Type = IfcWall`) wherever an op takes items, instead of copying rows. Searches use `Tab.Prop op value` joined by `and` and `or`, and run as native Navisworks searches.

Navisworks 2027 opens IFC with Conversion Method v4 by default, which gives elements no GUID, so Quantification can't take them off. For takeoff from IFC on 2027, set Options > File Readers > IFC > Conversion Method to v3 and reopen the model. v3 converts in a separate SceneConvertServer process that needs a .NET 10 runtime matching the installed ASP.NET Core runtime. Without one, that process crashes on load.

Lengths are in mm, areas in m2, volumes in m3, and dates in ISO format. Every error ends with `next:`, the exact call or action that fixes it.

For example, these calls flag every F90 wall and render the result:

```
read   {what:"items", where:"Pset_WallCommon.FireRating = F90"}       -> r1 items 142 (exact)
apply  {ops:[{op:"color", items:"r1", rgb:"#ff0000"}, {op:"viewpoint.save", name:"QA/F90 walls", items:"r1"}]}
export {out:[{what:"image", view:"QA/F90 walls", inline:true}]}
```

## Develop

```
node scripts/plugin.ts build [year...]   # compile into build/<year>/ only
npx tsc                                  # typecheck the server and tests
npm test                                 # end-to-end tests against every installed Navisworks, 2024 then 2027
npm test -- 2027                         # end-to-end tests against one version
npm test -- 2027 test/clash.test.ts      # one version, one file
```

The tests are end to end only. `npm test` builds each version, runs the whole suite against it, and prints a pass or fail line per version. Each test file launches its own Navisworks and loads the plugin from `build/<year>/`, through the `NAVISWORKS_MCP_CORE` variable read by the installed shim. Each test drives the real server over stdio with the MCP SDK client, then closes Navisworks normally. A generated IFC model (`test/fixture/`) gives known answers: the tests derive every expected count, clash and quantity from its definition. Your own Navisworks session is never touched. A test run leaves no autosave or crash-recovery state behind.

The code is laid out like this:

- `scripts/plugin.ts` holds the table of supported versions. Each row's year and API version give its install folder, bundle series, build folder and compat file.
- `plugin/shim/Shim.cs` is the installed plugin. It loads the core and the core's dependencies from bytes, so rebuilding never fights a running Navisworks that has the file open.
- `plugin/compat/<year>.cs` holds the few calls whose API differs between versions. The build compiles the core with the file for its version.
- `plugin/lib/Newtonsoft.Json.dll` is Newtonsoft.Json 13.0.3 (MIT license, beside it). Navisworks 2027 does not ship it, so every build carries it.
- `plugin/core/Host.cs` owns the pipe server, the jobs and the marshalling onto the UI thread.
- `plugin/core/Catalog.cs` holds the op table. Each op's signature string generates its JSON schema, its argument validation and its `help` text.
- `plugin/core/Targets.cs` holds handles, the target grammar and the `where` language.
- `plugin/core/Text.cs` holds the output format and the unit handling.
- `plugin/core/Ops/*.cs` holds one file per domain.
- `server/main.ts` and `server/link.ts` are the MCP server and the pipe client.
- `docs/design/` holds the design and the decisions behind it. `scripts/dump-api.ps1` writes reflection dumps of the installed Navisworks API to `docs/api/`, which git ignores. The Clash, TimeLiner and Quantification assemblies have no XML docs, so the dumps are the reference for them.
