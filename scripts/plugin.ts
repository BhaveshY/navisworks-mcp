import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

/// Every supported Navisworks Manage, oldest first. `api` is the major version of Autodesk.Navisworks.Api, which also names the bundle series and the registry key.
export const versions = [
	{ year: 2024, api: 21 },
	{ year: 2027, api: 24 },
] as const;

export type Navisworks = (typeof versions)[number] & { dir: string; out: string; coreDll: string };

const frameworkDir = 'C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319';
export const root = resolve(import.meta.dirname, '..');
export const version: string = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8')).version;
export const binaries = ['NavisworksMcp.dll', 'NavisworksMcp.Core.dll', 'Newtonsoft.Json.dll'] as const;
export const tar = join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'tar.exe');
const newtonsoft = join(root, 'plugin', 'lib', 'Newtonsoft.Json.dll');
export const bundleDir = join(process.env.APPDATA ?? '', 'Autodesk', 'ApplicationPlugins', 'NavisworksMcp.bundle');

export const installed: Navisworks[] = versions
	.map((v) => {
		const out = join(root, 'build', String(v.year));
		return { ...v, dir: `C:\\Program Files\\Autodesk\\Navisworks Manage ${v.year}`, out, coreDll: join(out, 'NavisworksMcp.Core.dll') };
	})
	.filter((nw) => existsSync(join(nw.dir, 'Roamer.exe')));

/// The installed Navisworks for `year`, or the newest installed one.
export function navisworks(year = process.env.NAVISWORKS_VERSION): Navisworks {
	const nw = year ? installed.find((x) => String(x.year) === year) : installed.at(-1);
	if (nw) return nw;
	const have = installed.length ? `installed: ${installed.map((x) => x.year).join(', ')}` : 'none is installed';
	throw new Error(`Navisworks Manage ${year ?? ''} is not installed or not supported (supported: ${versions.map((v) => v.year).join(', ')}; ${have})`);
}

const frameworkRefs = ['mscorlib', 'System', 'System.Core', 'System.Data', 'System.Drawing', 'System.Windows.Forms', 'System.Xml', 'System.Xml.Linq', 'Microsoft.CSharp'];
const navisworksRefs = [
	'Autodesk.Navisworks.Api',
	'Autodesk.Navisworks.Clash',
	'Autodesk.Navisworks.Timeliner',
	'Autodesk.Navisworks.Takeoff',
	'Autodesk.Navisworks.ComApi',
	'Autodesk.Navisworks.Interop.ComApi',
];

export function findCsc() {
	if (process.env.CSC) return process.env.CSC;
	const vswhere = 'C:\\Program Files (x86)\\Microsoft Visual Studio\\Installer\\vswhere.exe';
	const install = execFileSync(vswhere, ['-latest', '-products', '*', '-property', 'installationPath'], { encoding: 'utf8' }).trim();
	const csc = join(install, 'MSBuild', 'Current', 'Bin', 'Roslyn', 'csc.exe');
	if (!existsSync(csc)) throw new Error(`csc.exe not found at ${csc}. Install VS Build Tools or set CSC.`);
	return csc;
}

const sourcesIn = (dir: string) => readdirSync(dir, { recursive: true, encoding: 'utf8' }).filter((f) => f.endsWith('.cs')).map((f) => join(dir, f));

function compile(out: string, sources: string[], refs: string[]) {
	try {
		execFileSync(findCsc(), [
			'-nologo', '-target:library', '-optimize+', '-debug:portable', '-deterministic', '-langversion:latest', '-warnaserror+', '-nostdlib',
			`-out:${out}`,
			...frameworkRefs.map((r) => `-r:${join(frameworkDir, `${r}.dll`)}`),
			...refs.map((r) => `-r:${r}`),
			...sources,
		], { stdio: 'inherit' });
	} catch {
		throw new Error(`compiling ${out} failed`);
	}
}

/// Builds the shim, the core and the core's Newtonsoft.Json into nw.out, against that version's API.
export function build(nw: Navisworks) {
	mkdirSync(nw.out, { recursive: true });
	const api = (name: string) => join(nw.dir, `${name}.dll`);
	compile(nw.coreDll, [...sourcesIn(join(root, 'plugin', 'core')), join(root, 'plugin', 'compat', `${nw.year}.cs`)], [...navisworksRefs.map(api), newtonsoft]);
	compile(join(nw.out, 'NavisworksMcp.dll'), sourcesIn(join(root, 'plugin', 'shim')), [api('Autodesk.Navisworks.Api')]);
	copyFileSync(newtonsoft, join(nw.out, 'Newtonsoft.Json.dll'));
}

