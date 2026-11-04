import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { after, before, test } from 'node:test';
import { boxes, overlaps, storeys, type Box } from './fixture/scene.ts';
import { startSession, type Session } from './navisworks.ts';

let nw: Session;
before(async () => (nw = await startSession()));
after(() => nw?.close());

const of = (cls: Box['ifcClass']) => boxes.filter((b) => b.ifcClass === cls);
const pairs = (a: Box[], b: Box[], hit: (x: Box, y: Box) => boolean) => a.flatMap((x) => b.filter((y) => hit(x, y)).map((y) => `${x.name}+${y.name}`)).sort();
const gap = (a: Box, b: Box) => Math.hypot(...a.min.map((_, i) => Math.max(0, a.min[i]! - b.max[i]!, b.min[i]! - a.max[i]!)));
const levelAt = (z: number) => storeys.filter((s) => s.elevation <= z).at(-1)!.name;

const hardDuctsWalls = pairs(of('IfcDuctSegment'), of('IfcWall'), overlaps);
const clearColumnsSlab = (tol: number) => pairs(of('IfcColumn'), of('IfcSlab'), (a, b) => gap(a, b) < tol);

const handle = (text: string) => /^(r\d+) clashes/m.exec(text)![1]!;
const total = (text: string) => Number(/clashes ([\d,]+) \(exact\)/.exec(text)?.[1]?.replace(/,/g, ''));
type Row = Record<'clash' | 'status' | 'dist' | 'item1' | 'id1' | 'item2' | 'id2' | 'level' | 'grid' | 'assigned' | 'centre' | 'group', string>;
const rows = (text: string): Row[] => {
	const lines = text.split('\n');
	const header = lines.find((l) => l.startsWith('#|'))!.split('|');
	return lines.filter((l) => /^\d+\|/.test(l)).map((l) => Object.fromEntries(l.split('|').map((v, i) => [header[i], v])) as Row);
};
const sideA = new Set([...of('IfcDuctSegment'), ...of('IfcColumn')].map((b) => b.name));
const pairOf = (r: Row) => (sideA.has(r.item1) ? `${r.item1}+${r.item2}` : `${r.item2}+${r.item1}`);
const testRow = async (name: string) => {
	const text = await nw.call('read', { what: 'tests' });
	const lines = text.split('\n');
	const header = lines.find((l) => l.startsWith('test|'))!.split('|');
	const line = lines.find((l) => l.startsWith(`${name}|`));
	return line ? (Object.fromEntries(line.split('|').map((v, i) => [header[i], v])) as Record<string, string>) : undefined;
};
const apply = (...ops: Record<string, unknown>[]) => nw.call('apply', { ops });

test('a new test references a set live and saves a where as a search set', async () => {
	assert.match(await nw.call('read', { what: 'tests' }), /^tests 0 \(exact\)$/);
	await apply({ op: 'set.save', name: 'MEP/Ducts', where: 'Item.Type = IfcDuctSegment' });
	const added = await apply({ op: 'clash.add', name: 'Ducts vs Walls', a: 'set:MEP/Ducts', b: '?Item.Type = IfcWall' });
	assert.match(added, /added clash test "Ducts vs Walls" \(hard, tol 0 mm\): a set:MEP\/Ducts · b search set "Clash\/Ducts vs Walls B"/);
	assert.match(await nw.call('read', { what: 'sets' }), /^Clash\/Ducts vs Walls B\|search\|live$/m);
	const row = (await testRow('Ducts vs Walls'))!;
	assert.equal(row.type, 'hard');
	assert.equal(row.status, 'new');
	assert.equal(row.results, '0');
	assert.equal(row.a, 'set:MEP/Ducts');
	assert.equal(row.b, 'set:Clash/Ducts vs Walls B');
});

test('running a hard test finds every duct passing through a wall, with negative distances', async () => {
	const ran = await apply({ op: 'clash.run', test: 'Ducts vs Walls' });
	assert.match(ran, new RegExp(`ran "Ducts vs Walls": ${hardDuctsWalls.length} results · new ${hardDuctsWalls.length}`));
	const text = await nw.call('read', { what: 'clashes', test: 'Ducts vs Walls' });
	assert.equal(total(text), hardDuctsWalls.length);
	const found = rows(text);
	assert.deepEqual(found.map(pairOf).sort(), hardDuctsWalls);
	for (const r of found) {
		assert.ok(Number(r.dist) < 0, `penetration is negative: ${r.dist}`);
		assert.equal(r.status, 'new');
		assert.equal(r.level, levelAt(Number(r.centre.split(',')[2])));
	}
	const row = (await testRow('Ducts vs Walls'))!;
	assert.equal(row.status, 'complete');
	assert.equal(row.results, String(hardDuctsWalls.length));
	assert.match(row['last run']!, /^\d{4}-\d\d-\d\d \d\d:\d\d$/);
});

