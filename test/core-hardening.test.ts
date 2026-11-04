import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync } from 'node:fs';
import { join } from 'node:path';
import { performance } from 'node:perf_hooks';
import { startSession, type Session } from './navisworks.ts';
import { navisworks } from '../scripts/plugin.ts';

let nw: Session;
let firstHandle: string;
let hospitalHandle: string;
before(async () => {
 nw = await startSession();
 for (const tool of ['read', 'apply', 'export']) console.log('F1 catalog', tool, await call('help', { tool }));
 firstHandle = handle(await call('read', { what: 'items', where: 'Item.Type = IfcWall' }));
});
after(async () => { await nw?.close(); });
async function call(name: string, args: Record<string, unknown> = {}): Promise<string> {
 let result = await nw.callRaw(name, args);
 for (let job; (job = /next: job \{id:"(j[\w-]+)"/.exec(result.text)?.[1]); ) result = await nw.callRaw('job', { id: job, wait: 60000 });
 if (result.isError) throw new Error(result.text);
 return result.text;
}
const handle = (s: string) => /\b(r[\w.-]+) (?:items|clashes|tree)/.exec(s)![1]!;
const total = (s: string) => Number(/items ([\d,]+) \(exact\)/.exec(s)![1]!.replaceAll(',', ''));
const rows = (s: string) => s.split('\n').filter(l => /^\d+\|/.test(l));
const follow = (s: string) => JSON.parse(/^more: read (.+)$/m.exec(s)![1]!.replace(/(\w+):/g, '"$1":'));

test('F1 root IDs emitted by tree round-trip through every items target', async () => {
 const tree = await call('read', { what: 'tree' });
 const id = rows(tree)[0]!.split('|')[1]!;
 assert.equal(total(await call('read', { what: 'items', in: `id:${id}` })), 1);
 assert.match(await call('read', { what: 'props', items: `id:${id}` }), new RegExp(id));
});

test('F1 quoted filters preserve joins, escaped quotes and operators', async () => {
 for (const value of ['A and B', 'A or B', 'A " and B', 'A \\ or >= B']) {
  await call('apply', { ops: [{ op: 'props.set', items: '?Item.Name = W-South', tab: 'F1', props: { Text: value } }] });
  assert.equal(total(await call('read', { what: 'items', where: `F1.Text = ${JSON.stringify(value)}` })), 1);
 }
 await call('apply', { ops: [{ op: 'tl.add', name: 'A and B', start: '2026-03-01', end: '2026-03-02' }] });
 assert.match(await call('read', { what: 'tasks', where: 'name = "A and B"' }), /A and B/);
});

test('F1 native searches disclose topmost pruning and can include matching descendants', async () => {
 const ordinary = await call('read', { what: 'items', where: 'has Item.Name' });
 assert.match(ordinary, /topmost matching items/);
 const all = await call('read', { what: 'items', where: 'has Item.Name', descendants: true });
 const expected = Number(await call('script', { code: 'return nw.Doc.Models.RootItemDescendantsAndSelf.Count(i => i.PropertyCategories.FindPropertyByDisplayName("Item", "Name") != null);' }));
 assert.equal(total(all), expected);
 assert.ok(total(all) > total(ordinary));
 await call('apply', { ops: [{ op: 'set.save', name: 'F1/Descendants', items: handle(all) }] });
 assert.equal(total(await call('read', { what: 'items', in: 'set:F1/Descendants' })), expected);
});

test('F1 predictable malformed arguments return bad_args with actionable hints', async () => {
 for (const [tool, args] of [
  ['read', { what: {} }], ['read', { what: [] }], ['apply', { ops: [{ op: {} }] }],
  ['export', { out: [{ what: [] }] }], ['apply', { ops: [{ op: 'reset' }], preview: {} }],
  ['read', { what: 'items', in: 'all', limit: 2147483648 }],
  ['read', { what: 'props', items: 'id:999999999999:0' }],
  ['apply', { ops: [{ op: 'undo', steps: -1 }] }], ['job', { id: 'j1', wait: 2147483648 }],
 ] as const) {
  const result = await nw.callRaw(tool, args);
  assert.match(result.text, /error bad_args:[\s\S]*next: /, JSON.stringify({ tool, args, result }));
 }
 const stale = await nw.callRaw('read', { what: 'props', items: 'r999999999999999999999999' });
 assert.match(stale.text, /error stale_handle:/);
});

test('F1 pagination cannot loop at zero or overflow and applies across domains', async () => {
 for (const args of [{ what: 'items', where: 'Item.Type = IfcWall' }, { what: 'tasks' }, { what: 'qto', level: 'catalog' }]) {
  const zero = await call('read', { ...args, limit: 0 });
  assert.doesNotMatch(zero, /^more:/m);
  for (const invalid of [{ limit: -1 }, { page: 0 }, { page: 2147483647, limit: 2 }]) {
   const r = await nw.callRaw('read', { ...args, ...invalid });
   assert.ok(r.isError ? /error bad_args:/.test(r.text) : rows(r.text).length === 0, r.text);
  }
 }
});

test('F1 tree, values and properties continue without omissions', async () => {
 const countPages = async (args: Record<string, unknown>, line: RegExp) => {
  let page = await call('read', args);
  const seen: string[] = [];
  for (let n = 0; ; n++) {
   seen.push(...page.split('\n').filter(s => line.test(s)));
   if (!/^more:/m.test(page)) return seen;
   assert.ok(n < 100);
   page = await call('read', follow(page));
  }
 };
 const allTree = rows(await call('read', { what: 'tree', depth: 4, limit: 1000 }));
 assert.deepEqual(await countPages({ what: 'tree', depth: 4, limit: 2 }, /^\d+\|/), allTree);
 const propLines = await countPages({ what: 'props', items: '?Item.Type = IfcWall', limit: 1, tabs: 'Pset_Fixture' }, /^\d+:/);
 assert.equal(propLines.length, 4);
 assert.equal(new Set(propLines).size, 4);
 const values = await countPages({ what: 'values', prop: 'Pset_Fixture.FireRating', top: 1 }, /^F\d+\|/);
 assert.deepEqual(values.sort(), ['F30|2', 'F90|2']);
});

test('F1 error recovery hints name existing targets and valid tools; standalone ops are documented', async () => {
 const unknown = await nw.callRaw('help', { tool: 'not-a-tool' });
 assert.doesNotMatch(unknown.text.split('next:')[1]!, /not-a-tool/);
 const missing = await nw.callRaw('read', { what: 'items', where: 'Missing.NoSuchProperty = 1' });
 const hint = /next:.*read (\{.*\})/.exec(missing.text)?.[1];
 assert.ok(hint, missing.text);
 assert.equal((await nw.callRaw('read', JSON.parse(hint.replace(/(\w+):/g, '"$1":')))).isError, false);
 assert.match(await call('help', { tool: 'apply', op: 'open' }), /only op|alone|standalone/);
});

test('F1 bounded tables preserve every row through verbatim continuations', async () => {
 const text = 'x'.repeat(4000);
 await call('apply', { ops: [{ op: 'props.set', items: '?Item.Type = IfcWall', tab: 'F1', props: Object.fromEntries(Array.from({ length: 8 }, (_, i) => ['Long' + i, text])) }] });
 let page = await call('read', { what: 'items', where: 'Item.Type = IfcWall', cols: Array.from({ length: 8 }, (_, i) => 'F1.Long' + i), limit: 100000 });
 const seen: string[] = [];
 for (let i = 0; ; i++) {
  assert.ok(page.length <= 30000, `reply has ${page.length} characters`);
  seen.push(...rows(page).map(l => l.split('|')[1]!));
  if (!/^more:/m.test(page)) break;
  assert.ok(i < 10, 'continuation must advance');
  page = await call('read', follow(page));
 }
 assert.equal(seen.length, 4);
 assert.equal(new Set(seen).size, 4);
 let textPage = await call('script', { code: 'return new string(\'x\', 100000);' });
 let length = 0;
 for (;;) {
  assert.ok(textPage.length <= 30000);
  length += textPage.split('\n').filter(l => !l.startsWith('more:')).join('').length;
  if (!/^more:/m.test(textPage)) break;
  textPage = await call('read', follow(textPage));
 }
 assert.equal(length, 100000);
});

test('F1 clash handles resolve to live document items for props and selection', async () => {
 await call('apply', { ops: [{ op: 'clash.add', name: 'F1 clash', a: '?Item.Type = IfcWall', b: '?Item.Type = IfcDuctSegment' }, { op: 'clash.run', test: 'F1 clash' }] });
 const h = handle(await call('read', { what: 'clashes', test: 'F1 clash' }));
 assert.match(await call('read', { what: 'props', items: `${h}#0` }), /Pset_Fixture/);
 await call('apply', { ops: [{ op: 'select', items: `${h}#0` }] });
 assert.equal(total(await call('read', { what: 'items', in: 'selection' })), 2);
});

test('F1 real-model missing properties and live missing-property sets', async () => {
 const source = join(navisworks().dir, 'Samples', 'Quantification');
 const copy = join(nw.dir, 'hospital');
 cpSync(source, copy, { recursive: true });
 await call('apply', { ops: [{ op: 'open', path: join(copy, 'Autodesk_Hospital_Sample.nwf') }] });
 await call('script', { code: 'nw.Doc.SetActiveSheet("cache"); return nw.Doc.Models.Count;' });
 const missing = 'Element.Category = Doors and !has Element.Fire Rating';
 const expected = Number(await call('script', { code: 'var s = new Search(); s.Selection.SelectAll(); s.SearchConditions.Add(SearchCondition.HasPropertyByDisplayName("Element", "Category").EqualValue(VariantData.FromDisplayString("Doors"))); s.SearchConditions.Add(SearchCondition.HasPropertyByDisplayName("Element", "Fire Rating").Negate()); return s.FindAll(nw.Doc, false).Count;' }));
 assert.ok(expected > 0, 'the hospital must contain doors missing Fire Rating');
 console.log('F1 hospital missing Fire Rating', navisworks().year, expected);
 assert.equal(total(await call('read', { what: 'items', where: missing })), expected);
 assert.match(await call('help', { tool: 'read', op: 'items' }), /!has/);
 await call('apply', { ops: [{ op: 'set.save', name: 'F1/Missing', where: missing }] });
 assert.equal(total(await call('read', { what: 'items', in: 'set:F1/Missing' })), expected);
});

test('F1 unit-suffixed filters convert or explicitly reject', async () => {
 const copy = join(nw.dir, 'hospital');
 if (!(await call('status')).includes('Hospital')) {
  cpSync(join(navisworks().dir, 'Samples', 'Quantification'), copy, { recursive: true });
  await call('apply', { ops: [{ op: 'open', path: join(copy, 'Autodesk_Hospital_Sample.nwf') }] });
  await call('script', { code: 'nw.Doc.SetActiveSheet("cache"); return nw.Doc.Models.Count;' });
 }
 const metric = total(await call('read', { what: 'items', where: 'Element.Length > 3000', limit: 0 }));
 for (const suffix of ['3000mm', '3 m']) {
  const r = await nw.callRaw('read', { what: 'items', where: `Element.Length > "${suffix}"`, limit: 0 });
  if (r.isError) assert.match(r.text, /error bad_args:.*unit|error bad_args:.*numeric/);
  else assert.equal(total(r.text), metric);
 }
 const tenFeet = await nw.callRaw('read', { what: 'items', where: 'Element.Length > "10 ft"', limit: 0 });
 if (!tenFeet.isError) assert.equal(total(tenFeet.text), total(await call('read', { what: 'items', where: 'Element.Length > 3048', limit: 0 })));
 else assert.match(tenFeet.text, /error bad_args:/);
});

test('F1 hospital repeated reads avoid full-model enumeration', async () => {
 const failures: string[] = [];
 hospitalHandle = (await call('script', { code: 'var s=new Search { PruneBelowMatch=false }; s.Selection.SelectAll(); s.SearchConditions.Add(SearchCondition.HasPropertyByDisplayName("Item","Name")); return nw.Handle(s.FindAll(nw.Doc,false));' })).trim();
 for (const args of [{ what: 'values', prop: 'Item.Type' }, { what: 'values', prop: 'Element.Category' }, { what: 'stats' }, { what: 'tree', depth: 1 }, { what: 'items', in: hospitalHandle, limit: 3 }, { what: 'items', in: hospitalHandle, group: 'Item.Type', limit: 3 }, { what: 'props', items: hospitalHandle, limit: 1 }]) {
  let first = '';
  for (let repeat = 0; repeat < 3; repeat++) {
   const begin = performance.now();
   const result = await call('read', args);
   const ms = performance.now() - begin;
   console.log('F1 timing', JSON.stringify({ year: navisworks().year, args, repeat, ms, chars: result.length }));
   if (repeat === 0) first = result;
   else if (args.what !== 'tree') assert.equal(result, first);
   if (repeat > 0 && ms >= 1000) failures.push(`warm ${args.what} ${'prop' in args ? args.prop : ''}: ${ms}ms`);
  }
 }
 assert.deepEqual(failures, []);
});

test('F1 group headers show top groups and the omitted count', async () => {
 const grouped = await call('read', { what: 'items', in: hospitalHandle, group: 'Item.Type', limit: 3 });
 assert.match(grouped, /\d+ more groups/);
 assert.ok(grouped.length < 8000);
 await call('apply', { ops: [{ op: 'open', path: nw.fixture }] });
 assert.doesNotMatch(await call('read', { what: 'stats' }), /Hospital/);
});

test('F1 cached property aggregates invalidate after edits and undo', async () => {
 const write = (value: string) => call('apply', { ops: [{ op: 'props.set', items: '?Item.Name = W-South', tab: 'F1', props: { Cached: value } }] });
 await write('before');
 assert.match(await call('read', { what: 'values', prop: 'F1.Cached' }), /before\|1/);
 await write('after');
 assert.match(await call('read', { what: 'values', prop: 'F1.Cached' }), /after\|1/);
 await call('apply', { ops: [{ op: 'undo' }] });
 assert.match(await call('read', { what: 'values', prop: 'F1.Cached' }), /before\|1/);
});

test('F1 abandoned completed jobs are bounded and never reported as running', async () => {
 const block = nw.callRaw('script', { code: 'System.Threading.Thread.Sleep(30000); return "done";' });
 await new Promise(resolve => setTimeout(resolve, 300));
 const pending = Array.from({ length: 40 }, () => nw.callRaw('read', { what: 'items', in: 'all' }));
 await Promise.all([block, ...pending]);
 await new Promise(resolve => setTimeout(resolve, 5500));
 const status = await call('status');
 assert.doesNotMatch(status, /^jobs:/m);
 const retained = /completed results awaiting collection: (\d+)/.exec(status);
 assert.ok(retained, status);
 assert.ok(Number(retained[1]) <= 32, status);
});

test('F1 status stays responsive during scripts and cancellation is cooperative', async () => {
 const code = 'for (int i=0; i<600 && !nw.Cancelled; i++) System.Threading.Thread.Sleep(100); return nw.Cancelled ? "cancel observed" : "finished";';
 await call('script', { code, check: true });
 const running = call('script', { code });
 await new Promise(resolve => setTimeout(resolve, 300));
 const begin = performance.now();
 const status = await call('status');
 assert.ok(performance.now() - begin < 1000, status);
 const id = /\b(j[\w.-]+) script/.exec(status)![1]!;
 await nw.callRaw('job', { id, cancel: true, wait: 1000 });
 assert.match(await running, /cancel observed|cancelled/);
 assert.doesNotMatch(await call('status'), new RegExp(id + ' script'));
});

test('F1 handles from a closed process never address new results', async () => {
 const old = firstHandle;
 await nw.close();
 nw = await startSession();
 await call('read', { what: 'items', where: 'Item.Type = IfcDuctSegment' });
 const r = await nw.callRaw('apply', { ops: [{ op: 'color', items: old, rgb: '#ff0011' }] });
 assert.match(r.text, /error stale_handle:/);
 assert.doesNotMatch(await call('read', { what: 'items', where: 'Item.Type = IfcDuctSegment', cols: 'color' }), /#ff0011/);
});
