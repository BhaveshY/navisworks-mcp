import { execFileSync, spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { navisworks, type Navisworks } from '../scripts/plugin.ts';
import { writeIfc } from './fixture/ifc.ts';

const nw = navisworks();
const instancesDir = join(process.env.LOCALAPPDATA!, 'navisworks-mcp', 'instances');
const autoSaveDir = join(process.env.APPDATA!, 'Autodesk', `Navisworks Manage ${nw.year}`, 'AutoSave');

const autoRecoverKey = `HKCU\\Software\\Autodesk\\Navisworks Manage\\${nw.api}.0\\AutoRecover`;

/// Options a test Navisworks starts with, passed as -options: they hold for that process only and never reach the user's saved options.
/// 2027 autosaves during a test and sometimes answers with a modal "Reload from Auto-Save?" that blocks the clean exit.
const sessionOptions: Partial<Record<Navisworks['year'], string>> = {
	2027: '<optionset name="general"><optionset name="autosave"><option name="enable"><data type="bool">false</data></option></optionset></optionset>',
};

/// Navisworks offers to reload whatever these values name on its next start, the user's own start included.
function clearAutoRecover(sessionDir: string) {
	let values: string;
	try {
		values = execFileSync('reg', ['query', autoRecoverKey], { encoding: 'utf8' });
	} catch {
		return;
	}
	for (const name of ['LastSaved', 'FileWas']) {
		// Navisworks 2027 stores the path as the caller spelled it, forward slashes included.
		const value = new RegExp(`^\\s+${name}\\s+REG_SZ\\s+(.*)$`, 'm').exec(values)?.[1]?.trim().replaceAll('/', '\\');
		const ours = value?.startsWith(sessionDir) || (value?.startsWith(autoSaveDir) && !existsSync(value));
		if (ours) execFileSync('reg', ['add', autoRecoverKey, '/v', name, '/t', 'REG_SZ', '/d', '', '/f']);
	}
}

export type Session = {
	dir: string;
	fixture: string;
	path: (...parts: string[]) => string;
	pid: number;
	call: (name: string, args?: Record<string, unknown>) => Promise<string>;
	callRaw: (name: string, args?: Record<string, unknown>) => Promise<{ text: string; isError: boolean; images: number }>;
	close: () => Promise<void>;
};

export async function startSession(): Promise<Session> {
	const dir = mkdtempSync(join(tmpdir(), 'nmcp-'));
	// Navisworks names autosaves after the document, so a per-session prefix keeps cleanup to our own files.
	const tag = randomBytes(4).toString('hex');
	const path = (...parts: string[]) => join(dir, ...parts.slice(0, -1), `${tag}-${parts.at(-1)}`);
	const fixture = path('fixture.ifc');
	writeIfc(fixture);
	const options = sessionOptions[nw.year];
	const optionsFile = path('options.xml');
	if (options) writeFileSync(optionsFile, `<?xml version="1.0" encoding="UTF-8" ?>
<exchange xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://download.autodesk.com/us/navisworks/schemas/nw-exchange-12.0.xsd"><optionset name="">${options}</optionset></exchange>
`);
	const args = [...(options ? ['-options', optionsFile] : []), fixture];
	const roamer = spawn(join(nw.dir, 'Roamer.exe'), args, { stdio: 'ignore', env: { ...process.env, NAVISWORKS_MCP_CORE: nw.coreDll } });
	const pid = roamer.pid!;
	const registration = join(instancesDir, `${pid}.json`);
	for (let i = 0; i < 480 && !existsSync(registration); i++) await sleep(250);
	if (!existsSync(registration)) {
		roamer.kill();
		throw new Error(`Navisworks pid ${pid} never registered at ${registration}. Is the plugin installed (npm run install-plugin)?`);
	}
	JSON.parse(readFileSync(registration, 'utf8'));

	const transport = new StdioClientTransport({
		command: process.execPath,
		args: [join(import.meta.dirname, '..', 'server', 'main.ts')],
		env: { ...process.env, NAVISWORKS_MCP_PID: String(pid) } as Record<string, string>,
		stderr: 'inherit',
	});
	const client = new Client({ name: 'e2e', version: '0' });
	await client.connect(transport);

	for (let i = 0; ; i++) {
		const status = await client.callTool({ name: 'status', arguments: {} });
		const text = (status.content as { text: string }[])[0]!.text;
		if (text.includes(basename(fixture))) {
			if (!text.includes(`Manage ${nw.year}`)) throw new Error(`expected Navisworks Manage ${nw.year}, status says: ${text}`);
			break;
		}
		if (i > 240) throw new Error(`fixture never finished loading: ${text}`);
		await sleep(250);
	}

	const callRaw = async (name: string, args: Record<string, unknown> = {}) => {
		const result = await client.callTool({ name, arguments: args });
		const content = result.content as { type: string; text?: string }[];
		return {
			text: content.filter((c) => c.type === 'text').map((c) => c.text).join('\n'),
			isError: result.isError === true,
			images: content.filter((c) => c.type === 'image').length,
		};
	};
	return {
		dir,
		fixture,
		path,
		pid,
		callRaw,
		call: async (name, args) => {
			const r = await callRaw(name, args);
			if (r.isError) throw new Error(`${name} failed: ${r.text}`);
			return r.text;
		},
		close: async () => {
			// A killed Navisworks greets the next launch, the user's included, with a modal "reload last file?" dialog, so exit cleanly.
			const exited = new Promise<boolean>((resolve) => {
				if (roamer.exitCode !== null) return resolve(true);
				roamer.once('exit', () => resolve(true));
				setTimeout(() => resolve(false), 30000);
			});
			await callRaw('script', { code: 'nw.Doc.Clear(); System.Diagnostics.Process.GetCurrentProcess().CloseMainWindow(); return "closing";' }).catch(() => {});
			await client.close();
			const clean = await exited;
			if (!clean) roamer.kill();
			for (const f of existsSync(autoSaveDir) ? readdirSync(autoSaveDir) : []) if (f.startsWith(`${tag}-`)) rmSync(join(autoSaveDir, f), { force: true });
			clearAutoRecover(dir);
			if (!clean) throw new Error(`Navisworks pid ${pid} did not exit within 30 s of WM_CLOSE and was killed; the next launch will ask to reload`);
		},
	};
}
