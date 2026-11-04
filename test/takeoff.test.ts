import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { after, before, test } from 'node:test';
import { boxes, type Box } from './fixture/scene.ts';
import { startSession, type Session } from './navisworks.ts';

let nw: Session;
before(async () => (nw = await startSession()));
after(() => nw?.close());

const handle = (text: string) => /^(r[\w-]+) /.exec(text)![1]!;
const total = (text: string) => Number(/ ([\d,]+) \(exact\)/.exec(text)?.[1]?.replace(/,/g, ''));

/// Quantities qto.takeoff from:"bbox" derives from an axis-aligned box, in m, m2, m3.
function quantities(b: Box) {
	const [dx, dy, dz] = b.max.map((v, i) => (v - b.min[i]!) / 1000) as [number, number, number];
	const [s0, s1] = [dx, dy, dz].sort((x, y) => y - x) as [number, number, number];
	return { count: 1, length: Math.max(dx, dy) * 1000, area: s0 * s1, volume: dx * dy * dz };
}

function sum(list: Box[]) {
	return list.map(quantities).reduce((a, q) => ({ count: a.count + q.count, length: a.length + q.length, area: a.area + q.area, volume: a.volume + q.volume }), { count: 0, length: 0, area: 0, volume: 0 });
}

function row(text: string, key: string): Record<string, string> {
	const lines = text.split('\n');
	const header = lines.find((l) => l.startsWith('#|'))!.split('|');
	const cells = lines.map((l) => l.split('|')).find((c) => c.length === header.length && c[1] === key);
	assert.ok(cells, `no row ${key} in\n${text}`);
	return Object.fromEntries(header.map((h, i) => [h, cells[i]!]));
}

/// The text shows rounded numbers (whole units from 100, two decimals from 1), so compare within that rounding.
function near(shown: string, expected: number, what: string) {
	const v = Number(shown.replace(/,/g, ''));
	const tol = Math.abs(expected) >= 100 ? 0.5 : Math.abs(expected) >= 1 ? 0.005 : 0.00005;
	assert.ok(Math.abs(v - expected) <= tol * 1.01 + 1e-9, `${what}: shown ${shown}, expected ${expected}`);
}

function sameQuantities(r: Record<string, string>, q: ReturnType<typeof sum>, what: string) {
	near(r.count!, q.count, what + ' count');
	near(r.length_mm!, q.length, what + ' length');
	near(r.area_m2!, q.area, what + ' area');
	near(r.volume_m3!, q.volume, what + ' volume');
}

const walls = boxes.filter((b) => b.ifcClass === 'IfcWall');
const supply = boxes.filter((b) => b.props.System === 'Supply');

