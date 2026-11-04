import { spawn } from 'node:child_process';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { createInterface } from 'node:readline';
import { bundleDir, installed } from './plugin.ts';
import { checkClaude, checkCodex, checkRuntime, failure, main, oneLine, options, years, type Client } from './installation.ts';

const state = join(process.env.LOCALAPPDATA ?? '', 'navisworks-mcp');
const manifest = join(bundleDir, 'PackageContents.xml');

function checkBundle() {
	const xml = readFileSync(manifest, 'utf8');
	for (const nw of installed) {
		const components = xml.match(/<Components\b[^>]*>[\s\S]*?<\/Components>/g) ?? [];
		const component = components.find((c) => c.includes(`SeriesMin="Nw${nw.api}"`) && c.includes(`SeriesMax="Nw${nw.api}"`));
		const module = component && /<ComponentEntry\b[^>]*\bModuleName="\.\/Contents\/(\d{4})\/(NavisworksMcp\.[a-f0-9]{12}\.dll)"/.exec(component);
		if (!module || module[1] !== String(nw.year)) throw new Error(`missing component for ${nw.year}`);
		for (const dll of [module[2]!, 'NavisworksMcp.Core.dll', 'Newtonsoft.Json.dll']) {
			const path = join(bundleDir, 'Contents', String(nw.year), dll);
			if (!statSync(path).isFile() || statSync(path).size === 0) throw new Error(`missing or empty ${path}`);
		}
	}
}

function livePids() {
	const dir = join(state, 'instances');
	if (!existsSync(dir)) return [];
	return readdirSync(dir).flatMap((file) => {
		if (!/^\d+\.json$/.test(file)) return [];
		try {
			const instance = JSON.parse(readFileSync(join(dir, file), 'utf8'));
			if (instance.pid !== Number(file.slice(0, -5)) || instance.pid <= 0 || typeof instance.pipe !== 'string') return [];
			try { process.kill(instance.pid, 0); }
			catch (e) { if ((e as NodeJS.ErrnoException).code !== 'EPERM') return []; }
			return [instance.pid as number];
		} catch { return []; }
	});
}

