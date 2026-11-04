import { navisworks } from '../scripts/plugin.ts';
import assert from 'node:assert/strict';
import { copyFileSync, cpSync, existsSync, mkdirSync, readFileSync, unlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { after, before, test } from 'node:test';
import { boxes } from './fixture/scene.ts';
import { startSession, type Session } from './navisworks.ts';

let nw: Session;
before(async () => (nw = await startSession()));
after(() => nw?.close());

const count = (pred: (b: (typeof boxes)[number]) => boolean) => boxes.filter(pred).length;
const total = (text: string) => Number(/items ([\d,]+) \(exact\)/.exec(text)?.[1]?.replace(/,/g, ''));
const walls = count((b) => b.ifcClass === 'IfcWall');
const columns = boxes.filter((b) => b.ifcClass === 'IfcColumn');
const rowsEnding = (text: string, suffix: string) => text.split('\n').filter((l) => l.endsWith(suffix)).length;
const apply = (...ops: Record<string, unknown>[]) => nw.call('apply', { ops });
const png = (path: string) => readFileSync(path);
const modelItems = async () => Number(/^model 0 [^:]*: ([\d,]+) items/m.exec(await nw.call('read', { what: 'stats' }))![1]!.replace(/,/g, ''));

test('a saved viewpoint keeps camera, hide and colour overrides, and showing it restores them', async () => {
	await apply(
		{ op: 'color', items: '?Item.Type = IfcWall', rgb: '#ff0000' },
		{ op: 'hide', items: '?Item.Type = IfcSlab' },
		{ op: 'viewpoint.save', name: 'QA/Level 1', items: '?Item.Type = IfcWall' },
	);
	const list = await nw.call('read', { what: 'viewpoints' });
	assert.match(list, /^QA\|folder/m);
	assert.match(list, /^QA\/Level 1\|view\|hide\+colour\|off$/m);

	await apply({ op: 'reset' });
	const cleared = await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall or Item.Type = IfcSlab', cols: 'color,hidden' });
	assert.equal(rowsEnding(cleared, '|false'), walls + 1);
	assert.doesNotMatch(cleared, /#ff0000/);

	assert.match(await apply({ op: 'viewpoint.show', name: 'QA/Level 1' }), /showing "QA\/Level 1"/);
	const restored = await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall or Item.Type = IfcSlab', cols: 'color,hidden' });
	assert.equal(rowsEnding(restored, '|#ff0000|false'), walls);
	assert.match(restored, /\|S-Level2\|IfcSlab\|#[0-9a-f]{6}\|true$/m);
	await apply({ op: 'reset' });
});

test('viewpoint.save replaces by path; rename and delete work on paths', async () => {
	assert.match(await apply({ op: 'viewpoint.save', name: 'QA/Temp' }), /saved viewpoint "QA\/Temp"/);
	assert.match(await apply({ op: 'viewpoint.save', name: 'QA/Temp' }), /replaced viewpoint "QA\/Temp"/);
	assert.equal((await nw.call('read', { what: 'viewpoints' })).match(/QA\/Temp\|/g)?.length, 1);

	await apply({ op: 'viewpoint.rename', name: 'QA/Temp', to: 'Gone' });
	assert.match(await nw.call('read', { what: 'viewpoints', folder: 'QA' }), /^QA\/Gone\|view/m);
	await apply({ op: 'viewpoint.delete', name: 'QA/Gone' });
	assert.doesNotMatch(await nw.call('read', { what: 'viewpoints' }), /QA\/Gone/);

	const missing = await nw.callRaw('apply', { ops: [{ op: 'viewpoint.show', name: 'QA/Nope' }] });
	assert.equal(missing.isError, true);
	assert.match(missing.text, /error not_found: .*no viewpoint "QA\/Nope"[\s\S]*next: list viewpoints with read/);
});

test('zoom fits the view to the items bounding box', async () => {
	const text = await apply({ op: 'zoom', items: '?Item.Type = IfcColumn' });
	const min = [0, 1, 2].map((i) => Math.min(...columns.map((c) => c.min[i]!)));
	const max = [0, 1, 2].map((i) => Math.max(...columns.map((c) => c.max[i]!)));
	const fmt = (p: number[]) => p.map((v) => Math.round(v)).join(',');
	assert.match(text, new RegExp(`zoomed to ${columns.length} items \\(${fmt(min)} \\.\\. ${fmt(max)} mm\\)`));

	const corners = [0, 1, 2, 3, 4, 5, 6, 7].map((k) => [k & 1 ? max[0] : min[0], k & 2 ? max[1] : min[1], k & 4 ? max[2] : min[2]]);
	const extent = async () => {
		const code = `var v = nw.Doc.ActiveView; var xs = new List<int>(); var ys = new List<int>();
			foreach (var p in new[] { ${corners.map((p) => `new Point3D(${p.join(',')})`).join(', ')} }) {
				var r = v.ProjectPoint(p, false, false); if (r == null) return "offscreen"; xs.Add(r.X); ys.Add(r.Y); }
			return (double)(xs.Max() - xs.Min()) / v.Width + " " + (double)(ys.Max() - ys.Min()) / v.Height + " " + (xs.Min() >= 0 && ys.Min() >= 0 && xs.Max() <= v.Width && ys.Max() <= v.Height);`;
		const [w, h, inside] = (await nw.call('script', { code })).split(' ');
		return { span: Math.max(Number(w), Number(h)), inside: inside === 'True' };
	};
	const fitted = await extent();
	assert.equal(fitted.inside, true, 'every corner of the columns box is on screen');
	await apply({ op: 'zoom', items: 'all' });
	const wide = await extent();
	assert.ok(fitted.span > 0.3, `the columns fill the view after zoom (span ${fitted.span})`);
	assert.ok(fitted.span > wide.span * 1.5, `zooming to the columns enlarges them (${fitted.span} vs ${wide.span})`);
});

test('section planes and boxes read back, change the rendered image, and switch off', async () => {
	const image = async () => /^image (.+\.png) /m.exec(await nw.call('export', { out: [{ what: 'image', w: 320, h: 200 }] }))![1]!;
	assert.equal(await nw.call('read', { what: 'section' }), 'section off');
	await apply({ op: 'zoom', items: 'all' });
	const plain = png(await image());

	assert.match(await apply({ op: 'section', planes: ['z<1500', 'x>2000'] }), /section planes z<1500 · x>2000/);
	assert.equal(await nw.call('read', { what: 'section' }), 'section planes z<1500 · x>2000');
	assert.notDeepEqual(png(await image()), plain);

	await apply({ op: 'section', planes: [{ normal: [0, 0, 2], point: [0, 0, 1000] }] });
	assert.equal(await nw.call('read', { what: 'section' }), 'section planes z>1000');

	const pad = 100;
	const min = [0, 1, 2].map((i) => Math.min(...columns.map((c) => c.min[i]!)) - pad);
	const max = [0, 1, 2].map((i) => Math.max(...columns.map((c) => c.max[i]!)) + pad);
	await apply({ op: 'section', box: '?Item.Type = IfcColumn', pad });
	assert.equal(await nw.call('read', { what: 'section' }), `section box ${min.join(',')} .. ${max.join(',')} mm`);
	assert.notDeepEqual(png(await image()), plain);

	await apply({ op: 'section', off: true });
	assert.equal(await nw.call('read', { what: 'section' }), 'section off');
	assert.deepEqual(png(await image()), plain);

	const bad = await nw.callRaw('apply', { ops: [{ op: 'section', planes: ['up<3'] }] });
	assert.equal(bad.isError, true);
	assert.match(bad.text, /error bad_args: .*cannot read plane[\s\S]*next: pass planes:\["z<1500"\]/);
	const both = await nw.callRaw('apply', { ops: [{ op: 'section', off: true, planes: ['z<1'] }] });
	assert.match(both.text, /exactly one of planes, box or off/);
});

test('a viewpoint saves its section, and export image renders a saved viewpoint', async () => {
	await apply({ op: 'section', planes: ['z<1500'] }, { op: 'viewpoint.save', name: 'QA/Cut' }, { op: 'section', off: true });
	assert.match(await nw.call('read', { what: 'viewpoints' }), /^QA\/Cut\|view\|[^|]*\|1 plane\(s\)$/m);

	const r = await nw.callRaw('export', { out: [{ what: 'image', view: 'QA/Cut', w: 320, h: 200, inline: true }] });
	assert.equal(r.isError, false, r.text);
	assert.match(r.text, /^image .+\.png 320x200 of "QA\/Cut"$/m);
	assert.equal(r.images, 1);
	assert.equal(await nw.call('read', { what: 'section' }), 'section planes z<1500');
	await apply({ op: 'section', off: true });
});

test('user properties merge into a tab, show in props and where, and undo reverts them', async () => {
	assert.match(await apply({ op: 'props.set', items: '?Item.Type = IfcColumn', tab: 'QA', props: { Status: 'Checked', Round: 2, Done: true } }),
		new RegExp(`set Status, Round, Done in tab "QA" on ${columns.length} items`));
	await apply({ op: 'props.set', items: '?Item.Name = C-1', tab: 'QA', props: { Status: 'Open', Note: 'see RFI 12' } });

	const props = await nw.call('read', { what: 'props', items: '?Item.Type = IfcColumn', tabs: 'QA' });
	assert.match(props, /C-1 · IfcColumn\n\[QA\] Status=Open · Round=2 · Done=true · Note=see RFI 12/);
	assert.match(props, /C-2 · IfcColumn\n\[QA\] Status=Checked · Round=2 · Done=true$/m);
	assert.equal(props.match(/\[QA\]/g)?.length, columns.length);

	assert.equal(total(await nw.call('read', { what: 'items', where: 'QA.Status = Checked' })), columns.length - 1);
	assert.equal(total(await nw.call('read', { what: 'items', where: 'QA.Round = 2' })), columns.length);

	await apply({ op: 'undo' });
	assert.match(await nw.call('read', { what: 'props', items: '?Item.Name = C-1', tabs: 'QA' }), /\[QA\] Status=Checked · Round=2 · Done=true$/m);

	await apply({ op: 'props.delete', items: '?Item.Type = IfcColumn', tab: 'QA', names: 'Round' });
	assert.match(await nw.call('read', { what: 'props', items: '?Item.Name = C-2', tabs: 'QA' }), /\[QA\] Status=Checked · Done=true$/m);

	const bad = await nw.callRaw('apply', { ops: [{ op: 'props.set', items: '?Item.Type = IfcColumn', tab: 'QA', props: { Bad: [1] } }] });
	assert.match(bad.text, /error bad_args: .*property Bad is Array/);
});

test('NWD, NWF and CSV exports round-trip items, sets, viewpoints and user properties', async () => {
	const nwd = nw.path('out', 'coord.nwd');
	const nwf = nw.path('coord.nwf');
	const csv = nw.path('walls.csv');
	await apply({ op: 'set.save', name: 'QA/Walls', where: 'Item.Type = IfcWall' });
	const before = await modelItems();
	assert.ok(before > boxes.length, String(before));
	const out = await nw.call('export', { out: [
		{ what: 'nwd', path: nwd },
		{ what: 'csv', items: '?Item.Type = IfcWall', cols: 'Pset_Fixture.FireRating', path: csv },
	] });
	assert.match(out, /^nwd .+coord\.nwd [\d,]+ KB$/m);
	assert.match(out, new RegExp(`^csv .+walls\\.csv ${walls} rows$`, 'm'));
	const lines = readFileSync(csv, 'utf8').replace(/^﻿/, '').trim().split(/\r?\n/);
	assert.equal(lines[0], 'id,name,type,Pset_Fixture.FireRating');
	for (const b of boxes.filter((b) => b.ifcClass === 'IfcWall')) assert.ok(lines.some((l) => l.endsWith(`,${b.name},IfcWall,${b.props.FireRating}`)), b.name);
	assert.match(await nw.call('status'), /doc .*fixture\.ifc/);

	assert.match(await nw.call('export', { out: [{ what: 'nwf', path: nwf }] }), /document is now [0-9a-f]{8}-coord\.nwf/);

	const survived = async (file: string) => {
		assert.match(await apply({ op: 'open', path: file }), new RegExp(`opened ${file.split(/[\\/]/).pop()!.replace('.', '\\.')}: 1 model\\(s\\)`));
		assert.equal(await modelItems(), before, file);
		assert.equal(total(await nw.call('read', { what: 'items', in: 'set:QA/Walls' })), walls, file);
		const vps = await nw.call('read', { what: 'viewpoints' });
		assert.match(vps, /^QA\/Level 1\|view\|hide\+colour\|off$/m, file);
		assert.match(vps, /^QA\/Cut\|view\|[^|]*\|1 plane\(s\)$/m, file);
		assert.equal(total(await nw.call('read', { what: 'items', where: 'QA.Status = Checked' })), columns.length, file);
	};
	await survived(nwd);
	await survived(nwf);
});

test('append, model.remove, merge and refresh change the models of the document', async () => {
	const copy = nw.path('copy.ifc');
	copyFileSync(nw.fixture, copy);
	const items = Number(/1 model\(s\), ([\d,]+) items/.exec(await apply({ op: 'open', path: nw.fixture }))![1]!.replace(/,/g, ''));

	assert.match(await apply({ op: 'append', paths: [copy] }), new RegExp(`1 model\\(s\\) added, now 2 model\\(s\\), ${(2 * items).toLocaleString('en-US')} items`));
	assert.equal(total(await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall' })), 2 * walls);
	assert.match(await nw.call('status'), /^1\|[0-9a-f]{8}-copy\.ifc\|/m);

	const together = await nw.callRaw('apply', { ops: [{ op: 'model.remove', model: 1 }, { op: 'zoom', items: 'all' }] });
	assert.match(together.text, /model\.remove must be the only op/);
	assert.match(await apply({ op: 'model.remove', model: 1 }), /removed model 1 [0-9a-f]{8}-copy\.ifc: now 1 model\(s\)/);
	assert.equal(total(await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall' })), walls);

	assert.match(await apply({ op: 'merge', paths: copy }), /merged 1 file\(s\)/);
	assert.match(await apply({ op: 'refresh' }), /no file changed/);

	const missing = await nw.callRaw('apply', { ops: [{ op: 'append', paths: [nw.path('nope.nwc')] }] });
	assert.match(missing.text, /error not_found: no file .*nope\.nwc/);
	const outOfRange = await nw.callRaw('apply', { ops: [{ op: 'model.remove', model: 9 }] });
	assert.match(outOfRange.text, /error bad_args: no model 9/);
});


test('F3 saved-viewpoint regressions', async (t) => {
	const apply = (...ops: Record<string, unknown>[]) => nw.call('apply', { ops });
	const script = async (code: string) => (await nw.call('script', { code })).replace(/\nundo:.*$/, '');
	await t.test('B4 redo preserves all-visible and partially hidden saved viewpoints', async () => {
		for (const hidden of [false, true]) for (let repeat = 0; repeat < 2; repeat++) {
			await apply({ op: 'open', path: nw.fixture });
			if (hidden) await apply({ op: 'hide', items: '?Item.Type = IfcWall' });
			await apply({ op: 'section', planes: ['z<1500'] });
			await apply({ op: 'viewpoint.save', name: 'Visibility', items: '?Item.Type = IfcWall' });
			await apply({ op: 'undo' });
			await apply({ op: 'redo' });
			await apply({ op: hidden ? 'show' : 'hide', items: '?Item.Type = IfcWall' });
			await apply({ op: 'section', off: true });
			await apply({ op: 'viewpoint.show', name: 'Visibility' });
			assert.equal(await nw.call('read', { what: 'section' }), 'section planes z<1500');
			const read = await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall', cols: 'hidden' });
			assert.equal(read.split('\n').filter(l => l.endsWith(`|${hidden}`)).length, boxes.filter(b => b.ifcClass === 'IfcWall').length, read);
		}
	});
	await t.test('A9 replacing the currently shown Gatehouse viewpoint preserves its camera and rendered building', async () => {
		const path = nw.path('Gatehouse.dwg');
		copyFileSync(join(navisworks().dir, 'Samples', 'gatehouse', 'Gatehouse.dwg'), path);
		await apply({ op: 'open', path });
		await apply({ op: 'zoom', items: 'all' });
		const camera = await script('return nw.Doc.CurrentViewpoint.ToViewpoint().GetCamera();');
		for (let repeat = 0; repeat < 2; repeat++) {
			await apply({ op: 'section', box: 'all', pad: 100 }, { op: 'viewpoint.save', name: 'Cut' }, { op: 'section', off: true }, { op: 'viewpoint.show', name: 'Cut' }, { op: 'section', off: true });
			assert.equal(await script('return nw.Doc.CurrentViewpoint.ToViewpoint().GetCamera();'), camera);
			const image = nw.path(`gate-${repeat}.png`);
			await nw.call('export', { out: [{ what: 'image', path: image, w: 640, h: 400 }] });
			const green = await script(`using(var b=new System.Drawing.Bitmap(${JSON.stringify(image)})){int green=0;for(int y=0;y<b.Height;y++)for(int x=0;x<b.Width;x++){var p=b.GetPixel(x,y);if(p.G>p.R+20&&p.G>p.B+20)green++;}return green;}`);
			assert.ok(Number(green.replaceAll(',', '')) > 1000, `the green roof is visible (${green} pixels)`);
		}
	});
});


test('F3 noninteractive document and export errors', async (t) => {
	const apply = (...ops: Record<string, unknown>[]) => nw.call('apply', { ops });
	const script = async (code: string) => (await nw.call('script', { code })).replace(/\nundo:.*$/, '');
	await apply({op:'open',path:nw.fixture});
	await apply({op:'clash.add',name:'F3',a:'?Item.Type = IfcDuctSegment',b:'?Item.Type = IfcWall'});
	await apply({op:'clash.run',test:'F3'});
	await t.test('B3 B8 every export rejects bad destinations before native output and preserves existing bytes', async () => {
		const outputs = [
			{ what: 'image', ext: 'png', w: 32, h: 32 }, { what: 'csv', ext: 'csv', items: 'all' },
			{ what: 'nwd', ext: 'nwd' }, { what: 'nwf', ext: 'nwf' },
			{ what: 'clash_report', ext: 'html', test: 'F3', images: false }, { what: 'tasks_csv', ext: 'csv' },
		];
		for (let repeat = 0; repeat < 2; repeat++) for (const { ext, ...out } of outputs) {
			const wrong = await nw.callRaw('export', { out: [{ ...out, path: nw.path('wrong.txt') }] });
			assert.equal(wrong.isError, true, wrong.text);
			assert.match(wrong.text, /error bad_args:.*(?:extension|end in)/);
			const path = nw.path(`${out.what}-${repeat}.${ext}`);
			writeFileSync(path, 'preserve these bytes');
			for (const condition of ['readonly', 'locked', 'long', 'directory', 'denied', 'reserved', 'invalid', 'relative']) {
				let target = path;
				if (condition === 'readonly') await script(`System.IO.File.SetAttributes(${JSON.stringify(path)}, System.IO.FileAttributes.ReadOnly); return null;`);
				if (condition === 'locked') await script(`AppDomain.CurrentDomain.SetData("F3lock",new System.IO.FileStream(${JSON.stringify(path)},System.IO.FileMode.Open,System.IO.FileAccess.ReadWrite,System.IO.FileShare.None)); return null;`);
				if (condition === 'long') target = nw.path('x'.repeat(240) + '.' + ext);
				if (condition === 'reserved') target = join(nw.dir, `CON.${ext}`);
				if (condition === 'invalid') target = nw.path(`bad<name>.${ext}`);
				if (condition === 'relative') target = `C:relative.${ext}`;
				if (condition === 'directory') { target = nw.path(`directory-${out.what}-${repeat}.${ext}`); mkdirSync(target); }
				const denied = nw.path(`denied-${out.what}-${repeat}`);
				if (condition === 'denied') {
					mkdirSync(denied); target = join(denied, `out.${ext}`);
					await script(`var d=new System.IO.DirectoryInfo(${JSON.stringify(denied)});var acl=d.GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);AppDomain.CurrentDomain.SetData("F3acl",acl.GetSecurityDescriptorBinaryForm());acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User,System.Security.AccessControl.FileSystemRights.Write,System.Security.AccessControl.InheritanceFlags.ContainerInherit|System.Security.AccessControl.InheritanceFlags.ObjectInherit,System.Security.AccessControl.PropagationFlags.None,System.Security.AccessControl.AccessControlType.Deny));d.SetAccessControl(acl);return null;`);
				}
				try {
					const result = await nw.callRaw('export', { out: [{ ...out, path: target }] });
					assert.equal(result.isError, true, result.text);
					assert.match(result.text, /error (?:bad_args|not_found):/);
					assert.doesNotMatch(result.text, /next: (?:job|.*help)/);
				} finally {
					if (condition === 'readonly') await script(`System.IO.File.SetAttributes(${JSON.stringify(path)}, System.IO.FileAttributes.Normal); return null;`);
					if (condition === 'locked') await script('((IDisposable)AppDomain.CurrentDomain.GetData("F3lock")).Dispose(); AppDomain.CurrentDomain.SetData("F3lock",null); return null;');
					if (condition === 'denied') await script(`var acl=new System.Security.AccessControl.DirectorySecurity();acl.SetSecurityDescriptorBinaryForm((byte[])AppDomain.CurrentDomain.GetData("F3acl"),System.Security.AccessControl.AccessControlSections.Access);new System.IO.DirectoryInfo(${JSON.stringify(denied)}).SetAccessControl(acl);AppDomain.CurrentDomain.SetData("F3acl",null);return null;`);
				}
				assert.equal(readFileSync(path, 'utf8'), 'preserve these bytes');
				assert.match(await nw.call('status'), /doc /);
			}
		}
	});
	await t.test('C2 2D federation rejects append/merge without a modal and remains usable', async () => {
		const dest = join(nw.dir, 'hospital');
		cpSync(join(navisworks().dir, 'Samples', 'Quantification'), dest, { recursive: true });
		for (let repeat = 0; repeat < 2; repeat++) {
			await apply({ op: 'open', path: join(dest, 'Autodesk_Hospital_Sample.nwf') });
			assert.equal(await script('return nw.Doc.ActiveSheetType;'), 'Sheet2D');
			for (const op of ['append', 'merge']) for (const preview of [true, false]) {
				const result = await nw.callRaw('apply', { ops: [{ op, paths: [join(dest, 'Autodesk_Hospital_Architectural.nwc')] }], preview });
				assert.equal(result.isError, true, result.text);
				assert.match(result.text, /bad_args:.*2D sheet/);
				assert.match(await nw.call('status'), /doc /);
			}
			await apply({ op: 'open', path: nw.fixture });
			assert.match(await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall' }), /W-South/);
		}
	});
	await t.test('C2 corrupt, unsupported and missing-reference inputs return errors without blocking subsequent calls', async () => {
		const corrupt = nw.path('corrupt.nwd');
		const unsupported = nw.path('unsupported.xyz');
		writeFileSync(corrupt, 'not a Navisworks file');
		writeFileSync(unsupported, 'no reader for this format');
		const reference = nw.path('removed-reference.ifc');
		const federation = nw.path('missing-reference.nwf');
		copyFileSync(nw.fixture, reference);
		await apply({ op: 'open', path: reference });
		await nw.call('export', { out: [{ what: 'nwf', path: federation }] });
		await apply({ op: 'open', path: nw.fixture });
		unlinkSync(reference);
		const cache = reference.replace(/\.ifc$/, '.nwc');
		if (existsSync(cache)) unlinkSync(cache);
		for (let repeat = 0; repeat < 2; repeat++) for (const path of [corrupt, unsupported, federation]) for (const op of ['open', 'append', 'merge']) {
			const result = await nw.callRaw('apply', { ops: [op === 'open' ? { op, path } : { op, paths: [path] }] });
			assert.equal(result.isError, true, result.text);
			assert.match(result.text, /error (?:bad_args|not_found):/);
			assert.match(result.text, /next: /);
			assert.doesNotMatch(result.text, /next: job /);
			await apply({ op: 'open', path: nw.fixture });
			assert.match(await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall' }), /W-South/);
		}
	});
});
