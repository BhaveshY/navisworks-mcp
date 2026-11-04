import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { appendFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve, sep } from 'node:path';
import { after, before, test } from 'node:test';
import { binaries, build, installed, root, tar, versions } from '../scripts/plugin.ts';

// Real binaries and real scripts. No Navisworks process or user configuration is used.
const sandbox = mkdtempSync(join(tmpdir(), 'navisworks-setup-test-'));
const sources = join(sandbox, 'sources');
let archive: string;
before(() => {
	assert.ok(installed.length, 'install a supported Navisworks Manage to run installation e2e tests');
	for (const nw of installed) {
		const out = join(sources, String(nw.year));
		build({ ...nw, out, coreDll: join(out, 'NavisworksMcp.Core.dll') });
	}
	archive = zip('plugin.zip', installed.flatMap((nw) => binaries.map((dll) => `${nw.year}/${dll}`)));
});
after(() => {
	assert.ok(resolve(sandbox).startsWith(resolve(tmpdir()) + sep + 'navisworks-setup-test-'));
	rmSync(sandbox, { recursive: true, force: true, maxRetries: 3 });
});

function zip(name: string, files: string[]) {
	const path = join(sandbox, name);
	execFileSync(tar, ['-a', '-cf', path, '-C', sources, ...files]);
	writeFileSync(`${path}.sha256`, `${createHash('sha256').update(readFileSync(path)).digest('hex')}  ${basename(path)}\n`);
	return path;
}

function isolated(name: string) {
	const dir = join(sandbox, name);
	const env = { ...process.env, APPDATA: join(dir, 'roaming'), LOCALAPPDATA: join(dir, 'local'), CODEX_HOME: join(dir, 'codex') };
	const bundle = join(env.APPDATA, 'Autodesk', 'ApplicationPlugins', 'NavisworksMcp.bundle');
	const run = (script: string, args: string[] = []) => {
		const child = spawnSync(process.execPath, [join(root, 'scripts', script), ...args], { env, cwd: root, encoding: 'utf8', timeout: 45000, windowsHide: true });
		assert.ifError(child.error);
		return { code: child.status, output: child.stdout + child.stderr };
	};
	return { env, dir, bundle, run, setup: (args: string[] = []) => run('setup.ts', ['--zip', archive, ...args]) };
}

function snapshot(dir: string) {
	return readdirSync(dir, { recursive: true, encoding: 'utf8' }).sort().filter((file) => statSync(join(dir, file)).isFile())
		.map((file) => [file, createHash('sha256').update(readFileSync(join(dir, file))).digest('hex')]);
}

test('setup installs real DLLs for every installed year and reruns without changing bundle content', () => {
	const ctx = isolated('install');
	const result = ctx.setup();
	assert.equal(result.code, 0, result.output);
	for (const line of ['ok runtime', 'ok Navisworks', 'ok archive checksum', 'ok archive extraction', 'ok bundle', 'ok server handshake', 'ok setup complete']) assert.ok(result.output.includes(line), result.output);
	assert.match(result.output, /info Codex config.toml snippet:/);
	assert.match(result.output, /info Claude Code command/);
	const xml = readFileSync(join(ctx.bundle, 'PackageContents.xml'), 'utf8');
	assert.equal((xml.match(/<ComponentEntry /g) ?? []).length, installed.length);
	for (const nw of installed) {
		assert.ok(xml.includes(`SeriesMin="Nw${nw.api}" SeriesMax="Nw${nw.api}"`));
		const dir = join(ctx.bundle, 'Contents', String(nw.year));
		const shim = readdirSync(dir).find((file) => /^NavisworksMcp\.[a-f0-9]{12}\.dll$/.test(file));
		assert.ok(shim);
		for (const dll of binaries) assert.deepEqual(readFileSync(join(dir, dll === 'NavisworksMcp.dll' ? shim : dll)), readFileSync(join(sources, String(nw.year), dll)));
	}
	const first = snapshot(ctx.bundle);
	writeFileSync(join(ctx.bundle, 'Contents', String(installed[0]!.year), 'stale.dll'), 'old');
	const second = ctx.setup();
	assert.equal(second.code, 0, second.output);
	assert.deepEqual(snapshot(ctx.bundle), first);
});

