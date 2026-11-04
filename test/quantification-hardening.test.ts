import assert from 'node:assert/strict';
import { cpSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { test } from 'node:test';
import { navisworks } from '../scripts/plugin.ts';
import { startSession } from './navisworks.ts';

const handle = (text: string) => /\b(r\d+)\b/.exec(text)![1]!;
const rows = (text: string) => text.split('\n').filter(l => /^\d+\|/.test(l)).map(l => l.split('|'));
const number = (text: string) => Number(text.replaceAll(',', ''));
function near(actual: string, expected: number) {
	assert.ok(Math.abs(number(actual) - expected) <= Math.max(0.0001, Math.abs(expected) * 0.00001), `${actual} != ${expected}`);
}

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
		const samples = join(navisworks().dir, 'Samples');
		const legacy = nw.path('Architecture.nwc');
		cpSync(join(samples, 'Getting Started', 'Architecture.nwc'), legacy);
		await t.test('A4 legacy Revit physical doubles use explicit exporter units without scaling dimensionless properties', async () => {
			await apply([{ op: 'open', path: legacy }]);
			for (let repeat = 0; repeat < 2; repeat++) {
				const text = await call('read', { what: 'items', where: 'Element.Id = 201159', cols: 'Element.Length,Element.Area,Element.Volume' });
				const cells = rows(text)[0]!;
				assert.match(cells.at(-3)!, / mm$/);
				near(cells.at(-3)!.replace(' mm', ''), 38.25 * 304.8);
				near(cells.at(-2)!.replace(' m2', ''), 551.254 * 0.3048 ** 2);
				near(cells.at(-1)!.replace(' m3', ''), 223.947 * 0.3048 ** 3);
				const filtered = await call('read', { what: 'items', where: 'Element.Id = 201159 and Element.Length > 11000' });
				assert.equal(rows(filtered).length, 1);
				assert.match(await call('read', { what: 'props', items: '?Element.Id = 201159', tabs: 'Element' }), /Length=11,658\.6 mm/);
			}
			await apply([{ op: 'props.set', items: '?Element.Id = 201159', tab: 'QA', props: { Length: 38.25 } }]);
			assert.match(await call('read', { what: 'props', items: '?Element.Id = 201159', tabs: 'QA' }), /Length=38\.25$/m);
		});
		const hospital = join(nw.dir, 'hospital');
		cpSync(join(samples, 'Quantification'), hospital, { recursive: true });
		await t.test('C1 mapped hospital quantities agree with physical properties in reads, totals and CSV', async () => {
		for (const variable of [false, true]) {
			await apply([{ op: 'open', path: join(hospital, 'Autodesk_Hospital_Structural.nwc') }]);
			if (variable) await call('script', { code: 'var tk=Autodesk.Navisworks.Api.Takeoff.DocumentExtensions.GetTakeoff(nw.Doc); var cm=Autodesk.Navisworks.Api.Interop.LcTkConfigManager.Instance; cm.UnitsSystem=Autodesk.Navisworks.Api.Takeoff.TakeoffUnitSystem.Metric; string error; cm.LoadTemplateConfigFile(out error); var names=new[]{"Length","Width","Thickness","Height","Perimeter","Area","Volume","Weight"}; for(int i=0;i<names.Length;i++){var group=i<5?Autodesk.Navisworks.Api.Takeoff.TakeoffUnitGroup.Length:i==5?Autodesk.Navisworks.Api.Takeoff.TakeoffUnitGroup.Area:i==6?Autodesk.Navisworks.Api.Takeoff.TakeoffUnitGroup.Volume:Autodesk.Navisworks.Api.Takeoff.TakeoffUnitGroup.Weight; var unit=i<5?Autodesk.Navisworks.Api.Takeoff.TakeoffUnits.Meter:i==5?Autodesk.Navisworks.Api.Takeoff.TakeoffUnits.SquareMeter:i==6?Autodesk.Navisworks.Api.Takeoff.TakeoffUnits.CubicMeter:Autodesk.Navisworks.Api.Takeoff.TakeoffUnits.Kilogram; cm.AddTakeoffProperty("Model"+names[i],true,group,unit); cm.AddTakeoffPropertyRelatedCalculationColumn("Model"+names[i],true,group,unit,i);} cm.CreateSchema(out error); Autodesk.Navisworks.Api.Interop.DocumentTakeoffExtensions.SetHasSetUp(tk,true); Autodesk.Navisworks.Api.Interop.DocumentTakeoffExtensions.InitializeDatabase(tk); return error;' });
			await apply([{ op: 'qto.item.add', name: 'Slabs' }, { op: 'qto.takeoff', items: '?Element.Id = 186254', item: 'Slabs' }]);
			if (variable) t.diagnostic(await call('script', { code: 'var tk=Autodesk.Navisworks.Api.Takeoff.DocumentExtensions.GetTakeoff(nw.Doc); foreach(var v in tk.Objects.SelectVariables(1)) if(v.Definition.Name=="Area" || v.Definition.Name=="ModelArea") nw.Out.AppendLine(v.Definition.Name+" active="+v.ActiveValue+" unit="+v.ActiveUnits+" formula="+(v.IsAbleToGetFormula?v.ActiveFormula:"input")); return null;' }));
			const props = rows(await call('read', { what: 'items', where: 'Element.Id = 186254', cols: 'Element.Area,Element.Volume' }))[0]!;
			const area = number(props.at(-2)!.replace(' m2', '')), volume = number(props.at(-1)!.replace(' m3', ''));
			for (let repeat = 0; repeat < 2; repeat++) {
				for (const level of ['takeoffs', 'totals']) {
					const row = rows(await call('read', { what: 'qto', level }))[0]!;
					near(row.at(-2)!, area); near(row.at(-1)!, volume);
				}
			}
			const path = nw.path('slabs.csv');
			await call('export', { out: [{ what: 'qto_csv', path }] });
			const csv = readFileSync(path, 'utf8').trim().split('\n')[1]!.split(',');
			near(csv.at(-2)!, area); near(csv.at(-1)!, volume);
		}
		});
		await t.test('A1 bbox fallback respects the hospital fixed imperial schema', async () => {
			await apply([{ op: 'open', path: join(hospital, 'Autodesk_Hospital_Sample.nwf') }]);
			await call('script', { code: 'nw.Doc.SetActiveSheet("cache"); return nw.Doc.Models.Count;' });
			const dimensions = (await call('script', { code: 'var b=nw.Items("?Element.Id = 186254").First().BoundingBox(); return string.Join(";", new[]{nw.Mm(b.Size.X),nw.Mm(b.Size.Y),nw.Mm(b.Size.Z)}.Select(x=>x.ToString("R",System.Globalization.CultureInfo.InvariantCulture)));' })).trim().split(';').map(Number);
			const props = rows(await call('read', { what: 'items', where: 'Element.Id = 186254', cols: 'Element.Area,Element.Volume' }))[0]!;
			for (let repeat = 0; repeat < 2; repeat++) {
				await apply([{ op: 'qto.item.add', name: `F2/Bbox${repeat}` }, { op: 'qto.takeoff', items: '?Element.Id = 186254', item: `F2/Bbox${repeat}`, from: 'bbox' }]);
				const result = rows(await call('read', { what: 'qto', level: 'takeoffs', item: `F2/Bbox${repeat}` }));
				assert.equal(result.length, 1);
				near(result[0]!.at(-3)!, Math.max(dimensions[0]!, dimensions[1]!));
				near(result[0]!.at(-2)!, number(props.at(-2)!.replace(' m2', '')));
				near(result[0]!.at(-1)!, number(props.at(-1)!.replace(' m3', '')));
			}
		});
		await t.test('C13 A7 takeoff pages and property totals avoid full-model reconstruction', async () => {
			// Existing hospital catalog: 3,320 takeoffs in an 80,000+ node model.
			const expected = Number(await call('script', { code: 'var tk=Autodesk.Navisworks.Api.Takeoff.DocumentExtensions.GetTakeoff(nw.Doc); using(var cmd=tk.Database.Value.CreateCommand()){cmd.CommandText="SELECT COUNT(*) FROM TK_Object"; return cmd.ExecuteScalar();}' }));
			for (const args of [{ what: 'qto', level: 'takeoffs', limit: 1 }, { what: 'qto', by: 'Element.Category', limit: 20 }]) {
				for (let repeat = 0; repeat < 2; repeat++) {
					const start = performance.now();
					const text = await call('read', args);
					const ms = performance.now() - start;
					t.diagnostic(`${JSON.stringify(args)} repeat ${repeat}: ${ms.toFixed(1)} ms`);
					assert.ok(rows(text).length > 0);
					if (args.level === 'takeoffs') {
						assert.equal(rows(text).length, 1);
						assert.equal(number(/takeoffs ([\d,]+) \(exact\)/.exec(text)![1]!), expected);
					} else assert.equal(rows(text).reduce((sum, row) => sum + number(row[2]!), 0), expected);
					assert.ok(ms < 2000, `interactive QTO took ${ms.toFixed(1)} ms`);
				}
			}
		});
	} finally { await nw.close(); }
});