test('where filters results by either item, and a clash handle targets both items', async () => {
	const supply = of('IfcDuctSegment').filter((b) => b.props.System === 'Supply').map((b) => b.name);
	const text = await nw.call('read', { what: 'clashes', test: 'Ducts vs Walls', where: 'Pset_Fixture.System = Supply' });
	assert.equal(total(text), hardDuctsWalls.filter((p) => supply.includes(p.split('+')[0]!)).length);
	const all = handle(await nw.call('read', { what: 'clashes', test: 'Ducts vs Walls' }));
	const items = await nw.call('read', { what: 'items', in: all });
	assert.match(items, new RegExp(`items ${new Set(hardDuctsWalls.flatMap((p) => p.split('+'))).size} \\(exact\\)`));
});

test('paging keeps the clash handle', async () => {
	const first = await nw.call('read', { what: 'clashes', test: 'Ducts vs Walls', limit: 4 });
	const r = handle(first);
	const more = JSON.parse(/^more: read (\{.*\})$/m.exec(first)![1]!.replace(/(\w+):/g, '"$1":'));
	assert.deepEqual(more, { what: 'clashes', in: r, page: 2, limit: 4 });
	const second = await nw.call('read', more);
	assert.equal(handle(second), r);
	assert.equal(rows(second).length, hardDuctsWalls.length - 4);
});

test('edit sets status, assignment and comments; approve and undo', async () => {
	const r = handle(await nw.call('read', { what: 'clashes', test: 'Ducts vs Walls' }));
	const edited = await apply({ op: 'clash.edit', items: `${r}#0-1`, status: 'active', assign: 'MEP Lead', comment: 'reroute below the beam' });
	assert.match(edited, /edited 2 results: status active · assigned MEP Lead · comment added/);
	const active = rows(await nw.call('read', { what: 'clashes', test: 'Ducts vs Walls', status: 'active' }));
	assert.equal(active.length, 2);
	for (const a of active) assert.equal(a.assigned, 'MEP Lead');

	await apply({ op: 'clash.edit', items: `${r}#2`, approve: true });
	const byStatus = await nw.call('read', { what: 'clashes', test: 'Ducts vs Walls', group: 'status', limit: 0 });
	assert.match(byStatus, new RegExp(`by status: new ${hardDuctsWalls.length - 3} · active 2 · approved 1`));
	const row = (await testRow('Ducts vs Walls'))!;
	assert.deepEqual([row.new, row.active, row.approved], [String(hardDuctsWalls.length - 3), '2', '1']);

	await apply({ op: 'undo' });
	assert.equal(total(await nw.call('read', { what: 'clashes', test: 'Ducts vs Walls', status: 'approved' })), 0);
});

test('native groups by a property of item2 persist in the test, keep status, and ungroup', async () => {
	const walls = hardDuctsWalls.map((p) => p.split('+')[1]!);
	const counts = [...new Set(walls)].sort().map((w) => `${w} ${walls.filter((x) => x === w).length}`);
	const grouped = await apply({ op: 'clash.group', test: 'Ducts vs Walls', by: 'item2.Item.Name' });
	assert.match(grouped, new RegExp(`grouped ${hardDuctsWalls.length} results of "Ducts vs Walls" into ${counts.length} groups by item2.Item.Name: ${counts.join(' · ')}`));
	assert.equal((await testRow('Ducts vs Walls'))!.groups, String(counts.length));
	const text = await nw.call('read', { what: 'clashes', test: 'Ducts vs Walls', group: 'group', limit: 0 });
	for (const c of counts) assert.match(text, new RegExp(c));
	assert.equal(total(await nw.call('read', { what: 'clashes', test: 'Ducts vs Walls', status: 'active' })), 2);

	const byLevel = await apply({ op: 'clash.group', test: 'Ducts vs Walls', by: 'level' });
	assert.match(byLevel, new RegExp(`into 1 groups by level: Level 1 ${hardDuctsWalls.length}`));
	assert.equal((await testRow('Ducts vs Walls'))!.groups, '1');

	const ungrouped = await apply({ op: 'clash.ungroup', test: 'Ducts vs Walls' });
	assert.match(ungrouped, new RegExp(`ungrouped ${hardDuctsWalls.length} results from 1 groups`));
	const row = (await testRow('Ducts vs Walls'))!;
	assert.deepEqual([row.groups, row.results], ['0', String(hardDuctsWalls.length)]);
});

