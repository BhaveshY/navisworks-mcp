import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { root, versions } from './plugin.ts';

export const main = join(root, 'server', 'main.ts');
export const codexConfig = join(process.env.CODEX_HOME ?? join(process.env.USERPROFILE ?? homedir(), '.codex'), 'config.toml');
export const years = versions.map((v) => v.year).join(' or ');
export const oneLine = (value: unknown) => String(value instanceof Error ? value.message : value).replace(/\s+/g, ' ').trim();

export function failure(label: string, error: unknown, next: string) {
	console.log(`FAIL ${label}: ${oneLine(error)}\nnext: ${next}`);
}

export function checkRuntime() {
	if (process.platform !== 'win32' || Number(process.versions.node.split('.')[0]) < 24)
		throw new Error(`requires Windows and Node >= 24; found ${process.platform}, Node ${process.versions.node}`);
}

export type Client = 'codex' | 'claude' | 'none';
export function options(args: string[], setup: boolean) {
	let client: Client = 'none';
	let zip: string | undefined;
	let build = false;
	for (let i = 0; i < args.length; i++) {
		const arg = args[i];
		if (arg === '--client') {
			const value = args[++i];
			if (value !== 'codex' && value !== 'claude' && !(setup && value === 'none')) throw new Error('--client must be codex, claude' + (setup ? ' or none' : ''));
			client = value;
		} else if (arg === '--build' && setup) build = true;
		else if (arg === '--zip' && setup) {
			zip = args[++i];
			if (!zip || zip.startsWith('--')) throw new Error('--zip needs a path');
		} else throw new Error(`unknown option ${arg}`);
	}
	if (build && zip) throw new Error('choose --build or --zip, not both');
	return { client, zip, build };
}

export const codexSnippet = () => `[mcp_servers.navisworks]\ncommand = ${JSON.stringify(process.execPath)}\nargs = [${JSON.stringify(main)}]\n`;
const psQuote = (s: string) => `'${s.replaceAll("'", "''")}'`;
export const claudeSnippet = () => `claude mcp add navisworks --scope user -- ${psQuote(process.execPath)} ${psQuote(main)}`;

// Scan table headers outside TOML strings and comments. Keep original byte offsets so
// replacing our tables does not reformat any other setting, BOM or line ending.
function tables(text: string) {
	const headers: { start: number; end: number; keys: string[] }[] = [];
	let multiline = '';
	let offset = text.startsWith('\uFEFF') ? 1 : 0;
	for (const line of text.slice(offset).match(/[^\n]*\n|[^\n]+$/g) ?? []) {
		if (!multiline) {
			const header = /^\s*\[([^\r\n]+)\]\s*(?:#.*)?(?:\r?\n)?$/.exec(line);
			if (header) {
				const tokens = header[1]!.match(/"(?:\\.|[^"\\])*"|'[^']*'|[A-Za-z0-9_-]+|\./g) ?? [];
				const keys = tokens.filter((t) => t !== '.').map((t) => t.startsWith('"') ? JSON.parse(t) as string : t.startsWith("'") ? t.slice(1, -1) : t);
				headers.push({ start: offset, end: text.length, keys });
			}
		}
		let quote = '';
		for (let i = 0; i < line.length; i++) {
			if (multiline) {
				if (multiline === '"""' && line[i] === '\\') { i++; continue; }
				if (line.startsWith(multiline, i)) { i += 2; multiline = ''; }
			} else if (quote) {
				if (quote === '"' && line[i] === '\\') i++;
				else if (line[i] === quote) quote = '';
			} else if (line[i] === '#') break;
			else if (line.startsWith('"""', i) || line.startsWith("'''", i)) { multiline = line.slice(i, i + 3); i += 2; }
			else if (line[i] === '"' || line[i] === "'") quote = line[i]!;
		}
		offset += line.length;
	}
	for (let i = 0; i < headers.length - 1; i++) headers[i]!.end = headers[i + 1]!.start;
	return headers;
}

