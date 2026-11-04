import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { after, before, test } from 'node:test';
import { inflateSync } from 'node:zlib';
import { boxes } from './fixture/scene.ts';
import { startSession, type Session } from './navisworks.ts';

let nw: Session;
before(async () => {
	nw = await startSession();
	// The Construct green is 90% transparent, so the background shows through it. While simulating, 2024 darkens a gradient background and
	// 2027 keeps the document's own, so both render over the colour 2024 simulates behind the walls; neither darkens a plain background.
	await nw.call('script', { code: 'nw.Doc.SetPlainBackground(Color.FromByteRGB(20, 24, 30)); return "background pinned";' });
});
after(() => nw?.close());

const count = (pred: (b: (typeof boxes)[number]) => boolean) => boxes.filter(pred).length;
const total = (text: string) => Number(/tasks ([\d,]+) \(exact\)/.exec(text)?.[1]?.replace(/,/g, ''));
const handle = (text: string) => /^(r[\w-]+) /.exec(text)![1]!;

/// The table row whose task column (trimmed) is `name`, as cells keyed by header.
function row(text: string, name: string): Record<string, string> {
	const lines = text.split('\n');
	const header = lines.find((l) => l.startsWith('#|'))!.split('|');
	const cells = lines.map((l) => l.split('|')).find((c) => c.length === header.length && c[2]?.trim() === name);
	assert.ok(cells, `no row for task ${name} in\n${text}`);
	return Object.fromEntries(header.map((h, i) => [h, cells[i]!]));
}

/// Pixels whose green clearly dominates, which is how TimeLiner's Construct start appearance (green) renders.
function greenPixels(path: string): number {
	const png = readFileSync(path);
	const width = png.readUInt32BE(16);
	const height = png.readUInt32BE(20);
	const colorType = png[25]!;
	const channels = colorType === 6 ? 4 : colorType === 2 ? 3 : 0;
	assert.ok(channels > 0 && png[24] === 8, `unsupported PNG (color type ${colorType}, depth ${png[24]})`);
	const idat: Buffer[] = [];
	for (let at = 8; at < png.length; ) {
		const len = png.readUInt32BE(at);
		const type = png.toString('ascii', at + 4, at + 8);
		if (type === 'IDAT') idat.push(png.subarray(at + 8, at + 8 + len));
		at += 12 + len;
	}
	const raw = inflateSync(Buffer.concat(idat));
	const stride = width * channels;
	const pixels = Buffer.alloc(stride * height);
	for (let y = 0; y < height; y++) {
		const filter = raw[y * (stride + 1)]!;
		for (let x = 0; x < stride; x++) {
			const v = raw[y * (stride + 1) + 1 + x]!;
			const a = x >= channels ? pixels[y * stride + x - channels]! : 0;
			const b = y > 0 ? pixels[(y - 1) * stride + x]! : 0;
			const c = x >= channels && y > 0 ? pixels[(y - 1) * stride + x - channels]! : 0;
			const p = a + b - c;
			const paeth = Math.abs(p - a) <= Math.abs(p - b) && Math.abs(p - a) <= Math.abs(p - c) ? a : Math.abs(p - b) <= Math.abs(p - c) ? b : c;
			pixels[y * stride + x] = (v + [0, a, b, (a + b) >> 1, paeth][filter]!) & 255;
		}
	}
	let green = 0;
	for (let i = 0; i < pixels.length; i += channels) if (pixels[i + 1]! > pixels[i]! + 40 && pixels[i + 1]! > pixels[i + 2]! + 40) green++;
	return green;
}

const walls = count((b) => b.ifcClass === 'IfcWall');
const columns = count((b) => b.ifcClass === 'IfcColumn');
const slabs = count((b) => b.ifcClass === 'IfcSlab');

