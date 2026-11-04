import assert from 'node:assert/strict';
import { test } from 'node:test';
import { startSession } from './navisworks.ts';

const handle = (text: string) => /\b(r\d+)\b/.exec(text)![1]!;
const rows = (text: string) => text.split('\n').filter(l => /^\d+\|/.test(l)).map(l => l.split('|'));
test('Quantification hardening through MCP', async t => {
	const nw = await startSession();
	async function raw(tool: string, args: Record<string, unknown>) {
		let result = await nw.callRaw(tool, args);
		for (let job; (job = /next: job \{id:"(j\d+)"/.exec(result.text)?.[1]); ) result = await nw.callRaw('job', { id: job, wait: 60000 });
		return result;
	}
	async function call(tool: string, args: Record<string, unknown>) {
		const result = await raw(tool, args);
		assert.equal(result.isError, false, result.text);
		return result.text;
	}
	const apply = (ops: Record<string, unknown>[]) => call('apply', { ops });
	try {
		await t.test('B1 stale takeoff handles cannot delete replacement rows, including identical retakeoffs', async () => {
			for (const replacement of ['IfcDuctSegment', 'IfcWall']) {
				await apply([{ op: 'open', path: nw.fixture }]);
				const created = handle(await apply([{ op: 'qto.item.add', name: 'Old' }, { op: 'qto.takeoff', items: '?Item.Type = IfcWall', item: 'Old', from: 'bbox' }]));
				const old = handle(await call('read', { what: 'qto', level: 'takeoffs' }));
				await apply([{ op: 'qto.delete', name: 'Old' }]);
				await apply([{ op: 'qto.item.add', name: 'Old' }, { op: 'qto.takeoff', items: `?Item.Type = ${replacement}`, item: 'Old', from: 'bbox' }]);
				const before = rows(await call('read', { what: 'qto', level: 'takeoffs' }));
				for (const stale of [old, created]) {
					const result = await raw('apply', { ops: [{ op: 'qto.delete', takeoffs: stale }] });
					assert.match(result.text, /error stale_handle:/);
				}
				assert.deepEqual(rows(await call('read', { what: 'qto', level: 'takeoffs' })), before);
			}
		});
	} finally { await nw.close(); }
});