test('clearance finds the columns 10 mm below the slab; hard does not', async () => {
	const slab = await nw.call('read', { what: 'items', where: 'Item.Type = IfcSlab' });
	const slabHandle = /^(r\d+) items/.exec(slab)![1]!;
	await apply({ op: 'clash.add', name: 'Columns vs Slab', a: '?Item.Type = IfcColumn', b: slabHandle, type: 'clearance', tol: 20 });
	assert.equal((await testRow('Columns vs Slab'))!.b, 'set:Clash/Columns vs Slab B');
	assert.match(await apply({ op: 'clash.run', test: 'Columns vs Slab' }), new RegExp(`ran "Columns vs Slab": ${clearColumnsSlab(20).length} results`));
	const found = rows(await nw.call('read', { what: 'clashes', test: 'Columns vs Slab' }));
	assert.deepEqual(found.map(pairOf).sort(), clearColumnsSlab(20));
	const expectedGap = gap(of('IfcColumn')[0]!, of('IfcSlab')[0]!);
	for (const r of found) assert.equal(Math.round(Number(r.dist)), expectedGap);

	await apply({ op: 'clash.update', test: 'Columns vs Slab', type: 'hard', tol: 0, to: 'Columns vs Slab hard' });
	assert.equal((await testRow('Columns vs Slab hard'))!.type, 'hard');
	assert.match(await apply({ op: 'clash.run', test: 'Columns vs Slab hard' }), new RegExp(`ran "Columns vs Slab hard": ${pairs(of('IfcColumn'), of('IfcSlab'), overlaps).length} results`));
});

test('run all runs every test', async () => {
	const ran = await apply({ op: 'clash.run', test: 'all' });
	assert.match(ran, new RegExp(`ran "Ducts vs Walls": ${hardDuctsWalls.length} results`));
	assert.match(ran, /ran "Columns vs Slab hard": 0 results/);
});

test('reports write one image per result in html and one row per result in csv', async () => {
	const dir = nw.path('reports');
	const out = await nw.call('export', {
		out: [
			{ what: 'clash_report', test: 'Ducts vs Walls', format: 'html', path: join(dir, 'ducts.html') },
			{ what: 'clash_report', test: 'Ducts vs Walls', format: 'csv', images: false, path: join(dir, 'ducts.csv') },
		],
	});
	assert.match(out, new RegExp(`ducts\\.html · ${hardDuctsWalls.length} results .* ${hardDuctsWalls.length} images`));
	const html = readFileSync(join(dir, 'ducts.html'), 'utf8');
	const images = [...html.matchAll(/<img src="([^"]+)"/g)].map((m) => m[1]!);
	assert.equal(images.length, hardDuctsWalls.length);
	for (const img of images) assert.equal(readFileSync(join(dir, img)).subarray(1, 4).toString(), 'PNG');
	assert.equal(readdirSync(join(dir, 'ducts_images')).length, hardDuctsWalls.length);

	const csv = readFileSync(join(dir, 'ducts.csv'), 'utf8').trim().split('\r\n');
	assert.equal(csv[0]!.replace(/^\uFEFF/, ''), 'clash,status,dist,item1,id1,item2,id2,level,grid,assigned,centre,group');
	assert.equal(csv.length - 1, hardDuctsWalls.length);
});

test('errors name the fix', async () => {
	const dup = await nw.callRaw('apply', { ops: [{ op: 'clash.add', name: 'Ducts vs Walls', a: 'set:MEP/Ducts', b: '?Item.Type = IfcWall' }] });
	assert.match(dup.text, /error bad_args: .*already exists[\s\S]*next: change it with clash\.update/);
	const noTol = await nw.callRaw('apply', { ops: [{ op: 'clash.add', name: 'X', a: 'set:MEP/Ducts', b: 'set:MEP/Ducts', type: 'clearance' }] });
	assert.match(noTol.text, /error bad_args: .*clearance test needs tol > 0/);
	const items = await nw.callRaw('apply', { ops: [{ op: 'clash.edit', items: '?Item.Type = IfcWall', status: 'active' }] });
	assert.match(items.text, /error bad_args: .*is not a clash handle/);
	const missing = await nw.callRaw('read', { what: 'clashes', test: 'Nope' });
	assert.match(missing.text, /error not_found: no clash test "Nope"[\s\S]*next: list tests with read \{what:"tests"\}/);
});

