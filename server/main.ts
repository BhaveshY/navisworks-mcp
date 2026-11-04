import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createInterface } from 'node:readline';
import { Link, stateDir, type Catalog, type Reply } from './link.ts';

const instructions = `Navisworks Manage. Start with status. Lengths mm, areas m2, volumes m3, angles degrees, dates ISO.
read never changes anything; lists return a handle r#. Pass items:"r3", "r3#5", "r3#0-9", "selection", "last", "set:Folder/Name", "id:0:0/2/1" or "?Item.Type = IfcWall" instead of copying rows. Totals are exact for the returned set. Searches return topmost matching items by default; descendants:true includes nested matches.
Batch compatible changes in ONE apply call; open, append, merge, model.remove, refresh, tl.simulate, undo and redo must each run alone (see help): validated first, one undo step, preview:true plans only, "$0" is the handle made by op 0.
export writes files. Long work returns a job j#: call job, and never repeat a write that is still running.
Every error ends with next: do exactly that. help {tool, op} shows an example. script runs C# for anything else; loops must check nw.Cancelled or nw.CheckCancelled(). Cancellation cannot interrupt a blocked native call.`;

const statusOnly: Catalog = {
	tools: [{ name: 'status', description: 'Instance, document, models, units, selection and running jobs. Call first.', inputSchema: { type: 'object', properties: {} }, readOnly: true }],
	examples: {},
};

function loadCatalog(): Catalog {
	try {
		return JSON.parse(readFileSync(join(stateDir, 'catalog.json'), 'utf8')) as Catalog;
	} catch {
		return statusOnly;
	}
}

let catalog = loadCatalog();
const link = new Link();
const inflight = new Map<string | number, () => void>();

const send = (msg: object) => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...msg }) + '\n');

link.onCatalog = (fresh) => {
	if (JSON.stringify(fresh) === JSON.stringify(catalog)) return;
	catalog = fresh;
	send({ method: 'notifications/tools/list_changed' });
};

/// The server owns instance choice, so status gains a `pid` argument that the plugin never sees.
function tools() {
	return catalog.tools.map((t) => {
		const schema = t.inputSchema as { properties?: Record<string, object> };
		const inputSchema = t.name === 'status' ? { ...schema, properties: { ...schema.properties, pid: { type: 'integer', description: 'switch to another running Navisworks' } } } : schema;
		return { name: t.name, description: t.description, inputSchema, annotations: { readOnlyHint: t.readOnly } };
	});
}

function render(r: Reply) {
	if (!r.ok) return { content: [{ type: 'text', text: `error ${r.code}: ${r.message}\nnext: ${r.next}` }], isError: true };
	const content: object[] = [{ type: 'text', text: r.text }];
	if (r.png) content.push({ type: 'image', data: r.png, mimeType: 'image/png' });
	return { content };
}

async function call(id: string | number, name: string, args: Record<string, unknown>) {
	if (name === 'status' && 'pid' in args && (typeof args.pid !== 'number' || !Number.isSafeInteger(args.pid) || args.pid <= 0))
		return render({ ok: false, code: 'bad_args', message: 'pid must be a positive integer', next: 'call status without pid to use the current instance' });
	if (name === 'status' && typeof args.pid === 'number') {
		const { pid, ...rest } = args;
		if (!link.pin(pid as number)) return render({ ok: false, code: 'not_found', message: `no running Navisworks with pid ${pid}`, next: 'call status to see the running instances' });
		args = rest;
	}
	const reply = await link.call(name, args, (cancel) => inflight.set(id, cancel));
	inflight.delete(id);
	return render(reply);
}

const SUPPORTED = ['2025-11-25', '2025-06-18', '2025-03-26', '2024-11-05'];

async function handle(msg: { id?: string | number; method: string; params?: Record<string, any> }) {
	const { id, method, params } = msg;
	switch (method) {
		case 'initialize':
			return {
				protocolVersion: SUPPORTED.includes(params?.protocolVersion) ? params!.protocolVersion : SUPPORTED[0],
				capabilities: { tools: { listChanged: true } },
				serverInfo: { name: 'navisworks', version: '1.0.0' },
				instructions,
			};
		case 'ping':
			return {};
		case 'tools/list':
			return { tools: tools() };
		case 'tools/call':
			return call(id!, params?.name, params?.arguments ?? {});
		case 'notifications/cancelled':
			inflight.get(params?.requestId)?.();
			return undefined;
		default:
			if (method.startsWith('notifications/')) return undefined;
			throw Object.assign(new Error(`method not found: ${method}`), { code: -32601 });
	}
}

createInterface({ input: process.stdin }).on('line', async (line) => {
	if (!line.trim()) return;
	let msg;
	try {
		msg = JSON.parse(line);
	} catch {
		return send({ id: null, error: { code: -32700, message: 'parse error' } });
	}
	if (typeof msg !== 'object' || msg === null || Array.isArray(msg) || typeof msg.method !== 'string') {
		const id = typeof msg === 'object' && msg !== null && 'id' in msg ? msg.id : null;
		return send({ id, error: { code: -32600, message: 'invalid request: expected a JSON-RPC object with a method' } });
	}
	if (msg.method === 'tools/call' && (typeof msg.params?.name !== 'string' || (msg.params.arguments !== undefined && (typeof msg.params.arguments !== 'object' || msg.params.arguments === null || Array.isArray(msg.params.arguments)))))
		return send({ id: msg.id ?? null, error: { code: -32602, message: 'tools/call needs params.name (string) and optional params.arguments (object)' } });
	try {
		const result = await handle(msg);
		if (msg.id !== undefined) send({ id: msg.id, result });
	} catch (e) {
		if (msg.id !== undefined) send({ id: msg.id, error: { code: (e as { code?: number }).code ?? -32603, message: (e as Error).message } });
	}
});
