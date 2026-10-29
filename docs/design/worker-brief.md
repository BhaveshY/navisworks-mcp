# Brief for domain workers

You are adding one domain to a Navisworks Manage 2024 MCP server. The core works and is covered by `test/model.test.ts` (14 passing e2e tests). Read these before writing code:

- `docs/design/design.md`: the agreed design. Follow it.
- `plugin/core/Catalog.cs`: the op table, the signature DSL, `Args`, `Step`, `NwError`, how `read`, `apply` and `export` run.
- `plugin/core/Targets.cs`: handles (`Handle`, `Kind`), the target grammar, the `where` language.
- `plugin/core/Text.cs`: `Out` (lines, pipe tables), `Units` (mm, m2, m3, colours, points).
- `plugin/core/Ops/Model.cs`, `Ops/Sets.cs`, `Ops/Read.cs`: worked examples. Copy their shape.
- `test/model.test.ts`, `test/navisworks.ts`, `test/fixture/scene.ts`: how e2e tests run and how expected values are derived from the fixture definition.
- `docs/api/*.txt`: reflection dumps of the public API, including Clash, TimeLiner and Takeoff, which have no XML docs. `C:\Program Files\Autodesk\Navisworks Manage 2024\Autodesk.Navisworks.Api.xml` and `Autodesk.Navisworks.ComApi.xml` hold the XML docs. Grep before you use a member. Never guess an API name.

## Rules of the codebase

- An op is one entry in your domain's `public static readonly Op[] All`, built with `Catalog.Read(tool, name, signature, handler, doc, example)` for `read` and `export`, or `Catalog.Apply(name, signature, handler, doc, example)` for `apply`. Add your array to the `Ops` list in `Catalog.cs`. That one line is the only change to `Catalog.cs` you may make without asking.
- A parameter name must have the same type in every op of a tool. The catalog throws at load if it doesn't, and Navisworks then writes the error to `%LOCALAPPDATA%\navisworks-mcp\plugin.log`. Reuse existing names (`items`, `name`, `to`, `path`, `limit`, `page`, `where`, `cols`) with their existing types.
- An apply handler validates and resolves everything first and returns `new Step(plan, apply)`. The plan has no side effects. `preview:true` shows only plans. Name `apply` ops `domain.verb` (for example `clash.add`), and keep the verbs consistent with the existing ops.
- A list result becomes a handle through `c.Keep(new Handle { Kind = ..., Rows = ... })`, prints `rN <kind> <total> (exact)`, then a pipe table, then a `more:` line when the list is cut.
- Errors are `throw new NwError(code, message, next)`. `next` is the exact call or action that fixes it.
- The agent's output is compact text: lengths in mm, areas in m2, volumes in m3, dates ISO. No JSON.
- An op that must run alone and outside a transaction (open, save, run a long process) uses `.Alone()`.
- Code reads like the surrounding code. A comment only for a non-obvious why. No narration, no filler, no em dashes.

## Rules for tests

- Only end-to-end tests. They drive the real MCP server over stdio against a real Navisworks that `startSession()` launches with your build.
- Assert behavior an agent can observe through the tools: counts, values read back, files written, errors and their `next`. Derive expected values from `test/fixture/scene.ts` or from first principles about the geometry, never from a run of your code.
- Don't edit `scene.ts`, `ifc.ts` or `navisworks.ts`. If you need more fixture content, stop and say exactly what you need.
- Put your tests in `test/<your-domain>.test.ts`.

## How to build and test

- `node scripts/plugin.ts build` compiles into `build/` in your working copy. Never run `install` (that writes the shared bundle).
- `node --test test/<your-domain>.test.ts` launches its own Navisworks with your build and closes it afterwards. Also run `node --test test/model.test.ts` before you finish, and `npx tsc`.
- Never touch Navisworks processes you didn't start. The user has their own session open.
- If a test leaves Navisworks hanging (a modal dialog, for example), kill only that pid.

## Report back

1. Every op you added, with its signature line.
2. The exact changes you made to shared files (`Catalog.cs`, `Targets.cs`, `Text.cs`, anything outside your own files), as a diff.
3. Test results (paste the summary lines).
4. API behaviour you verified that the design doc listed as an open risk, and anything you found that contradicts the design.
5. What you left out, and why.