test('delete removes a test', async () => {
	await apply({ op: 'clash.delete', test: 'Columns vs Slab hard' });
	assert.equal(await testRow('Columns vs Slab hard'), undefined);
	assert.ok(await testRow('Ducts vs Walls'));
});

test('a run is one undo step', async () => {
	await apply({ op: 'clash.add', name: 'Undo run', a: 'set:MEP/Ducts', b: '?Item.Type = IfcWall' });
	await apply({ op: 'clash.run', test: 'Undo run' });
	assert.equal((await testRow('Undo run'))!.results, String(hardDuctsWalls.length));
	assert.match(await apply({ op: 'undo' }), /undid "MCP w\d+: clash\.run"/);
	const row = (await testRow('Undo run'))!;
	assert.deepEqual([row.status, row.results], ['new', '0']);
});

test('native groups and edits are saved in the NWD', async () => {
	await apply({ op: 'clash.group', test: 'Ducts vs Walls', by: 'item2.Item.Name' });
	const before = (await testRow('Ducts vs Walls'))!;
	const path = nw.path('saved.nwd').replaceAll('\\', '/');
	await nw.call('script', { code: `nw.Doc.SaveFile("${path}"); nw.Doc.OpenFile("${path}"); return nw.Doc.FileName;` });
	const walls = new Set(hardDuctsWalls.map((p) => p.split('+')[1]!));
	assert.equal(before.groups, String(walls.size));
	assert.deepEqual(await testRow('Ducts vs Walls'), before);
});

test('built-in rules suppress only their matching pairs and survive updates and saving', async () => {
	// Every fixture item belongs to one IFC file; each duct/wall pair consists of distinct composite objects.
	const name = 'Rule coverage';
	const sameFile = 'Items in same file';
	const sameComposite = 'Items in same composite object';
	await apply({ op: 'clash.add', name, a: '?Item.Type = IfcDuctSegment', b: '?Item.Type = IfcWall', rules: [sameFile, sameFile.toLowerCase()] });
	assert.equal((await testRow(name))!.rules, sameFile);
	await apply({ op: 'clash.run', test: name });
	assert.equal(total(await nw.call('read', { what: 'clashes', test: name })), 0);

	await apply({ op: 'clash.update', test: name, rules: [] }, { op: 'clash.run', test: name });
	assert.deepEqual(rows(await nw.call('read', { what: 'clashes', test: name })).map(pairOf).sort(), hardDuctsWalls);
	await apply({ op: 'clash.update', test: name, rules: sameComposite }, { op: 'clash.run', test: name });
	assert.equal(total(await nw.call('read', { what: 'clashes', test: name, status: 'new,active' })), hardDuctsWalls.length);
	await apply({ op: 'clash.update', test: name, tol: 1 });
	assert.equal((await testRow(name))!.rules, sameComposite, 'omitting rules preserves them');
	const path = nw.path('rules.nwd').replaceAll('\\', '/');
	await nw.call('script', { code: `nw.Doc.SaveFile("${path}"); nw.Doc.OpenFile("${path}"); return nw.Doc.FileName;` });
	assert.equal((await testRow(name))!.rules, sameComposite);

	await apply({ op: 'clash.update', test: name, rules: [sameFile] }, { op: 'clash.run', test: name });
	assert.equal(total(await nw.call('read', { what: 'clashes', test: name, status: 'new,active' })), 0);
	// Native reruns retain previously found pairs as resolved rather than deleting their history.
	assert.equal(total(await nw.call('read', { what: 'clashes', test: name, status: 'resolved' })), hardDuctsWalls.length);
});

test('unknown rules fail before changing a test or creating its selection sets', async () => {
	const before = await nw.call('read', { what: 'tests' });
	const sets = await nw.call('read', { what: 'sets' });
	for (const op of [
		{ op: 'clash.add', name: 'Bad rules', a: '?Item.Type = IfcDuctSegment', b: '?Item.Type = IfcWall', rules: ['not a built-in'] },
		{ op: 'clash.update', test: 'Rule coverage', rules: ['not a built-in'] },
	]) {
		const failed = await nw.callRaw('apply', { ops: [op] });
		assert.equal(failed.isError, true);
		assert.match(failed.text, /unknown or unavailable clash rule[\s\S]*next: use built-in rule names: .*Items in same file/);
		assert.equal(await nw.call('read', { what: 'tests' }), before);
		assert.equal(await nw.call('read', { what: 'sets' }), sets);
	}
});