test('read qto before any setup says how to start', async () => {
	const text = await nw.call('read', { what: 'qto' });
	assert.match(text, /not set up[\s\S]*next: apply \{ops:\[\{op:"qto.item.add"/);
});

test('catalog items are added by path, creating missing groups, and adding again changes nothing', async () => {
	const preview = await nw.call('apply', { preview: true, ops: [{ op: 'qto.item.add', name: 'Structure/Walls' }] });
	assert.match(preview, /would add catalog item "Structure\/Walls" \(first sets up Quantification with metric units\)/);
	const added = await nw.call('apply', {
		ops: [
			{ op: 'qto.item.add', name: 'Structure/Walls' },
			{ op: 'qto.item.add', name: 'Structure/Slabs', rgb: '#ff0000' },
			{ op: 'qto.group.add', name: 'MEP/Ducts' },
			{ op: 'qto.item.add', name: 'MEP/Ducts/Supply' },
		],
	});
	assert.match(added, /0 set up Quantification \(metric\); added catalog item "Structure\/Walls" and group "Structure"/);
	assert.match(added, /2 added catalog group "MEP", "MEP\/Ducts"/);
	const catalog = await nw.call('read', { what: 'qto', level: 'catalog' });
	assert.deepEqual(
		catalog.split('\n').filter((l) => /^\d+\|/.test(l)).map((l) => l.split('|').slice(1, 3).join(' ')),
		['group Structure', 'item Structure/Walls', 'item Structure/Slabs', 'group MEP', 'group MEP/Ducts', 'item MEP/Ducts/Supply'],
	);
	assert.match(await nw.call('apply', { ops: [{ op: 'qto.item.add', name: 'Structure/Walls' }] }), /catalog item "Structure\/Walls" exists/);
	const clash = await nw.callRaw('apply', { ops: [{ op: 'qto.item.add', name: 'Structure/Walls/Inner' }] });
	assert.match(clash.text, /"Structure\/Walls" is a catalog item, not a group/);
});

test('bounding-box takeoff gives counts, lengths, areas and volumes of the geometry', async () => {
	const took = await nw.call('apply', {
		ops: [
			{ op: 'qto.takeoff', items: '?Item.Type = IfcWall', item: 'Structure/Walls', from: 'bbox' },
			{ op: 'qto.takeoff', items: '?Pset_Fixture.System = Supply', item: 'MEP/Ducts/Supply', from: 'bbox' },
		],
	});
	assert.match(took, new RegExp(`0 took off ${walls.length} items into "Structure/Walls" -> r[\\w-]+; 0 had quantities from model properties`));
	assert.match(took, /undo: apply/);

	const totals = await nw.call('read', { what: 'qto' });
	assert.match(totals, /qto totals by item 3 \(exact\)/);
	sameQuantities(row(totals, 'Structure/Walls'), sum(walls), 'walls');
	sameQuantities(row(totals, 'MEP/Ducts/Supply'), sum(supply), 'supply ducts');
	assert.equal(row(totals, 'Structure/Slabs').takeoffs, '0');

	const takeoffs = await nw.call('read', { what: 'qto', level: 'takeoffs', item: 'Structure/Walls' });
	assert.equal(total(takeoffs), walls.length);
	const south = takeoffs.split('\n').find((l) => l.includes('|W-South|'))!.split('|');
	const q = quantities(walls.find((b) => b.name === 'W-South')!);
	near(south[5]!, q.length, 'W-South length');
	near(south[7]!, q.volume, 'W-South volume');

	const first = await nw.call('read', { what: 'qto', level: 'takeoffs', limit: 2 });
	const r = handle(first);
	const more = JSON.parse(/^more: read (\{.*\})$/m.exec(first)![1]!.replace(/(\w+):/g, '"$1":'));
	assert.deepEqual(more, { what: 'qto', in: r, page: 2, limit: 2 });
	const second = await nw.call('read', more);
	assert.match(second, new RegExp(`^${r} qto takeoffs ${walls.length + supply.length} \\(exact\\)`));
	assert.equal(second.split('\n').filter((l) => /^\d+\|/.test(l)).length, 2);
});

test('totals group by catalog group or by a model property', async () => {
	const byRating = await nw.call('read', { what: 'qto', by: 'Pset_Fixture.FireRating' });
	sameQuantities(row(byRating, 'F90'), sum(walls.filter((b) => b.props.FireRating === 'F90')), 'F90');
	sameQuantities(row(byRating, 'F30'), sum(walls.filter((b) => b.props.FireRating === 'F30')), 'F30');
	const byGroup = await nw.call('read', { what: 'qto', by: 'group' });
	sameQuantities(row(byGroup, 'Structure'), sum(walls), 'Structure');
	sameQuantities(row(byGroup, 'MEP/Ducts'), sum(supply), 'MEP/Ducts');
});

test('model takeoff counts items and says when the model has no quantities; repeats are skipped', async () => {
	const slab = await nw.call('apply', { ops: [{ op: 'qto.takeoff', items: '?Item.Type = IfcSlab', item: 'Structure/Slabs' }] });
	assert.match(slab, /took off 1 items into "Structure\/Slabs".*0 had quantities from model properties.*next: .*from:"bbox"/);
	const again = await nw.call('apply', { ops: [{ op: 'qto.takeoff', items: '?Item.Type = IfcWall', item: 'Structure/Walls', from: 'bbox' }] });
	assert.match(again, new RegExp(`took off 0 items .*; ${walls.length} were already in it`));
	const totals = await nw.call('read', { what: 'qto' });
	assert.equal(row(totals, 'Structure/Slabs').count, '1');
	sameQuantities(row(totals, 'Structure/Walls'), sum(walls), 'walls after repeat');

	const missing = await nw.callRaw('apply', { ops: [{ op: 'qto.takeoff', items: '?Item.Type = IfcColumn', item: 'Structure/Columns' }] });
	assert.match(missing.text, /no catalog item "Structure\/Columns"[\s\S]*next: add it first/);
});

test('export qto_csv writes the totals with full precision', async () => {
	const path = nw.path('qto.csv');
	assert.match(await nw.call('export', { out: [{ what: 'qto_csv', path, level: 'totals' }] }), /qto_csv .*qto\.csv 3 rows \(totals\)/);
	const lines = readFileSync(path, 'utf8').replace(/^﻿/, '').trim().split(/\r?\n/);
	assert.equal(lines[0], 'item,takeoffs,count,length_mm,area_m2,volume_m3');
	const cells = lines.find((l) => l.startsWith('Structure/Walls,'))!.split(',');
	const q = sum(walls);
	assert.equal(cells[2], String(walls.length));
	assert.ok(Math.abs(Number(cells[5]) - q.volume) < 1e-4, `csv volume ${cells[5]} vs ${q.volume}`);
});

test('delete removes takeoff rows or whole catalog paths in one undo step', async () => {
	const takeoffs = handle(await nw.call('read', { what: 'qto', level: 'takeoffs', item: 'Structure/Walls' }));
	const deleted = await nw.call('apply', { ops: [{ op: 'qto.delete', takeoffs: `${takeoffs}#0-1` }, { op: 'qto.delete', name: 'MEP' }] });
	assert.match(deleted, /0 deleted 2 takeoffs/);
	assert.match(deleted, /1 deleted "MEP" \(2 groups, 1 items, 2 takeoffs\)/);
	const after = await nw.call('read', { what: 'qto', level: 'catalog' });
	assert.doesNotMatch(after, /MEP/);
	assert.match(after, new RegExp(`\\|item\\|Structure/Walls\\|\\d+\\|${walls.length - 2}$`, 'm'));

	await nw.call('apply', { ops: [{ op: 'undo' }] });
	const restored = await nw.call('read', { what: 'qto' });
	sameQuantities(row(restored, 'Structure/Walls'), sum(walls), 'walls after undo');
	sameQuantities(row(restored, 'MEP/Ducts/Supply'), sum(supply), 'supply after undo');
});
