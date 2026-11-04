import assert from 'node:assert/strict';
import { test } from 'node:test';
import { navisworks } from '../scripts/plugin.ts';
import { boxes } from './fixture/scene.ts';
import { startSession } from './navisworks.ts';

test('native takeoff identity is preserved and GUID-less or virtual rows never resolve to the file root', async () => {
	const nw = await startSession({ ifcConversion: 'v4' });
	const v4 = navisworks().year === 2027;
	const walls = boxes.filter((b) => b.ifcClass === 'IfcWall');
	try {
		const identity = await nw.call('script', { code: `
var items = nw.Items("?Item.Type = IfcWall");
return items.Count + ":" + items.Count(i => i.InstanceGuid == Guid.Empty) + ":" + Autodesk.Navisworks.Api.Interop.LcUOption.GetName("file_readers.LcNwcLoaderPlugin:lcldifc.load_method").GetPtr().Name;` });
		assert.equal(identity, `${walls.length}:${v4 ? walls.length : 0}:lcldifc_load_method:${v4 ? 3 : 2}`);
		await nw.call('apply', { ops: [
			{ op: 'qto.item.add', name: 'MCP/Walls' },
			{ op: 'qto.item.add', name: 'Native/Walls' },
			{ op: 'qto.item.add', name: 'Native/Virtual' },
		] });
		const took = await nw.call('apply', { ops: [{ op: 'qto.takeoff', items: '?Item.Type = IfcWall', item: 'MCP/Walls', from: 'bbox' }] });
		assert.match(took, new RegExp(`took off ${v4 ? 0 : walls.length} items`));
		if (v4) assert.match(took, new RegExp(`${walls.length} without a GUID were left out[\\s\\S]*next: .*Conversion Method to v3`));
		// Exercise the ModelItem overload itself. For v4 it inserts unlinked (SQL NULL identity) rows.
		const inserted = await nw.call('script', { code: `
var tk = Autodesk.Navisworks.Api.Takeoff.DocumentExtensions.GetTakeoff(nw.Doc);
long parent, virtualParent;
using (var cmd = tk.Database.Value.CreateCommand()) {
 cmd.CommandText = "SELECT ID FROM TK_Item WHERE Name='Walls' ORDER BY ID DESC LIMIT 1"; parent = Convert.ToInt64(cmd.ExecuteScalar());
 cmd.CommandText = "SELECT ID FROM TK_Item WHERE Name='Virtual'"; virtualParent = Convert.ToInt64(cmd.ExecuteScalar());
}
using (var tr = tk.Database.BeginTransaction(Autodesk.Navisworks.Api.Data.DatabaseChangedAction.Edited)) {
 foreach (var item in nw.Items("?Item.Type = IfcWall")) tk.Objects.InsertModelItemTakeoff(parent, item);
 tk.Objects.InsertVirtualTakeoff(virtualParent, tk.Objects.CreateDefaultInputVariables());
 tr.Commit();
}
using (var cmd = tk.Database.Value.CreateCommand()) {
 cmd.CommandText = "SELECT COUNT(*) FROM TK_Object WHERE ModelItemID IS NULL";
 nw.Out.AppendLine("unlinked=" + cmd.ExecuteScalar());
} return "inserted";` });
		assert.match(inserted, new RegExp(`unlinked=${(v4 ? walls.length : 0) + 1}\\b`));
		const models = async (item: string) => (await nw.call('read', { what: 'qto', level: 'takeoffs', item })).split('\n')
			.filter((l) => /^\d+\|/.test(l)).map((l) => l.split('|')[3]!).sort();
		const expected = v4 ? walls.map(() => '-') : walls.map((w) => w.name).sort();
		assert.deepEqual(await models('Native/Walls'), expected);
		assert.deepEqual(await models('Native/Virtual'), ['-']);
		const grouped = await nw.call('read', { what: 'qto', level: 'totals', item: 'Native/Virtual', by: 'Item.Name' });
		assert.match(grouped, /^0\|-\|1\|/m);
		const path = nw.path('identity.nwd').replaceAll('\\', '/');
		await nw.call('script', { code: `nw.Doc.SaveFile("${path}"); nw.Doc.OpenFile("${path}"); return nw.Doc.FileName;` });
		assert.deepEqual(await models('Native/Walls'), expected);
		assert.deepEqual(await models('Native/Virtual'), ['-']);
	} finally {
		await nw.close();
	}
});