/// Installs one bundle with a component per installed Navisworks, each built against its own API, and removes everything else in it.
/// The shim gets a content-hashed file name, so installing over a running Navisworks never hits a locked file.
export function install(directories: ReadonlyMap<number, string>) {
	// Validate all sources before changing an existing bundle.
	for (const nw of installed) for (const dll of binaries) {
		const dir = directories.get(nw.year);
		if (!dir || !existsSync(join(dir, dll))) throw new Error(`missing ${nw.year}/${dll}`);
	}
	if (!installed.length) throw new Error('no supported Navisworks Manage installed');
	const contents = join(bundleDir, 'Contents');
	const keep = new Set<string>();
	const components = installed.map((nw) => {
		const source = directories.get(nw.year)!;
		const dir = join(contents, String(nw.year));
		const shimDll = join(source, 'NavisworksMcp.dll');
		const shim = `NavisworksMcp.${createHash('sha256').update(readFileSync(shimDll)).digest('hex').slice(0, 12)}.dll`;
		mkdirSync(dir, { recursive: true });
		if (!existsSync(join(dir, shim))) copyFileSync(shimDll, join(dir, shim));
		for (const dll of ['NavisworksMcp.Core.dll', 'Newtonsoft.Json.dll']) copyFileSync(join(source, dll), join(dir, dll));
		for (const file of [dir, ...[shim, 'NavisworksMcp.Core.dll', 'Newtonsoft.Json.dll'].map((f) => join(dir, f))]) keep.add(file);
		return `  <Components Description="Navisworks Manage ${nw.year}">
    <RuntimeRequirements OS="Win64" Platform="NAVMAN" SeriesMin="Nw${nw.api}" SeriesMax="Nw${nw.api}" />
    <ComponentEntry AppName="NavisworksMcp" ModuleName="./Contents/${nw.year}/${shim}" AppType="ManagedPlugin" />
  </Components>
`;
	});
	writeFileSync(join(bundleDir, 'PackageContents.xml'), `<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="Navisworks" ProductType="Application" Name="NavisworksMcp" AppVersion="${version}" Author="navisworks-mcp">
${components.join('')}</ApplicationPackage>
`);
	for (const entry of readdirSync(contents, { recursive: true, encoding: 'utf8' }).sort().reverse()) {
		const path = join(contents, entry);
		if (keep.has(path)) continue;
		try {
			rmSync(path, { recursive: true });
		} catch {
			// A running Navisworks still holds the old shim open; the next install removes it.
		}
	}
	return bundleDir;
}

function release() {
	const missing = versions.filter((v) => !installed.some((nw) => nw.year === v.year));
	if (missing.length) throw new Error(`release needs Navisworks Manage ${missing.map((v) => v.year).join(', ')} installed`);
	for (const nw of installed) build(nw);
	const dist = join(root, 'dist');
	mkdirSync(dist, { recursive: true });
	const name = `navisworks-mcp-plugin-${version}.zip`;
	const zip = join(dist, name);
	execFileSync(tar, ['-a', '-cf', zip, '-C', join(root, 'build'), ...installed.flatMap((nw) => binaries.map((dll) => `${nw.year}/${dll}`))]);
	writeFileSync(`${zip}.sha256`, `${createHash('sha256').update(readFileSync(zip)).digest('hex')}  ${name}\n`);
	console.log(`created ${zip}`);
	console.log(`gh release create v${version} dist/${name} dist/${name}.sha256 --repo BhaveshY/navisworks-mcp --title v${version} --generate-notes`);
}

/// Runs the e2e files against each version in turn, building each first. Every version runs even after one fails.
function test(args: string[]) {
	const years = args.filter((a) => /^\d{4}$/.test(a));
	const files = args.filter((a) => !years.includes(a));
	const chosen = years.length ? years.map((y) => navisworks(y)) : installed;
	const verdicts = chosen.map((nw) => {
		console.log(`\n# Navisworks Manage ${nw.year}`);
		try {
			build(nw);
		} catch (e) {
			return { nw, pass: false, why: (e as Error).message };
		}
		const run = spawnSync(process.execPath, ['--test', '--test-concurrency=6', ...(files.length ? files : ['test/*.test.ts'])], {
			cwd: root,
			stdio: 'inherit',
			env: { ...process.env, NAVISWORKS_VERSION: String(nw.year) },
		});
		return { nw, pass: run.status === 0, why: run.status === 0 ? '' : `node --test exited ${run.status ?? run.signal}` };
	});
	console.log('');
	for (const v of verdicts) console.log(`Navisworks Manage ${v.nw.year}: ${v.pass ? 'pass' : `FAIL (${v.why})`}`);
	return verdicts.every((v) => v.pass) ? 0 : 1;
}

if (import.meta.main) {
	const [command, ...args] = process.argv.slice(2);
	if (command === 'build') {
		for (const nw of args.length ? args.map((y) => navisworks(y)) : installed) {
			build(nw);
			console.log(`built ${nw.out}`);
		}
	} else if (command === 'install') {
		for (const nw of installed) build(nw);
		console.log(`installed ${install(new Map(installed.map((nw) => [nw.year, nw.out])))}`);
	}
	else if (command === 'release') release();
	else if (command === 'test') process.exitCode = test(args);
	else throw new Error('usage: node scripts/plugin.ts build [year...] | install | release | test [year...] [file...]');
}