// Use the real stdio protocol without installing the SDK. Kill only this child.
function probe() {
	const child = spawn(process.execPath, [main], {
		stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true,
		env: { ...process.env, NAVISWORKS_MCP_PID: '' },
	});
	const pending = new Map<number, { resolve: (value: any) => void; reject: (error: Error) => void }>();
	let sequence = 0;
	let stderr = '';
	let stopped: Error | undefined;
	const rejectAll = (error: Error) => {
		stopped = error;
		for (const p of pending.values()) p.reject(error);
		pending.clear();
	};
	child.stderr.on('data', (data) => { stderr = (stderr + data).slice(-2000); });
	child.on('error', rejectAll);
	child.stdin.on('error', rejectAll);
	child.on('exit', (code) => rejectAll(new Error(`server exited ${code}: ${stderr}`)));
	const closed = new Promise<void>((resolve) => child.once('close', () => resolve()));
	const lines = createInterface({ input: child.stdout });
	lines.on('line', (line) => {
		try {
			const msg = JSON.parse(line);
			const p = pending.get(msg.id);
			if (!p) return;
			pending.delete(msg.id);
			if (msg.error) p.reject(new Error(JSON.stringify(msg.error)));
			else p.resolve(msg.result);
		} catch { rejectAll(new Error('server emitted invalid JSON')); }
	});
	return {
		async call(method: string, params: object = {}, timeout = 10000) {
			if (stopped) throw stopped;
			const id = ++sequence;
			let timer: NodeJS.Timeout | undefined;
			try {
				return await new Promise<any>((resolve, reject) => {
					pending.set(id, { resolve, reject });
					timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timed out within the 10 s probe limit`)); }, timeout);
					child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
				});
			} finally { clearTimeout(timer); }
		},
		initialized() { child.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n'); },
		async close() { lines.close(); child.kill(); await closed; },
	};
}

export async function doctor(client: Client = 'none') {
	let failed = false;
	const check = async (label: string, next: string, fn: () => unknown | Promise<unknown>) => {
		try {
			const detail = await fn();
			console.log(`ok ${label}${detail ? `: ${oneLine(detail)}` : ''}`);
			return true;
		} catch (e) { failed = true; failure(label, e, next); return false; }
	};
	await check('runtime', 'Install Node 24 or later on Windows, then rerun node scripts/doctor.ts.', checkRuntime);
	await check('Navisworks', `Install Navisworks Manage ${years}, then rerun node scripts/doctor.ts.`, () => {
		if (!installed.length) throw new Error('no supported Navisworks Manage installed');
		return installed.map((nw) => nw.year).join(', ');
	});
	await check('bundle', 'Run node scripts/setup.ts to install or repair the bundle.', () => { checkBundle(); return bundleDir; });
	const server = probe();
	const handshakeDeadline = Date.now() + 10000;
	try {
		const ready = await check('server handshake', 'Run node server/main.ts with Node 24 or later; fix the reported startup error, then rerun node scripts/doctor.ts.', async () => {
			const hello = await server.call('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'doctor', version: '1' } });
			if (!hello?.serverInfo || !hello?.capabilities?.tools) throw new Error('invalid initialize response');
			server.initialized();
			const result = await server.call('tools/list', {}, Math.max(1, handshakeDeadline - Date.now()));
			if (!result?.tools?.some((t: { name: string }) => t.name === 'status')) throw new Error('tools/list did not include status');
			return `initialize + tools/list (${result.tools.length} tools)`;
		});
		await check('plugin status', 'Start or restart Navisworks Manage, wait for it to finish loading, then rerun node scripts/doctor.ts.', async () => {
			const pids = livePids();
			if (!pids.length) {
				console.log('info Navisworks with the plugin is not running; start Navisworks, then rerun npm run doctor to confirm the plugin loads');
				return 'no live instance to check';
			}
			if (!ready) throw new Error('server handshake failed');
			const versions: string[] = [];
			for (const pid of pids) {
				const result = await server.call('tools/call', { name: 'status', arguments: { pid } });
				const text = result?.content?.filter((c: { type: string }) => c.type === 'text').map((c: { text: string }) => c.text).join('\n') ?? '';
				if (result?.isError) throw new Error(text);
				const line = text.split(/\r?\n/).find((line: string) => /^navisworks pid /i.test(line));
				if (!line) throw new Error(`pid ${pid} returned no Navisworks version line: ${text}`);
				versions.push(`pid ${pid}: ${line}`);
			}
			return versions.join('; ');
		});
	} finally { await server.close(); }
	await check('plugin log', 'Fix the reported plugin error; rerun node scripts/setup.ts, restart Navisworks, then rerun node scripts/doctor.ts.', () => {
		const log = join(state, 'plugin.log');
		if (!existsSync(log)) return 'no startup errors';
		const installedAt = existsSync(manifest) ? statSync(manifest).mtimeMs : 0;
		const entries = readFileSync(log, 'utf8').split(/(?=^\d{4}-\d\d-\d\dT)/m);
		const recent = entries.filter((entry) => Date.parse(entry.split(/\s/)[0]!) > installedAt);
		if (recent.length) throw new Error(recent.at(-1)!.trim());
		return 'no errors newer than the bundle';
	});
	if (client !== 'none') await check(`${client} registration`, `Run node scripts/setup.ts --client ${client}. For Claude Code, install claude on PATH first.`, client === 'codex' ? checkCodex : checkClaude);
	return failed ? 1 : 0;
}

if (import.meta.main) {
	try { process.exitCode = await doctor(options(process.argv.slice(2), false).client); }
	catch (e) { failure('doctor', e, 'Run node scripts/doctor.ts [--client codex|claude].'); process.exitCode = 1; }
}
