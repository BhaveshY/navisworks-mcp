import assert from 'node:assert/strict';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect } from 'node:net';
import { after, before, test } from 'node:test';
import { boxes } from './fixture/scene.ts';
import { startSession, type Session } from './navisworks.ts';

let nw: Session;
before(async () => (nw = await startSession()));
after(() => nw?.close());

const count = (pred: (b: (typeof boxes)[number]) => boolean) => boxes.filter(pred).length;
const total = (text: string) => Number(/items ([\d,]+) \(exact\)/.exec(text)?.[1]?.replace(/,/g, ''));
const handle = (text: string) => /^(r\d+) /.exec(text)![1]!;

test('status names the open document, its unit and its model', async () => {
	const text = await nw.call('status');
	assert.match(text, /fixture\.ifc/);
	assert.match(text, /units mm/);
});

test('a where search returns exactly the matching elements', async () => {
	const walls = await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall' });
	assert.equal(total(walls), count((b) => b.ifcClass === 'IfcWall'));
	for (const b of boxes.filter((b) => b.ifcClass === 'IfcWall')) assert.match(walls, new RegExp(`\\|${b.name}\\|`));

	const f90 = await nw.call('read', { what: 'items', where: 'Pset_Fixture.FireRating = F90' });
	assert.equal(total(f90), count((b) => b.props.FireRating === 'F90'));
});

test('or starts a new group and and narrows within it', async () => {
	const text = await nw.call('read', { what: 'items', where: 'Pset_Fixture.FireRating = F90 or Pset_Fixture.System = Supply and Item.Name ~ 2' });
	assert.equal(total(text), count((b) => b.props.FireRating === 'F90' || (b.props.System === 'Supply' && b.name.includes('2'))));
});

test('typed comparisons use the property type', async () => {
	const external = await nw.call('read', { what: 'items', where: 'Pset_Fixture.IsExternal = true' });
	assert.equal(total(external), count((b) => b.props.IsExternal === true));
});

/// Runs the `more:` line exactly as an agent would copy it.
const follow = (text: string) => {
	const call = /^more: read (\{.*\})$/m.exec(text)?.[1];
	assert.ok(call, `no more: line in\n${text}`);
	return nw.call('read', JSON.parse(call.replace(/(\w+):/g, '"$1":')));
};
const names = (text: string) => text.split('\n').filter((l) => /^\d+\|/.test(l)).map((l) => l.split('|')[2]!);

test('following the more: line verbatim walks every row exactly once', async () => {
	const expected = boxes.filter((b) => 'System' in b.props).map((b) => b.name).sort();
	let page = await nw.call('read', { what: 'items', where: 'has Pset_Fixture.System', limit: 3 });
	const r = handle(page);
	assert.equal(total(page), expected.length);
	const seen = names(page);
	while (/^more: /m.test(page)) {
		page = await follow(page);
		assert.equal(handle(page), r);
		seen.push(...names(page));
	}
	assert.deepEqual(seen.sort(), expected);
});

test('last pages the most recent handle', async () => {
	const walls = await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall' });
	const again = await nw.call('read', { what: 'items', in: 'last' });
	assert.equal(handle(again), handle(walls));
	assert.equal(total(again), count((b) => b.ifcClass === 'IfcWall'));
});

test('group and values count by property', async () => {
	const grouped = await nw.call('read', { what: 'items', where: 'has Pset_Fixture.System', group: 'Pset_Fixture.System', limit: 0 });
	assert.match(grouped, new RegExp(`Supply ${count((b) => b.props.System === 'Supply')}`));
	assert.match(grouped, new RegExp(`Return ${count((b) => b.props.System === 'Return')}`));
	const values = await nw.call('read', { what: 'values', prop: 'Pset_Fixture.FireRating' });
	assert.match(values, new RegExp(`F30\\|${count((b) => b.props.FireRating === 'F30')}`));
});

test('props shows the property tabs of an item', async () => {
	const walls = await nw.call('read', { what: 'items', where: 'Item.Name = W-South' });
	const text = await nw.call('read', { what: 'props', items: `${handle(walls)}#0` });
	assert.match(text, /\[Pset_Fixture\].*FireRating=F90/);
});

test('an unknown property fails with a next step instead of an empty list', async () => {
	const r = await nw.callRaw('read', { what: 'items', where: 'Pset_Nope.Thing = 1' });
	assert.equal(r.isError, true);
	assert.match(r.text, /error not_found: .*Pset_Nope\.Thing[\s\S]*next: /);
});

test('bad arguments name the op usage', async () => {
	const r = await nw.callRaw('apply', { ops: [{ op: 'color', items: 'r1' }] });
	assert.equal(r.isError, true);
	assert.match(r.text, /error bad_args: op 0: color needs "rgb"[\s\S]*next: usage: color items:target rgb:color/);
});