test('tasks added under a parent read back with hierarchy, document task types and attached items', async () => {
	const added = await nw.call('apply', {
		ops: [
			{ op: 'set.save', name: '4D/Walls', where: 'Item.Type = IfcWall' },
			{ op: 'tl.add', name: 'Level 1', start: '2026-03-01', end: '2026-03-02' },
			{ op: 'tl.add', name: 'Walls', parent: 'Level 1', start: '2026-03-01', end: '2026-03-10', items: 'set:4D/Walls' },
			{ op: 'tl.add', name: 'Columns', parent: 'Level 1', start: '2026-03-05', end: '2026-03-12', items: '?Item.Type = IfcColumn' },
			{ op: 'tl.add', name: 'Slab', start: '2026-03-11', end: '2026-03-20', task_type: 'construct' },
		],
	});
	assert.match(added, /undo: apply/);
	const tasks = await nw.call('read', { what: 'tasks' });
	assert.equal(total(tasks), 4);
	assert.match(tasks, /types: Construct, Demolish, Temporary/);
	const level = row(tasks, 'Level 1');
	assert.equal(level.start, '2026-03-01');
	assert.equal(level.end, '2026-03-12', 'a parent spans its subtasks');
	assert.deepEqual([row(tasks, 'Walls').id, row(tasks, 'Walls').items, row(tasks, 'Walls').attached], ['1.1', String(walls), 'set:4D/Walls']);
	assert.deepEqual([row(tasks, 'Columns').items, row(tasks, 'Columns').attached], [String(columns), 'explicit']);
	assert.equal(row(tasks, 'Slab').type, 'Construct');

	const bad = await nw.callRaw('apply', { ops: [{ op: 'tl.add', name: 'X', start: '2026-03-01', end: '2026-03-02', task_type: 'Build' }] });
	assert.equal(bad.isError, true);
	assert.match(bad.text, /no task type "Build"[\s\S]*next: use one of: Construct, Demolish, Temporary/);
	const backwards = await nw.callRaw('apply', { ops: [{ op: 'tl.add', name: 'X', start: '2026-03-05', end: '2026-03-01' }] });
	assert.match(backwards.text, /end 2026-03-01 is before start 2026-03-05/);
});

test('match name=set attaches each task to the set with its name', async () => {
	await nw.call('apply', { ops: [{ op: 'set.save', name: '4D/Slab', where: 'Item.Type = IfcSlab' }, { op: 'tl.attach', tasks: 'Slab', match: 'name=set' }] });
	const slab = row(await nw.call('read', { what: 'tasks' }), 'Slab');
	assert.deepEqual([slab.items, slab.attached], [String(slabs), 'set:4D/Slab']);
});

test('where filters tasks; update and delete change them as one undo step', async () => {
	const later = await nw.call('read', { what: 'tasks', where: 'start >= 2026-03-05 and type = Construct' });
	assert.equal(total(later), 2);
	assert.match(later, /Columns/);
	assert.match(later, /Slab/);

	const walls = handle(await nw.call('read', { what: 'tasks', where: 'name = Walls' }));
	await nw.call('apply', { ops: [{ op: 'tl.update', tasks: `${walls}#0`, name: 'Walls L1', end: '2026-03-14' }, { op: 'tl.delete', tasks: 'Level 1/Columns' }] });
	const changed = await nw.call('read', { what: 'tasks' });
	assert.equal(total(changed), 3);
	assert.equal(row(changed, 'Walls L1').end, '2026-03-14');
	assert.equal(row(changed, 'Level 1').end, '2026-03-14', 'the parent follows its subtasks');

	await nw.call('apply', { ops: [{ op: 'undo' }] });
	const undone = await nw.call('read', { what: 'tasks' });
	assert.equal(total(undone), 4);
	assert.equal(row(undone, 'Walls').end, '2026-03-10');

	const gone = await nw.callRaw('apply', { ops: [{ op: 'tl.update', tasks: 'Level 1/Nope', end: '2026-03-14' }] });
	assert.match(gone.text, /no task "Level 1\/Nope"[\s\S]*next: list tasks/);
});

test('simulate drives TimeLiner to a date: active tasks and the rendered view follow the schedule', async () => {
	const early = await nw.call('apply', { ops: [{ op: 'tl.simulate', at: '2026-03-06' }] });
	assert.match(early, /simulation at 2026-03-06; active: .*Walls.*Columns/);
	const tasks = await nw.call('read', { what: 'tasks' });
	assert.match(tasks, /simulation at 2026-03-06/);
	assert.deepEqual(['Walls', 'Columns', 'Slab'].map((t) => row(tasks, t).active), ['yes', 'yes', 'no']);
	const earlyImage = /^image (.+\.png)/m.exec(await nw.call('export', { out: [{ what: 'image', w: 400, h: 250 }] }))![1]!;

	await nw.call('apply', { ops: [{ op: 'tl.simulate', at: '2026-03-15' }] });
	const late = await nw.call('read', { what: 'tasks' });
	assert.deepEqual(['Walls', 'Columns', 'Slab'].map((t) => row(late, t).active), ['no', 'no', 'yes']);
	const lateImage = /^image (.+\.png)/m.exec(await nw.call('export', { out: [{ what: 'image', w: 400, h: 250 }] }))![1]!;

	const building = greenPixels(earlyImage);
	const built = greenPixels(lateImage);
	assert.ok(building > 2000, `walls under construction render in the Construct green (${building} green pixels)`);
	assert.ok(built * 5 < building, `finished walls lose the green (${built} vs ${building} green pixels)`);

	const clamped = await nw.call('apply', { ops: [{ op: 'tl.simulate', at: '2027-01-01' }] });
	assert.match(clamped, /simulation at 2026-03-20 \(clamped to the schedule 2026-03-01\.\.2026-03-20\)/);
	await nw.call('apply', { ops: [{ op: 'tl.simulate', stop: true }] });
	assert.doesNotMatch(await nw.call('read', { what: 'tasks' }), /simulation at|\|active/);
});

