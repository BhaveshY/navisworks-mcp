import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve, sep } from 'node:path';
import { binaries, build, findCsc, install, installed, tar, version, versions } from './plugin.ts';
import { checkRuntime, claudeSnippet, codexSnippet, failure, oneLine, options, registerClaude, registerCodex, years } from './installation.ts';
import { doctor } from './doctor.ts';

async function setup() {
	let temp: string | undefined;
	let label = 'arguments';
	let next = 'Run node scripts/setup.ts [--build | --zip <path>] [--client codex|claude|none].';
	const step = async <T>(name: string, fix: string, fn: () => T | Promise<T>): Promise<T> => {
		label = name;
		next = fix;
		const result = await fn();
		console.log(`ok ${name}`);
		return result;
	};
	try {
		const args = options(process.argv.slice(2), true);
		await step('runtime', 'Install Node 24 or later on Windows, then rerun node scripts/setup.ts.', checkRuntime);
		await step('Navisworks', `Install Navisworks Manage ${years}, then rerun node scripts/setup.ts.`, () => {
			if (!installed.length) throw new Error('no supported Navisworks Manage installed');
		});
		console.log(`info detected Navisworks Manage ${installed.map((nw) => nw.year).join(', ')}`);
		const scratch = () => temp ??= mkdtempSync(join(tmpdir(), 'navisworks-setup-'));
		let zip = args.zip ? resolve(args.zip) : undefined;
		let fromSource = args.build;
		if (!fromSource && !zip) {
			await step('binary download', 'Download the matching release zip and .sha256, then run node scripts/setup.ts --zip <path>; or install VS 2022 Build Tools and run node scripts/setup.ts --build.', async () => {
				const name = `navisworks-mcp-plugin-${version}.zip`;
				const url = `https://github.com/BhaveshY/navisworks-mcp/releases/download/v${version}/${name}`;
				try {
					const [archive, checksum] = await Promise.all([url, `${url}.sha256`].map(async (url) => {
						const response = await fetch(url, { signal: AbortSignal.timeout(30000) });
						if (!response.ok) throw new Error(`HTTP ${response.status} from ${url}`);
						return Buffer.from(await response.arrayBuffer());
					}));
					zip = join(scratch(), name);
					writeFileSync(zip, archive!);
					writeFileSync(`${zip}.sha256`, checksum!);
				} catch (e) {
					try { findCsc(); }
					catch { throw new Error(`${oneLine(e)}; csc.exe is unavailable`); }
					console.log(`info release download failed (${oneLine(e)}); falling back to building from source`);
					fromSource = true;
				}
			});
		}
		let directories: Map<number, string>;
		if (fromSource) {
			directories = await step('build', 'Install VS 2022 Build Tools with MSBuild and the C# compiler, or set CSC to csc.exe; rerun node scripts/setup.ts --build.', () => {
				for (const nw of installed) build(nw);
				return new Map(installed.map((nw) => [nw.year, nw.out]));
			});
		} else {
			await step('archive checksum', `Obtain ${basename(zip!)} and its matching ${basename(zip!)}.sha256 from the same release, then rerun node scripts/setup.ts --zip <path>.`, () => {
				const checksum = readFileSync(`${zip}.sha256`, 'utf8').trim();
				const match = /^([a-fA-F0-9]{64})[ \t]+\*?([^\r\n]+)$/.exec(checksum);
				if (!match || match[2] !== basename(zip!)) throw new Error('invalid .sha256 sidecar or filename');
				const actual = createHash('sha256').update(readFileSync(zip!)).digest('hex');
				if (actual !== match[1]!.toLowerCase()) throw new Error(`SHA-256 mismatch for ${zip}`);
			});
			const entries = await step('archive layout', 'Use the release zip with only <year>/NavisworksMcp.dll, <year>/NavisworksMcp.Core.dll and <year>/Newtonsoft.Json.dll; ensure Windows tar.exe is available.', () => {
				const entries = execFileSync(tar, ['-tf', zip!], { encoding: 'utf8', windowsHide: true }).trim().split(/\r?\n/);
				const allowed = new Set(versions.flatMap((v) => [`${v.year}/`, ...binaries.map((dll) => `${v.year}/${dll}`)]));
				if (new Set(entries).size !== entries.length || entries.some((entry) => !allowed.has(entry))) throw new Error('unexpected or duplicate archive paths');
				const listing = execFileSync(tar, ['-tvf', zip!], { encoding: 'utf8', windowsHide: true }).trim().split(/\r?\n/);
				if (listing.some((line) => !/^[-d]/.test(line))) throw new Error('archive links are not allowed');
				return entries;
			});
			for (const nw of installed) {
				await step(`archive year ${nw.year}`, `Use a release zip containing Navisworks Manage ${nw.year}, or run node scripts/setup.ts --build.`, () => {
					for (const dll of binaries) if (!entries.includes(`${nw.year}/${dll}`)) throw new Error(`missing ${nw.year}/${dll}`);
				});
			}
			directories = await step('archive extraction', 'Ensure Windows tar.exe is available and TEMP is writable, then rerun node scripts/setup.ts --zip <path>.', () => {
				const out = join(scratch(), 'binaries');
				mkdirSync(out);
				execFileSync(tar, ['-xf', zip!, '-C', out, ...installed.flatMap((nw) => binaries.map((dll) => `${nw.year}/${dll}`))], { windowsHide: true });
				return new Map(installed.map((nw) => [nw.year, join(out, String(nw.year))]));
			});
		}
		await step('bundle', 'Check write access to %APPDATA%\\Autodesk\\ApplicationPlugins, then rerun node scripts/setup.ts.', () => install(directories));
		// Both recipes are useful when the installing agent and the target client differ.
		console.log(`info Codex config.toml snippet:\n${codexSnippet()}info Claude Code command (PowerShell):\n${claudeSnippet()}`);
		if (args.client !== 'none') {
			await step(`${args.client} registration`, args.client === 'codex'
				? 'Check write access to $CODEX_HOME/config.toml (default %USERPROFILE%\\.codex\\config.toml), then rerun node scripts/setup.ts --client codex.'
				: 'Install Claude Code, put claude on PATH, then rerun node scripts/setup.ts --client claude.', args.client === 'codex' ? registerCodex : registerClaude);
		}
		label = 'doctor';
		next = 'Run node scripts/doctor.ts and follow each next: line.';
		const result = await doctor(args.client);
		if (result) return result;
		console.log('ok setup complete; start Navisworks, or restart it if running without the plugin loaded; restart the MCP client.');
		return 0;
	} catch (e) { failure(label, e, next); return 1; }
	finally {
		if (temp && existsSync(temp)) {
			// Only the unique directory created by this invocation is disposable.
			if (!resolve(temp).startsWith(resolve(tmpdir()) + sep + 'navisworks-setup-')) throw new Error('unexpected temporary directory');
			rmSync(temp, { recursive: true, force: true, maxRetries: 3 });
		}
	}
}

if (import.meta.main) {
	try { process.exitCode = await setup(); }
	catch (e) { failure('temporary files', e, 'Check access to TEMP, then rerun node scripts/setup.ts.'); process.exitCode = 1; }
}