test('Codex registration preserves unrelated bytes and replaces its table and subtables once', () => {
	const ctx = isolated('codex');
	mkdirSync(ctx.env.CODEX_HOME, { recursive: true });
	const config = join(ctx.env.CODEX_HOME, 'config.toml');
	const prefix = '\uFEFFmodel = "example"\r\n# preserve spacing\r\n[mcp_servers.other]\r\ncommand  = "untouched.exe"\r\nnotes = """\r\n[mcp_servers.navisworks]\r\nThis is a string, not a table.\r\n"""\r\n';
	const middle = '[mcp_servers.third]\r\ncommand = "third.exe"\r\n';
	const suffix = '[profiles.work]\r\nmodel = "keep"\r\n';
	writeFileSync(config, prefix + '[mcp_servers.navisworks]\r\ncommand = "old.exe"\r\nargs = []\r\n' + middle + '[mcp_servers."navisworks".env]\r\nOLD = "remove"\r\n' + suffix);
	const first = ctx.setup(['--client', 'codex']);
	assert.equal(first.code, 0, first.output);
	assert.match(first.output, /ok codex registration/);
	const expected = prefix + `[mcp_servers.navisworks]\r\ncommand = ${JSON.stringify(process.execPath)}\r\nargs = [${JSON.stringify(join(root, 'server', 'main.ts'))}]\r\n` + middle + suffix;
	assert.deepEqual(readFileSync(config), Buffer.from(expected));
	const second = ctx.setup(['--client', 'codex']);
	assert.equal(second.code, 0, second.output);
	assert.deepEqual(readFileSync(config), Buffer.from(expected));
	const doctor = ctx.run('doctor.ts', ['--client', 'codex']);
	assert.equal(doctor.code, 0, doctor.output);
});

test('Codex registration appends to a config without a final newline and is idempotent', () => {
	const ctx = isolated('codex-append');
	mkdirSync(ctx.env.CODEX_HOME, { recursive: true });
	const config = join(ctx.env.CODEX_HOME, 'config.toml');
	const original = 'model = "keep"\n[mcp_servers.other]\ncommand = "keep.exe"';
	writeFileSync(config, original);
	const first = ctx.setup(['--client', 'codex']);
	assert.equal(first.code, 0, first.output);
	const text = readFileSync(config, 'utf8');
	assert.ok(text.startsWith(original + '\n[mcp_servers.navisworks]\n'));
	assert.equal(ctx.setup(['--client', 'codex']).code, 0);
	assert.equal(readFileSync(config, 'utf8'), text);
});

test('Codex registration retains a BOM before the replaced first table', () => {
	const ctx = isolated('codex-bom');
	mkdirSync(ctx.env.CODEX_HOME, { recursive: true });
	const config = join(ctx.env.CODEX_HOME, 'config.toml');
	const suffix = '[mcp_servers.other]\ncommand = "keep.exe"\n';
	writeFileSync(config, '\uFEFF[mcp_servers.navisworks]\ncommand = "old.exe"\n' + suffix);
	const result = ctx.setup(['--client', 'codex']);
	assert.equal(result.code, 0, result.output);
	const text = readFileSync(config, 'utf8');
	assert.ok(text.startsWith('\uFEFF[mcp_servers.navisworks]'));
	assert.ok(text.endsWith(suffix));
	assert.equal(ctx.setup(['--client', 'codex']).code, 0);
	assert.equal(readFileSync(config, 'utf8'), text);
});

test('source setup and the existing install command produce the same bundle', () => {
	const ctx = isolated('build');
	const built = ctx.run('setup.ts', ['--build']);
	assert.equal(built.code, 0, built.output);
	assert.match(built.output, /ok build/);
	const first = snapshot(ctx.bundle);
	const legacy = ctx.run('plugin.ts', ['install']);
	assert.equal(legacy.code, 0, legacy.output);
	assert.deepEqual(snapshot(ctx.bundle), first);
});

