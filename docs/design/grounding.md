# Grounding for the Navisworks MCP design

## Goal

An MCP server that lets an agent drive Autodesk Navisworks Manage 2024 for quick questions and for full production coordination work. It must be fast, reliable, cheap in tokens, and lose no capability. Priorities, all four chosen by the user: model QA and properties, clash detection, coordination outputs (viewpoints, sectioning, images, NWD/NWF, append/merge, exports), TimeLiner and Quantification. Only end-to-end tests are allowed. Only Navisworks Manage 2024 is supported.

## Facts verified on this machine

- Navisworks Manage 2024 (Roamer.exe 21.3.1423.42) at `C:\Program Files\Autodesk\Navisworks Manage 2024`. API assemblies there: `Autodesk.Navisworks.Api.dll` (.NET API, with XML docs), `Autodesk.Navisworks.Clash.dll`, `Autodesk.Navisworks.Timeliner.dll`, `Autodesk.Navisworks.Takeoff.dll` (Quantification), `Autodesk.Navisworks.ComApi.dll` plus interop (COM API, needed for user-defined property tabs and some exports), `Autodesk.Navisworks.Automation.dll`, `Newtonsoft.Json.dll`.
- Plugins load from `%APPDATA%\Autodesk\ApplicationPlugins\<Name>.bundle\PackageContents.xml` with `Platform="NAVMAN" SeriesMin="Nw21" SeriesMax="Nw21"` and `AppType="ManagedPlugin"`. The `%APPDATA%\Autodesk Navisworks Manage 2024\Plugins` folder is not scanned.
- An `EventWatcherPlugin` loads at startup. Its `OnLoaded` runs on the UI thread before the GUI exists. A hidden WinForms `Control` created there marshals work onto the UI thread with `Control.Invoke`. The Navisworks API is single-threaded and must be called on that thread.
- Spike result: Node connects to a named pipe served by the plugin, the plugin runs the request on the UI thread and replies. Round trip 7 ms including connect.
- No .NET SDK or .NET Framework targeting pack is installed. Roslyn `csc.exe` from VS 2022 Build Tools compiles against `C:\Windows\Microsoft.NET\Framework64\v4.0.30319` with `-nostdlib`, so the plugin can use current C# syntax on .NET Framework 4.8.
- Node 24 is installed. It runs `.ts` files directly by stripping types, so a TypeScript server needs no build step if it uses only erasable syntax.
- Several Navisworks instances can run at once. The user keeps one open with their own work, so tests must launch and own their own instance.
- Shipped sample models: `Samples\Getting Started\{Architecture,MEP,Structure}.nwc`, the Autodesk hospital (`Autodesk_Hospital_*.nwc`, `.nwf`), `Eircom_Park.nwf`, `snowmobile.nwd`, a Quantification sample, and others.

## Reference designs the user already runs

These are the user's other CAD MCP servers. Their instructions show the style that works for this user.

- revit-mcp-next: "Start with status. Lengths mm, angles degrees. Writes apply at once and return ids plus an undo hint; preview:true is a dry run. Every error ends with fix: do exactly that. find_elements returns a handle r#: pass from:"r3", "selection" or "last" instead of copying ids. Totals are exact; continue lists with the page token. change_set runs several ops as one undo step. Long work returns a job j#: call job_status. Never repeat a write that is running or unknown."
- InDesign MCP: "change with ONE apply call holding all ops (pre-validated; created items are removed if an op fails). Address items by numeric id or by name. Every error has error.next: follow it, don't loop. indesign_script runs raw script for anything else."
- Vectorworks MCP: tools `vw_status, vw_read, vw_capture, vw_create, vw_edit, vw_apply, vw_view, vw_organize, vw_document, vw_script`. "Every error carries error.next_step: follow it, never loop."

## Navisworks domain notes

- The document is a tree of `ModelItem`s under `Models[i].RootItem`. Items have no stable element id of their own. Source ids (Revit `Element ID`, IFC `GlobalId`) appear as properties when the source format provides them. `InstanceGuid` exists on some items.
- Properties are `PropertyCategory` (display name and internal name) holding `DataProperty` values. Search is native and fast through `Search` with `SearchCondition`s. Iterating properties of many items in managed code is the slow path.
- Selection sets and search sets live in `doc.SelectionSets`. Viewpoints in `doc.SavedViewpoints`. Clash tests in `doc.GetClash().TestsData`. TimeLiner in `doc.GetTimeliner()`. Quantification in `doc.GetTakeoff()` (a SQL-like database).
- Appearance overrides (color, transparency, hide) go through `doc.Models.OverrideTemporaryColor` and related calls. Permanent overrides persist in NWF/NWD.
- Long operations (running clash tests, appending large files) block the UI thread for seconds to minutes.
