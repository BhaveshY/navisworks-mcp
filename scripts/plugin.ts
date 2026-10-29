import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

export const navisworksDir = process.env.NAVISWORKS_DIR ?? 'C:\\Program Files\\Autodesk\\Navisworks Manage 2024';
const frameworkDir = 'C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319';
const root = resolve(import.meta.dirname, '..');
export const outDir = join(root, 'build');
export const coreDll = join(outDir, 'NavisworksMcp.Core.dll');
const shimDll = join(outDir, 'NavisworksMcp.dll');
export const bundleDir = join(process.env.APPDATA!, 'Autodesk', 'ApplicationPlugins', 'NavisworksMcp.bundle');

const frameworkRefs = ['mscorlib', 'System', 'System.Core', 'System.Data', 'System.Drawing', 'System.Windows.Forms', 'System.Xml', 'System.Xml.Linq', 'Microsoft.CSharp'];
const navisworksRefs = [
	'Autodesk.Navisworks.Api',
	'Autodesk.Navisworks.Clash',
	'Autodesk.Navisworks.Timeliner',
	'Autodesk.Navisworks.Takeoff',
	'Autodesk.Navisworks.ComApi',
	'Autodesk.Navisworks.Interop.ComApi',
	'Newtonsoft.Json',
];

function findCsc() {
	if (process.env.CSC) return process.env.CSC;
	const vswhere = 'C:\\Program Files (x86)\\Microsoft Visual Studio\\Installer\\vswhere.exe';
	const install = execFileSync(vswhere, ['-latest', '-products', '*', '-property', 'installationPath'], { encoding: 'utf8' }).trim();
	const csc = join(install, 'MSBuild', 'Current', 'Bin', 'Roslyn', 'csc.exe');
	if (!existsSync(csc)) throw new Error(`csc.exe not found at ${csc}. Install VS Build Tools or set CSC.`);
	return csc;
}

function compile(out: string, sourceDir: string, refs: string[]) {
	const sources = readdirSync(sourceDir, { recursive: true, encoding: 'utf8' }).filter((f) => f.endsWith('.cs')).map((f) => join(sourceDir, f));
	try {
		execFileSync(findCsc(), [
			'-nologo', '-target:library', '-optimize+', '-debug:portable', '-langversion:latest', '-warnaserror+', '-nostdlib',
			`-out:${out}`,
			...frameworkRefs.map((r) => `-r:${join(frameworkDir, `${r}.dll`)}`),
			...refs.map((r) => `-r:${join(navisworksDir, `${r}.dll`)}`),
			...sources,
		], { stdio: 'inherit' });
	} catch {
		console.error(`compiling ${out} failed`);
		process.exit(1);
	}
}

export function build() {
	mkdirSync(outDir, { recursive: true });
	compile(coreDll, join(root, 'plugin', 'core'), navisworksRefs);
	compile(shimDll, join(root, 'plugin', 'shim'), ['Autodesk.Navisworks.Api']);
}

/// The shim gets a content-hashed file name, so installing over a running Navisworks never hits a locked file.
export function install() {
	const contents = join(bundleDir, 'Contents');
	mkdirSync(contents, { recursive: true });
	const shim = `NavisworksMcp.${createHash('sha256').update(readFileSync(shimDll)).digest('hex').slice(0, 12)}.dll`;
	if (!existsSync(join(contents, shim))) copyFileSync(shimDll, join(contents, shim));
	copyFileSync(coreDll, join(contents, 'NavisworksMcp.Core.dll'));
	writeFileSync(join(bundleDir, 'PackageContents.xml'), `<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="Navisworks" ProductType="Application" Name="NavisworksMcp" AppVersion="1.0.0" Author="navisworks-mcp">
  <Components Description="Navisworks Manage 2024">
    <RuntimeRequirements OS="Win64" Platform="NAVMAN" SeriesMin="Nw21" SeriesMax="Nw21" />
    <ComponentEntry AppName="NavisworksMcp" ModuleName="./Contents/${shim}" AppType="ManagedPlugin" />
  </Components>
</ApplicationPackage>
`);
	for (const old of readdirSync(contents)) {
		if (old === shim || old === 'NavisworksMcp.Core.dll') continue;
		try {
			rmSync(join(contents, old));
		} catch {
			// A running Navisworks still holds the old shim open; the next install removes it.
		}
	}
	return bundleDir;
}

if (import.meta.main) {
	build();
	console.log(process.argv[2] === 'build' ? `built ${outDir}` : `installed ${install()}`);
}