test('a zip missing a detected year fails before changing the bundle', () => {
	const ctx = isolated('missing-year');
	const missing = installed[0]!.year;
	const files = installed.filter((nw) => nw.year !== missing).flatMap((nw) => binaries.map((dll) => `${nw.year}/${dll}`));
	if (!files.length) {
		const other = versions.find((v) => v.year !== missing)!;
		mkdirSync(join(sources, String(other.year)), { recursive: true });
		files.push(`${other.year}/`);
	}
	const incomplete = zip('incomplete.zip', files);
	const result = ctx.run('setup.ts', ['--zip', incomplete]);
	assert.equal(result.code, 1, result.output);
	assert.ok(result.output.includes(`FAIL archive year ${missing}: missing ${missing}/NavisworksMcp.dll\nnext: Use a release zip containing Navisworks Manage ${missing}`), result.output);
	assert.equal(existsSync(ctx.bundle), false);
});

test('a corrupted zip fails SHA-256 verification before extraction or installation', () => {
	const ctx = isolated('corrupt');
	const corrupted = zip('corrupted.zip', installed.flatMap((nw) => binaries.map((dll) => `${nw.year}/${dll}`)));
	appendFileSync(corrupted, 'corrupted');
	const result = ctx.run('setup.ts', ['--zip', corrupted]);
	assert.equal(result.code, 1, result.output);
	assert.match(result.output, /FAIL archive checksum: SHA-256 mismatch[^\n]*\nnext: Obtain corrupted.zip/);
	assert.equal(existsSync(ctx.bundle), false);
	assert.doesNotMatch(result.output, /ok archive extraction/);
});

test('doctor verifies a temp install, preserves its files, and reports an empty install', () => {
	const ctx = isolated('doctor');
	const setup = ctx.setup();
	assert.equal(setup.code, 0, setup.output);
	const state = join(ctx.env.LOCALAPPDATA, 'navisworks-mcp');
	mkdirSync(join(state, 'instances'), { recursive: true });
	writeFileSync(join(state, 'instances', '2147483647.json'), '{"pid":2147483647,"pipe":"stale","started":"2000-01-01"}');
	const before = snapshot(ctx.dir);
	const doctor = ctx.run('doctor.ts');
	assert.equal(doctor.code, 0, doctor.output);
	assert.match(doctor.output, /ok bundle:/);
	assert.match(doctor.output, /ok server handshake: initialize \+ tools\/list \(1 tools\)/);
	assert.match(doctor.output, /info Navisworks with the plugin is not running/);
	assert.deepEqual(snapshot(ctx.dir), before);
	const empty = isolated('empty').run('doctor.ts');
	assert.equal(empty.code, 1, empty.output);
	assert.match(empty.output, /FAIL bundle:[^\n]*\nnext: Run node scripts\/setup.ts/);
	assert.match(empty.output, /ok server handshake:/, 'doctor continues after failure');
});

test('doctor reports only plugin log entries newer than the bundle install', () => {
	const ctx = isolated('log');
	assert.equal(ctx.setup().code, 0);
	const state = join(ctx.env.LOCALAPPDATA, 'navisworks-mcp');
	mkdirSync(state, { recursive: true });
	const log = join(state, 'plugin.log');
	writeFileSync(log, '2000-01-01T00:00:00.000Z pid 1 old error\n');
	assert.equal(ctx.run('doctor.ts').code, 0);
	const recent = new Date(Date.now() + 1000).toISOString();
	appendFileSync(log, `${recent} pid 1 first new error\n${recent} pid 1 last new error\n  stack detail\n`);
	const result = ctx.run('doctor.ts');
	assert.equal(result.code, 1, result.output);
	assert.match(result.output, /FAIL plugin log: .*last new error stack detail\nnext:/);
	assert.doesNotMatch(result.output, /first new error|old error/);
});
