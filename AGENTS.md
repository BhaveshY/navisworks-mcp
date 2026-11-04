# Navisworks MCP

This repository connects an MCP client to Autodesk Navisworks Manage 2024 and 2027. A C# plugin runs operations inside Navisworks. A Node server exposes the tools over stdio and connects to the plugin over a named pipe. The server, setup, and doctor have no runtime npm dependencies.

## Install on Windows

Prerequisites: Windows, Node 24 or later, Git, and Navisworks Manage 2024, 2027, or both. Detection uses the supported version table in `scripts/plugin.ts` and the standard `C:\Program Files\Autodesk\Navisworks Manage <year>` folders. Windows supplies `tar.exe`. Release installs do not need Visual Studio or `npm install`. Source builds need Visual Studio 2022 Build Tools with MSBuild and the C# compiler. Set `CSC` to the full path to `csc.exe` if automatic detection fails.

Ask only which MCP client to register if the client is unknown. Choose `codex`, `claude`, or `none`. Run these commands in PowerShell:

```powershell
git clone https://github.com/BhaveshY/navisworks-mcp.git
cd navisworks-mcp
node scripts/setup.ts --client codex
```

Replace `codex` with the chosen client. `npm run setup -- --client codex` runs the same script. If PowerShell blocks `npm.ps1` or `npx.ps1`, use `npm.cmd` or `npx.cmd`.

Setup detects installed years and downloads binaries for this checkout's exact `package.json` version:

```text
https://github.com/BhaveshY/navisworks-mcp/releases/download/v<version>/navisworks-mcp-plugin-<version>.zip
https://github.com/BhaveshY/navisworks-mcp/releases/download/v<version>/navisworks-mcp-plugin-<version>.zip.sha256
```

Setup verifies SHA-256 before extraction. It installs one bundle at `%APPDATA%\Autodesk\ApplicationPlugins\NavisworksMcp.bundle`, with a component for each installed year. Rerunning setup replaces the same components and removes stale files. A locked old shim stays until a later run can remove it. The shim's filename includes its content hash.

If a download fails and the C# compiler is available, setup reports the failure and builds from source. To choose source builds explicitly, run `node scripts/setup.ts --build --client codex`. For offline installation, put the zip and its `.zip.sha256` sidecar in the same folder, then run `node scripts/setup.ts --zip C:\downloads\navisworks-mcp-plugin-0.1.0.zip --client codex`. The checksum file must contain `<sha256>  <zip filename>`. The zip contains only `<year>/NavisworksMcp.dll`, `<year>/NavisworksMcp.Core.dll`, and `<year>/Newtonsoft.Json.dll`, with optional year directory entries. Do not combine `--zip` and `--build`.

Codex registration replaces only `[mcp_servers.navisworks]` and its subtables in `$CODEX_HOME/config.toml`. The default is `%USERPROFILE%\.codex\config.toml`. Other bytes stay unchanged. Claude registration runs `claude mcp remove navisworks --scope user`, ignoring a missing registration, then `claude mcp add navisworks --scope user -- <node> <server>`. Claude must be on PATH. `--client none` skips registration. Setup prints both client recipes with absolute paths and the current Node executable.

Ask the user to start Navisworks, or restart it if it is running without the plugin loaded. Ask the user to restart the MCP client. Do not close their Navisworks process or modify an open document. Setup and doctor do not launch or stop Navisworks.

## Verify the install

Run `npm run doctor`, or `node scripts/doctor.ts`. Add `--client codex` or `--client claude` to check registration. With npm, use `npm run doctor -- --client codex`.

Doctor checks the Node runtime, detected years, bundle components and DLLs, and the real server's MCP `initialize` and `tools/list` responses within 10 seconds. It continues after failures and exits 1 if any check fails. It leaves configuration, bundle files, logs, and stale instance files unchanged.

Before Navisworks has loaded the plugin for the first time, `tools/list` contains only `status`. This is expected. Doctor reports that no plugin instance is running. After the user starts Navisworks, rerun doctor and call the MCP `status` tool. Confirm the Navisworks version line. Doctor also calls `status` for each live registered instance and checks `%LOCALAPPDATA%\navisworks-mcp\plugin.log` for entries newer than `PackageContents.xml`.

## Resolve failures

Setup stops at its first failed step. Its final doctor pass uses doctor's continue-on-failure behavior. Every failure has the form `FAIL <label>: <detail>` followed by `next: <fix>`. Follow that exact fix. These are all failure labels; `<year>` means a detected year, and details include the failing path or system error.

