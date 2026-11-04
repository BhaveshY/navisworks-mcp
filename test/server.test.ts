import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { join } from 'node:path';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { createInterface } from 'node:readline';
import { test } from 'node:test';

test('malformed JSON-RPC gets an error reply and the server keeps serving', async () => {
	const server = spawn(process.execPath, [join(import.meta.dirname, '..', 'server', 'main.ts')], { stdio: ['pipe', 'pipe', 'inherit'], env: { ...process.env, LOCALAPPDATA: mkdtempSync(join(tmpdir(), 'nmcp-protocol-')) } });
	const replies = createInterface({ input: server.stdout })[Symbol.asyncIterator]();
	const next = async () => JSON.parse((await replies.next()).value as string);
	try {
		for (const bad of ['null', '42', '[]', '{"id":7}', '{"jsonrpc":"2.0","id":8,"method":"tools/call","params":null}', '{"id":10,"method":"tools/call","params":{"name":"status","arguments":[]}}']) {
			server.stdin.write(bad + '\n');
			const reply = await next();
			assert.ok(reply.error, `${bad} gets an error, got ${JSON.stringify(reply)}`);
		}
		server.stdin.write('{not json\n');
		assert.equal((await next()).error.code, -32700);
		server.stdin.write('{"jsonrpc":"2.0","id":9,"method":"ping"}\n');
		assert.deepEqual(await next(), { jsonrpc: '2.0', id: 9, result: {} });
		assert.equal(server.exitCode, null);
	} finally {
		server.kill();
	}
});
