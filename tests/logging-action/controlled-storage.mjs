// Test-only HTTPS gateway + real disposable SQL/RLS. Never contacts an external service.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { createRequire } from 'node:module';
import { createServer as netServer } from 'node:net';
import { createServer } from 'node:https';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { join, dirname, resolve } from 'node:path';
import { tmpdir } from 'node:os';

export async function controlledStorage() {
  assert.notEqual(process.getuid?.(), 0, 'No OS users are provisioned by this test.');
  const require = createRequire(new URL('../../scripts/logging/package.json', import.meta.url));
  const { default: EmbeddedPostgres } = await import(require.resolve('embedded-postgres'));
  const { default: pg } = await import(require.resolve('pg'));
  const owned = await mkdtemp(join(tmpdir(), 'logging-action-e2e-'));
  const platform = resolve(dirname(require.resolve(`@embedded-postgres/${process.platform}-${process.arch}`)), '..');
  assert.equal(spawnSync(process.execPath, [join(platform, 'scripts/hydrate-symlinks.js')], { timeout: 30000 }).status, 0);
  const socket = netServer(); await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
  const port = socket.address().port; await new Promise(resolve => socket.close(resolve));
  const password = randomBytes(24).toString('hex');
  const engine = new EmbeddedPostgres({ databaseDir: join(owned, 'cluster'), user: 'postgres', password, port,
    persistent: false, createPostgresUser: false, postgresFlags: ['-h', '127.0.0.1', '-c', 'unix_socket_directories='], onLog: () => {}, onError: () => {} });
  const connections = []; const servers = [];
  const close = async () => {
    for (const server of servers) { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
    await Promise.allSettled(connections.map(client => client.end()));
    await engine.stop(); await rm(owned, { recursive: true, force: true });
  };
  try {
    await engine.initialise(); await engine.start();
    const connect = async user => {
      const client = new pg.Client({ host: '127.0.0.1', port, database: 'postgres', user, password,
        connectionTimeoutMillis: 5000, query_timeout: 5000, statement_timeout: 4000 });
      connections.push(client); await client.connect(); return client;
    };
    const admin = await connect('postgres');
    await admin.query(`create role anon nologin; create role authenticated nologin;
      create role synthetic_client login noinherit password '${password}'; grant authenticated to synthetic_client;
      create schema auth; grant usage on schema auth to authenticated;
      create function auth.uid() returns uuid language sql stable as $$ select nullif(current_setting('request.jwt.claim.sub', true),'')::uuid $$;
      grant execute on function auth.uid() to authenticated;`);
    await admin.query(await readFile('database/logging/001_telemetry.sql', 'utf8'));
    const writer = await connect('synthetic_client'); await writer.query('set role authenticated');
    await writer.query("select set_config('request.jwt.claim.sub','10000000-0000-0000-0000-000000000001',false)");
    const effective = (await writer.query('select current_user, rolsuper, rolbypassrls from pg_roles where rolname=current_user')).rows[0];
    assert.deepEqual(effective, { current_user: 'authenticated', rolsuper: false, rolbypassrls: false });
    const cert = join(owned, 'ca.pem'); const key = join(owned, 'key.pem');
    const tls = spawnSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-sha256', '-days', '1',
      '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1', '-addext', 'basicConstraints=critical,CA:TRUE', '-keyout', key, '-out', cert], { timeout: 30000 });
    assert.equal(tls.status, 0, 'Synthetic local certificate must be created.');
    const options = { key: await readFile(key), cert: await readFile(cert) };
    async function serve(handler) {
      const server = createServer(options, handler); servers.push(server);
      await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
      return `https://127.0.0.1:${server.address().port}`;
    }
    return { owned, admin, writer, cert, serve, close };
  } catch (error) { await close(); throw error; }
}
