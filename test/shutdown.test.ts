import assert from 'node:assert/strict';
import { copyFileSync, existsSync, readdirSync } from 'node:fs';
import { basename, join } from 'node:path';
import { test } from 'node:test';
import { navisworks } from '../scripts/plugin.ts';
import { boxes, overlaps } from './fixture/scene.ts';
import { startSession } from './navisworks.ts';

test('a clash session creates no recovery file and exits after saving, reopening and appending a model', async () => {
	const nw = await startSession();
	try {
		const ducts = boxes.filter((b) => b.ifcClass === 'IfcDuctSegment');
		const walls = boxes.filter((b) => b.ifcClass === 'IfcWall');
		const expected = ducts.flatMap((d) => walls.filter((w) => overlaps(d, w))).length;
		const result = await nw.call('apply', { ops: [
			{ op: 'clash.add', name: 'Shutdown', a: '?Item.Type = IfcDuctSegment', b: '?Item.Type = IfcWall' },
			{ op: 'clash.run', test: 'Shutdown' },
		] });
		assert.match(result, new RegExp(`ran "Shutdown": ${expected} results`));
		// Exercise the native request, rather than merely asserting the options XML we wrote.
		await nw.call('script', { code: 'return Autodesk.Navisworks.Api.Interop.LcOpAutoSave.RequestAutoSave();' });
		const autosaves = join(process.env.APPDATA!, 'Autodesk', `Navisworks Manage ${navisworks().year}`, 'AutoSave');
		const prefix = basename(nw.fixture).split('-')[0]! + '-';
		assert.deepEqual((existsSync(autosaves) ? readdirSync(autosaves) : []).filter((f) => f.startsWith(prefix)), []);
		const path = nw.path('shutdown.nwd').replaceAll('\\', '/');
		await nw.call('script', { code: `nw.Doc.SaveFile("${path}"); nw.Doc.OpenFile("${path}"); return nw.Doc.FileName;` });
		assert.match(await nw.call('read', { what: 'tests' }), new RegExp(`Shutdown\\|hard\\|0\\|complete\\|[^\\n]*\\|${expected}\\|`));
		// IFC conversion leaves a separate progress window around; closing it is not closing Navisworks.
		const copy = nw.path('shutdown-copy.ifc');
		copyFileSync(nw.fixture, copy);
		assert.match(await nw.call('apply', { ops: [{ op: 'append', paths: [copy] }] }), /now 2 model\(s\)/);
		const wallCount = boxes.filter((b) => b.ifcClass === 'IfcWall').length;
		assert.match(await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall' }), new RegExp(`items ${wallCount * 2} \\(exact\\)`));
	} finally {
		await nw.close();
	}
	assert.throws(() => process.kill(nw.pid, 0), { code: 'ESRCH' });
});

test('a test session autosaves nothing, whatever the open document is called', async () => {
	const nw = await startSession();
	const autosaves = join(process.env.APPDATA!, 'Autodesk', `Navisworks Manage ${navisworks().year}`, 'AutoSave');
	// No session prefix: the harness cannot recognise an autosave of this document as a test file, so none may be written.
	const stem = `unprefixed${process.pid}${Date.now()}`;
	const model = join(nw.dir, `${stem}.ifc`);
	try {
		copyFileSync(nw.fixture, model);
		await nw.call('apply', { ops: [{ op: 'open', path: model }] });
		await nw.call('apply', { ops: [{ op: 'color', items: '?Item.Type = IfcWall', rgb: '#ff0000' }] });
		await nw.call('script', { code: 'return Autodesk.Navisworks.Api.Interop.LcOpAutoSave.RequestAutoSave();' });
		assert.deepEqual((existsSync(autosaves) ? readdirSync(autosaves) : []).filter((f) => f.startsWith(stem)), []);
	} finally {
		await nw.close();
	}
});