| FAIL line prefix | Meaning | Fix |
| --- | --- | --- |
| `FAIL arguments:` | Setup flags are invalid. | Run `node scripts/setup.ts [--build \| --zip <path>] [--client codex\|claude\|none]`. |
| `FAIL runtime:` | Windows or Node 24 is missing. | Install Node 24 or later on Windows, then rerun the command. |
| `FAIL Navisworks:` | No supported Manage installation was detected. | Install Manage 2024 or 2027 in its standard folder, then rerun the command. |
| `FAIL binary download:` | The release download failed and no compiler was found. | Obtain the matching zip and checksum and use `--zip <path>`, or install VS 2022 Build Tools and use `--build`. |
| `FAIL build:` | Source compilation failed. | Install the MSBuild and C# compiler components, or set `CSC`; fix any compiler error and rerun `--build`. |
| `FAIL archive checksum:` | The zip or checksum is missing, malformed, renamed, or does not match. | Obtain the zip and matching `.zip.sha256` from the same release; keep the original filename and rerun `--zip <path>`. |
| `FAIL archive layout:` | The archive has unexpected paths, duplicate paths, links, or cannot be listed. | Use the release layout above and ensure Windows `tar.exe` is available. |
| `FAIL archive year <year>:` | A detected year's DLL is missing from the archive. | Obtain an archive containing that year or run `node scripts/setup.ts --build`. |
| `FAIL archive extraction:` | Windows tar could not extract the archive. | Check `tar.exe`, the archive, and write access to TEMP, then rerun `--zip <path>`. |
| `FAIL bundle:` | Setup could not install, or doctor found a missing component or DLL. | Check write access to `%APPDATA%\Autodesk\ApplicationPlugins`; run `node scripts/setup.ts` to repair. |
| `FAIL codex registration:` | Codex config could not be written or does not point to this checkout and Node executable. | Check access to `$CODEX_HOME/config.toml`, then run `node scripts/setup.ts --client codex`. |
| `FAIL claude registration:` | Claude is missing, its command failed, or registration points elsewhere. | Install Claude Code on PATH and run `node scripts/setup.ts --client claude`. |
| `FAIL server handshake:` | The server failed to start or answer MCP within 10 seconds. | Run `node server/main.ts` with Node 24 or later, fix the startup error, then rerun doctor. The server normally waits silently for stdio input. |
| `FAIL plugin status:` | A live registration could not return status. | Start or restart Navisworks, wait for startup to finish, then rerun doctor. |
| `FAIL plugin log:` | A plugin log entry is newer than the bundle install. | Fix the reported error, rerun setup, restart Navisworks, and rerun doctor. |
| `FAIL doctor:` | Doctor arguments are invalid or an unexpected doctor error occurred. | Run `node scripts/doctor.ts [--client codex\|claude]` and follow its diagnostics. |
| `FAIL temporary files:` | Setup could not create or clean up its temporary files. | Check access to TEMP, then rerun setup. |

## Update or uninstall

To update, run `git pull` in the checkout and rerun `node scripts/setup.ts --client <client>`. Restart Navisworks to load the updated core and restart the MCP client. Keep the checkout in place because the registration points to `server/main.ts` there.

To uninstall, ask the user to close Navisworks and delete only `%APPDATA%\Autodesk\ApplicationPlugins\NavisworksMcp.bundle`. For Codex, remove `[mcp_servers.navisworks]` and its subtables from the config file, preserving other settings. For Claude, run `claude mcp remove navisworks --scope user`. Restart the MCP client. The checkout can then be deleted.

## Maintain the repository

Use the version table in `scripts/plugin.ts`. Do not duplicate it. Build and install share one bundle writer. Keep runtime code free of npm dependencies. `@modelcontextprotocol/sdk` is a development dependency for tests.

```powershell
npm ci
node scripts/plugin.ts build
npm run install-plugin
npx tsc
node --test test/setup.test.ts
node --test test/server.test.ts
npm test
npm test -- 2027 test/clash.test.ts
node scripts/plugin.ts release
```

Tests are end to end only. Assert real files, process exit codes, protocol responses, and application state. Do not add mocked tests or tests of private implementation details. Installation tests build real DLLs into a temporary folder and run the real setup and doctor scripts with isolated APPDATA, LOCALAPPDATA, and CODEX_HOME. They need a supported Navisworks installation and the compiler, but no running Navisworks. They leave the real bundle, MCP configuration, and user's processes alone.

`npm run install-plugin` builds and writes the real user bundle unless APPDATA is redirected. `npm test` launches each installed Navisworks version and runs the full application suite. Run that suite only when application testing is requested. Each application test owns its process and closes only that process.

`release` requires every supported Manage year installed and names any missing year. It builds each year's DLLs with `-deterministic`, writes `dist/navisworks-mcp-plugin-<version>.zip` and its `.zip.sha256`, and prints a `gh release create` command. Inspect the zip with `tar.exe -tf dist/navisworks-mcp-plugin-<version>.zip`. It must contain the three DLLs per supported year, without a wrapping folder. Release creation, tagging, and pushing are separate maintainer actions. Do not run the printed command without authorization.
