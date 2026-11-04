import assert from 'node:assert/strict';
import { copyFileSync, existsSync, rmSync } from 'node:fs';
import { after, before, test } from 'node:test';
import { startSession, type Session } from './navisworks.ts';

let nw: Session;
before(async () => (nw = await startSession()));
// Closing runs a script after the failed open below; it used to need a document, so Navisworks never got the close request.
after(() => nw?.close());

test('a federation with a missing model fails cleanly, and script still runs with no model open', async () => {
	const model = nw.path('model.ifc');
	const federation = nw.path('federation.nwf');
	copyFileSync(nw.fixture, model);
	await nw.call('apply', { ops: [{ op: 'open', path: model }] });
	await nw.call('export', { out: [{ what: 'nwf', path: federation }] });
	await nw.call('apply', { ops: [{ op: 'open', path: nw.fixture }] });
	for (const file of [model, `${model}.nwc`]) if (existsSync(file)) rmSync(file);

	const opened = await nw.callRaw('apply', { ops: [{ op: 'open', path: federation }] });
	assert.equal(opened.isError, true, opened.text);
	assert.match(opened.text, /error not_found: (unresolved model reference: \S|.*references a file that is missing)/);
	assert.match(opened.text, /next: copy every file the federation references/);

	const script = await nw.callRaw('script', { code: 'return nw.Doc.IsClear ? "no model open" : "model open";' });
	assert.equal(script.isError, false, script.text);
	assert.match(script.text, /^(no model open|model open)$/m);
});