test('apply batches overrides into one undo step, and undo reverts them', async () => {
	const walls = handle(await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall' }));
	const preview = await nw.call('apply', { preview: true, ops: [{ op: 'color', items: walls, rgb: '#ff0000' }, { op: 'transparency', items: walls, pct: 50 }] });
	assert.match(preview, /nothing changed/);
	const before = await nw.call('read', { what: 'items', in: walls, cols: 'color' });
	assert.doesNotMatch(before, /#ff0000/);

	const applied = await nw.call('apply', { ops: [{ op: 'color', items: walls, rgb: '#ff0000' }, { op: 'transparency', items: walls, pct: 50 }] });
	assert.match(applied, /undo: apply/);
	const after = await nw.call('read', { what: 'items', in: walls, cols: 'color,transparency' });
	assert.equal(after.split('\n').filter((l) => l.endsWith('|#ff0000|50')).length, count((b) => b.ifcClass === 'IfcWall'));

	await nw.call('apply', { ops: [{ op: 'undo' }] });
	const undone = await nw.call('read', { what: 'items', in: walls, cols: 'color,transparency' });
	assert.doesNotMatch(undone, /#ff0000/);
});

test('isolate hides everything else; reset shows it again', async () => {
	await nw.call('apply', { ops: [{ op: 'isolate', items: '?Item.Type = IfcSlab' }] });
	const ducts = await nw.call('read', { what: 'items', where: 'Item.Type = IfcDuctSegment', cols: 'hidden' });
	assert.equal(ducts.split('\n').filter((l) => l.endsWith('|true')).length, count((b) => b.ifcClass === 'IfcDuctSegment'));
	await nw.call('apply', { ops: [{ op: 'reset' }] });
	const shown = await nw.call('read', { what: 'items', where: 'Item.Type = IfcDuctSegment', cols: 'hidden' });
	assert.doesNotMatch(shown, /\|true$/m);
});

test('a search set saved from a handle stays live and works as a target', async () => {
	const ducts = handle(await nw.call('read', { what: 'items', where: 'Pset_Fixture.System = Supply' }));
	const saved = await nw.call('apply', { ops: [{ op: 'set.save', name: 'QA/Supply ducts', items: ducts }] });
	assert.match(saved, new RegExp(`saved search set "QA/Supply ducts" \\(${count((b) => b.props.System === 'Supply')} items now\\)`));
	const again = await nw.call('apply', { ops: [{ op: 'set.save', name: 'QA/Supply ducts', items: ducts }] });
	assert.match(again, /replaced search set/);
	const sets = await nw.call('read', { what: 'sets' });
	assert.match(sets, /QA\/Supply ducts\|search\|live/);
	assert.equal(sets.match(/QA\/Supply ducts\|/g)?.length, 1);
	const viaSet = await nw.call('read', { what: 'items', in: 'set:QA/Supply ducts' });
	assert.equal(total(viaSet), count((b) => b.props.System === 'Supply'));
});

test('export image writes a PNG of the requested size and can return it inline', async () => {
	const r = await nw.callRaw('export', { out: [{ what: 'image', w: 320, h: 200, inline: true }] });
	assert.equal(r.isError, false, r.text);
	const path = /^image (.+\.png) 320x200$/m.exec(r.text)![1]!;
	const png = readFileSync(path);
	assert.equal(png.subarray(1, 4).toString(), 'PNG');
	assert.equal(png.readUInt32BE(16), 320);
	assert.equal(png.readUInt32BE(20), 200);
	assert.equal(r.images, 1);
});

test('script runs C# against the API and its handles feed other tools', async () => {
	const tall = boxes.filter((b) => b.max[2] - b.min[2] >= 2995);
	const text = await nw.call('script', { code: 'return nw.Handle(nw.Items("?has Pset_Fixture.FireRating or has Pset_Fixture.System or has Pset_Fixture.LoadBearing").Where(i => nw.Mm(i.BoundingBox().Size.Z) >= 2995));' });
	const r = /^(r\d+)$/m.exec(text)![1]!;
	assert.equal(total(await nw.call('read', { what: 'items', in: r })), tall.length);
	const bad = await nw.callRaw('script', { code: 'return undefinedThing;' });
	assert.equal(bad.isError, true);
	assert.match(bad.text, /error script: compile failed:\nline 1: .*undefinedThing/);
});

test('every row id resolves back to its own item through an id: target', async () => {
	const walls = await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall' });
	const rows = walls.split('\n').filter((l) => /^\d+\|/.test(l)).map((l) => l.split('|'));
	assert.equal(rows.length, count((b) => b.ifcClass === 'IfcWall'));
	for (const [, id, name] of rows) {
		assert.match(id!, /^\d+:\d+(\/\d+)*$/);
		const back = await nw.call('read', { what: 'items', in: `id:${id}` });
		assert.equal(total(back), 1);
		assert.match(back, new RegExp(`\|${id}\|${name}\|`));
	}
});

test('a failed batch undoes its own earlier ops even when an identical batch succeeded before it', async () => {
	const walls = handle(await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall' }));
	const colors = async () => (await nw.call('read', { what: 'items', in: walls, cols: 'color' })).split('\n').filter((l) => /^\d+\|/.test(l)).map((l) => l.split('|').at(-1));
	await nw.call('apply', { ops: [{ op: 'color', items: walls, rgb: '#00ff00' }, { op: 'transparency', items: walls, pct: 10 }] });
	const failed = await nw.callRaw('apply', { ops: [{ op: 'color', items: walls, rgb: '#0000ff' }, { op: 'transparency', items: walls, pct: 101 }] });
	assert.equal(failed.isError, true);
	assert.match(failed.text, /op 1 \(transparency\) failed: .*every change in this call was undone/);
	assert.deepEqual([...new Set(await colors())], ['#00ff00']);
	await nw.call('apply', { ops: [{ op: 'reset' }] });
});

test('set.save with an unknown property fails before it creates anything', async () => {
	const r = await nw.callRaw('apply', { ops: [{ op: 'set.save', name: 'QA/Broken', where: 'Pset_Nope.Thing = 1' }] });
	assert.equal(r.isError, true);
	assert.match(r.text, /nothing changed/);
	assert.doesNotMatch(await nw.call('read', { what: 'sets' }), /QA\/Broken/);
});

test('saving a handle searched inside a scope keeps exactly its items', async () => {
	const columns = handle(await nw.call('read', { what: 'items', where: 'Item.Type = IfcColumn' }));
	const scoped = handle(await nw.call('read', { what: 'items', in: columns, where: 'Pset_Fixture.LoadBearing = true' }));
	const saved = await nw.call('apply', { ops: [{ op: 'set.save', name: 'QA/Load-bearing columns', items: scoped }] });
	assert.match(saved, new RegExp(`selection set "QA/Load-bearing columns" \\(${count((b) => b.ifcClass === 'IfcColumn' && b.props.LoadBearing === true)} items now\\)`));
});

test('values keeps numbers apart that only round alike', async () => {
	await nw.call('apply', { ops: [
		{ op: 'props.set', items: '?Item.Name = W-South', tab: 'QA', props: { Width: 100.1 } },
		{ op: 'props.set', items: '?Item.Name = W-North', tab: 'QA', props: { Width: 100.4 } },
		{ op: 'props.set', items: '?Item.Name = W-East', tab: 'QA', props: { Width: 100.4 } },
	] });
	const text = await nw.call('read', { what: 'values', prop: 'QA.Width' });
	assert.match(text, /values QA\.Width: 2 distinct in 3 items/);
	assert.equal(text.split('\n').filter((l) => /\|[12]$/.test(l)).length, 2);
});

test('preview plans what it can and does not fail on ops that depend on earlier ones', async () => {
	const r = await nw.call('apply', { preview: true, ops: [{ op: 'set.save', name: 'QA/New', where: 'Item.Type = IfcSlab' }, { op: 'select', items: 'set:QA/New' }] });
	assert.match(r, /^0 would save search set \(live\) "QA\/New"$/m);
	assert.match(r, /^1 select: cannot plan before earlier ops apply/m);
	assert.doesNotMatch(await nw.call('read', { what: 'sets' }), /QA\/New\|/);
});

test('handles from before a file was opened are refused, not applied to the new model', async () => {
	const walls = handle(await nw.call('read', { what: 'items', where: 'Item.Type = IfcWall' }));
	await nw.call('apply', { ops: [{ op: 'open', path: nw.fixture }] });
	const r = await nw.callRaw('apply', { ops: [{ op: 'hide', items: walls }] });
	assert.equal(r.isError, true);
	assert.match(r.text, /error stale_handle: .*models changed[\s\S]*next: repeat the read/);
});

test('status can re-pin the same instance repeatedly without losing the connection', async () => {
	for (let i = 0; i < 3; i++) assert.match(await nw.call('status', { pid: nw.pid }), new RegExp(`pid ${nw.pid}`));
});

test('garbage on the pipe is dropped without taking Navisworks down', async () => {
	const pipe = connect(String.raw`\\.\pipe\navisworks-mcp-` + nw.pid);
	await once(pipe, 'connect');
	pipe.resume();
	for (const line of ['not json', 'null', '[]', '{"id":"x","tool":"status"}', '{"id":1}', '{"id":2,"tool":7}', '{"cancel":"y"}']) pipe.write(line + '\n');
	pipe.end();
	await once(pipe, 'close');
	assert.match(await nw.call('status'), new RegExp(`pid ${nw.pid}`));
});

test('quantities keep their decimals', async () => {
	await nw.call('apply', { ops: [{ op: 'props.set', items: '?Item.Name = W-West', tab: 'QA', props: { Area: 1840.25, Big: 3000000000 } }] });
	const text = await nw.call('read', { what: 'props', items: '?Item.Name = W-West', tabs: 'QA' });
	assert.match(text, /Area=1,840\.25/);
	assert.match(text, /Big=3,000,000,000/);
});