test('import creates tasks from a mapped CSV, and the exported CSV imports back', async () => {
	const before = total(await nw.call('read', { what: 'tasks' }));
	const csv = nw.path('schedule.csv');
	writeFileSync(csv, 'Task;Begin;Finish;Kind;Under;Sets\nLevel 2;01.04.2026;02.04.2026;;;\nSlab L2;01.04.2026;10.04.2026;Construct;Level 2;4D/Slab\n"Shoring; temp";03.04.2026;08.04.2026;Temporary;Level 2;\n');
	const map = { name: 'Task', start: 'Begin', end: 'Finish', type: 'Kind', parent: 'Under', set: 'Sets', format: 'dd.MM.yyyy' };

	const broken = nw.path('broken.csv');
	writeFileSync(broken, 'Task;Begin;Finish\nA;01.04.2026;soon\n');
	const bad = await nw.callRaw('apply', { ops: [{ op: 'tl.import', csv: broken, map: { name: 'Task', start: 'Begin', end: 'Finish', format: 'dd.MM.yyyy' } }] });
	assert.match(bad.text, /line 2: end "soon" is not a date[\s\S]*nothing was imported/);
	assert.equal(total(await nw.call('read', { what: 'tasks' })), before);

	assert.match(await nw.call('apply', { ops: [{ op: 'tl.import', csv, map }] }), /imported 3 tasks from [0-9a-f]{8}-schedule\.csv, 1 attached to sets/);
	const tasks = await nw.call('read', { what: 'tasks' });
	assert.equal(total(tasks), before + 3);
	assert.deepEqual([row(tasks, 'Slab L2').id, row(tasks, 'Slab L2').items, row(tasks, 'Slab L2').attached], ['3.1', String(slabs), 'set:4D/Slab']);
	assert.equal(row(tasks, 'Shoring; temp').type, 'Temporary');
	assert.equal(row(tasks, 'Level 2').end, '2026-04-10');

	const out = nw.path('tasks.csv');
	assert.match(await nw.call('export', { out: [{ what: 'tasks_csv', path: out }] }), new RegExp(`tasks_csv .*tasks\\.csv ${before + 3} tasks`));
	const lines = readFileSync(out, 'utf8').replace(/^﻿/, '').trim().split(/\r?\n/);
	assert.equal(lines[0], 'Id,Name,Parent,Type,Start,End,ActualStart,ActualEnd,Items,Set');
	assert.ok(lines.includes(`1.1,Walls,Level 1,Construct,2026-03-01,2026-03-10,,,${walls},4D/Walls`), lines.join('\n'));
	assert.ok(lines.includes('3.2,"Shoring; temp",Level 2,Temporary,2026-04-03,2026-04-08,,,0,'), lines.join('\n'));

	await nw.call('apply', { ops: [{ op: 'tl.import', csv: out, replace: true }] });
	const again = await nw.call('read', { what: 'tasks' });
	assert.equal(total(again), before + 3);
	assert.deepEqual([row(again, 'Walls').items, row(again, 'Walls').attached, row(again, 'Walls').id], [String(walls), 'set:4D/Walls', '1.1']);
});

test('import reports a set name shared by two sets instead of failing', async () => {
	await nw.call('apply', { ops: [
		{ op: 'set.save', name: 'A/Dup', where: 'Item.Type = IfcWall' },
		{ op: 'set.save', name: 'B/Dup', where: 'Item.Type = IfcSlab' },
	] });
	const csv = nw.path('dup.csv');
	writeFileSync(csv, 'Name,Start,End,Set\nDup task,2026-05-01,2026-05-02,Dup\n');
	const r = await nw.callRaw('apply', { ops: [{ op: 'tl.import', csv }] });
	assert.equal(r.isError, true);
	assert.match(r.text, /error bad_args: op 0 \(tl\.import\) failed: 1 problem\(s\) in [0-9a-f]{8}-dup\.csv: line 2: no single set "Dup"\. nothing changed\nnext: .*nothing was imported/);
});