function ourTables(text: string) {
	return tables(text).filter((h) => h.keys[0] === 'mcp_servers' && h.keys[1] === 'navisworks');
}

export function registerCodex() {
	const text = existsSync(codexConfig) ? readFileSync(codexConfig, 'utf8') : '';
	const ranges = ourTables(text);
	const newline = text.includes('\r\n') ? '\r\n' : '\n';
	const snippet = codexSnippet().replaceAll('\n', newline);
	let result = '';
	let offset = 0;
	for (const [i, range] of ranges.entries()) {
		result += text.slice(offset, range.start) + (i === 0 ? snippet : '');
		offset = range.end;
	}
	result += text.slice(offset);
	if (!ranges.length) result += (text.length && !text.endsWith('\n') ? newline : '') + snippet;
	mkdirSync(dirname(codexConfig), { recursive: true });
	writeFileSync(codexConfig, result);
}

export function checkCodex() {
	const text = readFileSync(codexConfig, 'utf8');
	const ranges = ourTables(text).filter((h) => h.keys.length === 2);
	if (ranges.length !== 1) throw new Error(`expected one [mcp_servers.navisworks] table in ${codexConfig}`);
	const table = text.slice(ranges[0]!.start, ranges[0]!.end);
	// Setup writes JSON-compatible TOML basic strings. Also accept literal strings.
	const string = String.raw`(?:"(?:\\.|[^"\\])*"|'[^']*')`;
	const value = (key: string, pattern: string) => new RegExp(`^\\s*${key}\\s*=\\s*(${pattern})\\s*(?:#.*)?$`, 'm').exec(table)?.[1];
	const decode = (v: string | undefined) => v?.startsWith("'") ? v.slice(1, -1) : JSON.parse(v ?? 'null');
	const command = decode(value('command', string));
	const argsValue = value('args', `\\[\\s*${string}\\s*,?\\s*\\]`);
	const server = argsValue && decode(new RegExp(string).exec(argsValue)?.[0]);
	if (command !== process.execPath || server !== main)
		throw new Error(`registration does not point to ${process.execPath} ${main}`);
}

export function claude(args: string[]) {
	let executable: string;
	try {
		executable = execFileSync('where.exe', ['claude'], { encoding: 'utf8', windowsHide: true }).trim().split(/\r?\n/).find((p) => /\.(exe|cmd|bat)$/i.test(p))!;
		if (!executable) throw new Error('not found');
	} catch { throw new Error('claude is not on PATH; install Claude Code and open a new terminal'); }
	if (/\.(cmd|bat)$/i.test(executable)) {
		return execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', `& ${[executable, ...args].map(psQuote).join(' ')}; exit $LASTEXITCODE`], { encoding: 'utf8', windowsHide: true, timeout: 20000 });
	}
	return execFileSync(executable, args, { encoding: 'utf8', windowsHide: true, timeout: 20000 });
}

export function registerClaude() {
	try { claude(['mcp', 'remove', 'navisworks', '--scope', 'user']); }
	catch (e) {
		const output = `${(e as { stdout?: string }).stdout ?? ''} ${(e as { stderr?: string }).stderr ?? ''}`;
		if (!/server.*(?:not found|does not exist)|no .*server.*(?:found|named)|no .*server.*with.*name/i.test(output)) throw e;
	}
	claude(['mcp', 'add', 'navisworks', '--scope', 'user', '--', process.execPath, main]);
}

export function checkClaude() {
	const output = claude(['mcp', 'get', 'navisworks']);
	const field = (name: string) => new RegExp(`^\\s*${name}:\\s*(.+)$`, 'm').exec(output)?.[1]?.trim();
	if (field('Command') !== process.execPath || field('Args') !== main) throw new Error(`registration does not point to ${process.execPath} ${main}`);
}
