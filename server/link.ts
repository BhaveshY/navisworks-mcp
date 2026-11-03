import { readdirSync, readFileSync, rmSync } from 'node:fs';
import { connect, type Socket } from 'node:net';
import { join } from 'node:path';

export const stateDir = join(process.env.LOCALAPPDATA ?? '', 'navisworks-mcp');

export type Catalog = {
	tools: { name: string; description: string; inputSchema: object; readOnly: boolean }[];
	examples: Record<string, string>;
};

export type Reply =
	| { ok: true; text: string; png?: string; job?: string }
	| { ok: false; code: string; message: string; next: string };

type Instance = { pid: number; started: string; pipe: string };

function alive(pid: number) {
	try {
		process.kill(pid, 0);
		return true;
	} catch (e) {
		return (e as NodeJS.ErrnoException).code === 'EPERM';
	}
}

/// Live Navisworks instances, newest first. Registration files of dead processes are removed.
export function instances(): Instance[] {
	const dir = join(stateDir, 'instances');
	let files: string[];
	try {
		files = readdirSync(dir).filter((f) => f.endsWith('.json'));
	} catch {
		return [];
	}
	const live: Instance[] = [];
	for (const f of files) {
		const pid = Number(f.slice(0, -5));
		if (!alive(pid)) {
			rmSync(join(dir, f), { force: true });
			continue;
		}
		try {
			live.push(JSON.parse(readFileSync(join(dir, f), 'utf8')) as Instance);
		} catch {
			// Being written right now; it shows up on the next call.
		}
	}
	return live.sort((a, b) => b.started.localeCompare(a.started));
}

const fail = (code: string, message: string, next: string): Reply => ({ ok: false, code, message, next });

/// One pipe to one Navisworks. Its requests settle when it closes, whatever replaced it since.
class Connection {
	readonly ready: Promise<void>;
	closed = false;
	private readonly socket: Socket;
	private readonly pending = new Map<number, (r: Reply) => void>();
	private seq = 0;

	constructor(instance: Instance, onCatalog: (catalog: Catalog) => void) {
		this.socket = connect(`\\\\.\\pipe\\${instance.pipe}`);
		this.ready = new Promise((resolve, reject) => {
			const timer = setTimeout(() => {
				reject(new Error('Navisworks accepted the connection but sent no hello within 10 s'));
				this.socket.destroy();
			}, 10000);
			let buffer = '';
			this.socket.setEncoding('utf8');
			this.socket.on('data', (chunk: string) => {
				buffer += chunk;
				let nl: number;
				while ((nl = buffer.indexOf('\n')) >= 0) {
					const msg = JSON.parse(buffer.slice(0, nl));
					buffer = buffer.slice(nl + 1);
					if (msg.hello) {
						clearTimeout(timer);
						onCatalog(msg.hello.catalog);
						resolve();
						continue;
					}
					this.pending.get(msg.id)?.(msg);
					this.pending.delete(msg.id);
				}
			});
			this.socket.on('error', reject);
			this.socket.on('close', () => {
				this.closed = true;
				clearTimeout(timer);
				reject(new Error('Navisworks closed the connection before it was ready'));
				for (const settle of this.pending.values())
					settle(fail('disconnected', 'Navisworks closed the connection during the call; whether it took effect is unknown', 'call status, check the result, and repeat only if it did not happen'));
				this.pending.clear();
			});
		});
		this.ready.catch(() => {});
	}

	send(tool: string, args: object, onSent: (cancel: () => void) => void): Promise<Reply> {
		if (this.closed) return Promise.resolve(fail('not_running', 'Navisworks closed the connection before the call was sent; nothing ran', 'call status, then repeat the call'));
		const id = ++this.seq;
		return new Promise<Reply>((resolve) => {
			this.pending.set(id, resolve);
			this.socket.write(JSON.stringify({ id, tool, args, wait: 25000 }) + '\n');
			onSent(() => this.socket.write(JSON.stringify({ cancel: id }) + '\n'));
		});
	}

	close() {
		this.closed = true;
		this.socket.destroy();
	}
}

/// The connection to the pinned Navisworks instance, opened lazily and reopened after it closes.
export class Link {
	pinned = process.env.NAVISWORKS_MCP_PID ? Number(process.env.NAVISWORKS_MCP_PID) : undefined;
	onCatalog: (catalog: Catalog) => void = () => {};
	private connection: Connection | undefined;

	pin(pid: number) {
		if (!instances().some((i) => i.pid === pid)) return false;
		this.pinned = pid;
		this.connection?.close();
		this.connection = undefined;
		return true;
	}

	private target(): Instance | Reply {
		const all = instances();
		if (this.pinned !== undefined) {
			const pinned = all.find((i) => i.pid === this.pinned);
			return pinned ?? fail('not_running', `Navisworks pid ${this.pinned} is not running`, all.length ? `call status {pid:${all[0]!.pid}} to use the running instance` : 'start Navisworks Manage, then call status');
		}
		return all[0] ?? fail('not_running', 'Navisworks is not running', 'start Navisworks Manage (the plugin loads with it), then call status');
	}

	async call(tool: string, args: object, onSent: (cancel: () => void) => void = () => {}): Promise<Reply> {
		let connection = this.connection;
		if (!connection || connection.closed) {
			const t = this.target();
			if ('ok' in t) return t;
			connection = this.connection = new Connection(t, (catalog) => this.onCatalog(catalog));
		}
		try {
			await connection.ready;
		} catch (e) {
			if (this.connection === connection) this.connection = undefined;
			return fail('not_running', `cannot reach Navisworks: ${(e as Error).message}`, 'wait for Navisworks to finish starting, then call status');
		}
		return connection.send(tool, args, onSent);
	}
}
