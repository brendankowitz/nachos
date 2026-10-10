// Smoke conformance: the unmodified @honcho-ai/sdk, used through its documented API, against a running Nachos.
// Configured only by the environment file that eng/conformance/start-nachos-for-conformance.sh (Linux/macOS) or
// eng/conformance/Start-NachosForConformance.ps1 (Windows) writes; `--run` / `-Run` exports it. To run by hand in bash:
// `set -a; . <file>; set +a; npm ci; npm test`.
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { test } from 'node:test';
import { AuthenticationError, Honcho, ServerError } from '@honcho-ai/sdk';

const env = process.env;
for (const name of ['NACHOS_BASE_URL', 'NACHOS_ADMIN_KEY']) {
  if (!env[name]) {
    throw new Error(`${name} is not set: run eng/conformance/start-nachos-for-conformance.sh --run`);
  }
}
const authMode = env.NACHOS_AUTH_MODE ?? 'disabled';
const authWorkspace = env.NACHOS_AUTH_WORKSPACE ?? 'conformance-auth';
const authPeer = env.NACHOS_AUTH_PEER ?? 'member';

const client = (workspaceId, { apiKey = env.NACHOS_ADMIN_KEY, ...options } = {}) =>
  new Honcho({ baseURL: env.NACHOS_BASE_URL, workspaceId, apiKey, timeout: 10_000, ...options });

// A fresh workspace per test: scenarios never see each other's rows.
const freshWorkspace = () => `ts-${randomUUID().replaceAll('-', '').slice(0, 12)}`;

test('workspace get-or-create and metadata', async () => {
  const honcho = client(freshWorkspace());
  // The SDK creates the workspace lazily on the first call, so the first metadata read is the get-or-create.
  assert.deepEqual(await honcho.getMetadata(), {});
  await honcho.setMetadata({ owner: 'conformance', n: 1 });
  assert.deepEqual(await honcho.getMetadata(), { owner: 'conformance', n: 1 });
  await honcho.setMetadata({ n: 2 });
  assert.deepEqual(await honcho.getMetadata(), { n: 2 });
});

test('peer get-or-create and metadata', async () => {
  const honcho = client(freshWorkspace());
  const created = await honcho.peer('alice', { metadata: { role: 'user' } });
  assert.equal(created.id, 'alice');
  const again = await honcho.peer('alice');
  assert.equal(again.id, 'alice');
  assert.deepEqual(await again.getMetadata(), { role: 'user' });
  await again.setMetadata({ role: 'admin', n: 3 });
  assert.deepEqual(await (await honcho.peer('alice')).getMetadata(), { role: 'admin', n: 3 });
});

test('session get-or-create and metadata', async () => {
  const honcho = client(freshWorkspace());
  const created = await honcho.session('s1', { metadata: { topic: 'intro' } });
  assert.equal(created.id, 's1');
  const again = await honcho.session('s1');
  assert.deepEqual(await again.getMetadata(), { topic: 'intro' });
  await again.setMetadata({ topic: 'budget' });
  assert.deepEqual(await (await honcho.session('s1')).getMetadata(), { topic: 'budget' });
});

test('list auto-paginates over 25 sessions', async () => {
  const honcho = client(freshWorkspace());
  const expected = new Set(Array.from({ length: 25 }, (_, n) => `s${String(n).padStart(2, '0')}`));
  for (const id of expected) {
    await honcho.session(id, { metadata: { batch: 'b1' } });
  }

  const page = await honcho.sessions({ filters: { metadata: { batch: 'b1' } }, size: 10 });
  assert.deepEqual([page.total, page.pages, page.items.length], [25, 3, 10]);
  // Iterating the page follows the remaining pages on its own.
  const seen = new Set();
  for await (const session of page) {
    seen.add(session.id);
  }
  assert.deepEqual(seen, expected);

  // This SDK's unfiltered list is accepted by Nachos, as honcho-ai 2.5.1's empty-body list is (test_list_without_filters).
  assert.deepEqual((await honcho.sessions()).items.length, 25);
  assert.ok((await honcho.workspaces()).total >= 1);
});

test('session peers add, set and remove; peer configuration get and set', async () => {
  const honcho = client(freshWorkspace());
  const [alice, bob, carol] = await Promise.all(['alice', 'bob', 'carol'].map((id) => honcho.peer(id)));
  const session = await honcho.session('s1');
  const members = async () => new Set((await session.peers()).map((peer) => peer.id));

  await session.addPeers([alice, bob]);
  assert.deepEqual(await members(), new Set(['alice', 'bob']));
  await session.setPeers([bob, carol]);
  assert.deepEqual(await members(), new Set(['bob', 'carol']));
  await session.removePeers([bob]);
  assert.deepEqual(await members(), new Set(['carol']));

  // Peer-level configuration.
  assert.equal((await alice.getConfiguration()).observeMe ?? null, null);
  await alice.setConfiguration({ observeMe: false });
  assert.equal((await alice.getConfiguration()).observeMe, false);

  // Session-level peer configuration.
  await session.addPeers([alice]);
  await session.setPeerConfiguration(alice, { observeOthers: true });
  assert.equal((await session.getPeerConfiguration(alice)).observeOthers, true);
  await session.setPeerConfiguration(alice, { observeMe: false });
  assert.equal((await session.getPeerConfiguration(alice)).observeMe, false);
});

test('batch messages: create, list, get and metadata update', async () => {
  const honcho = client(freshWorkspace());
  const alice = await honcho.peer('alice');
  const session = await honcho.session('s1');
  await session.addPeers([alice]);

  const created = await session.addMessages(
    Array.from({ length: 20 }, (_, n) => alice.message(`message ${n}`, { metadata: { n } })),
  );
  assert.deepEqual(created.map((m) => m.content), Array.from({ length: 20 }, (_, n) => `message ${n}`));
  assert.equal(new Set(created.map((m) => m.id)).size, 20);

  const listed = await (await session.messages({ size: 7 })).toArray();
  assert.deepEqual(new Set(listed.map((m) => m.id)), new Set(created.map((m) => m.id)));

  const fetched = await session.getMessage(created[3].id);
  assert.deepEqual([fetched.id, fetched.content, fetched.peerId], [created[3].id, 'message 3', 'alice']);

  await session.updateMessage(created[3].id, { edited: true });
  assert.deepEqual((await session.getMessage(created[3].id)).metadata, { edited: true });
});

test('idempotent replay returns the same batch', async () => {
  // @honcho-ai/sdk takes extra request headers per client (`defaultHeaders`), so a dedicated client replays one batch
  // under one Idempotency-Key. Spec 9.1: the replay returns the stored response and performs no second mutation.
  const honcho = client(freshWorkspace(), { defaultHeaders: { 'Idempotency-Key': `ts-${randomUUID()}` } });
  const alice = await honcho.peer('alice');
  const session = await honcho.session('s1');
  await session.addPeers([alice]);

  const batch = Array.from({ length: 3 }, (_, n) => alice.message(`replayed ${n}`));
  const first = await session.addMessages(batch);
  const second = await session.addMessages(batch);

  assert.deepEqual(second.map((m) => m.id), first.map((m) => m.id));
  assert.equal((await (await session.messages()).toArray()).length, 3);

  // Contrast: without the header the same batch is stored twice (spec 9.1: such requests behave exactly like Honcho),
  // so the assertions above are not satisfied by a server that merely ignores repeated batches.
  const plain = client(freshWorkspace());
  const plainAlice = await plain.peer('alice');
  const plainSession = await plain.session('s2');
  await plainSession.addPeers([plainAlice]);
  const unkeyed = Array.from({ length: 3 }, (_, n) => plainAlice.message(`unkeyed ${n}`));
  const unkeyedFirst = await plainSession.addMessages(unkeyed);
  const unkeyedSecond = await plainSession.addMessages(unkeyed);
  assert.equal(new Set([...unkeyedFirst, ...unkeyedSecond].map((m) => m.id)).size, 6);
  assert.equal((await (await plainSession.messages()).toArray()).length, 6);
});

test('an unimplemented call surfaces 501 promptly', async () => {
  const honcho = client(freshWorkspace(), { timeout: 3_000 });
  const alice = await honcho.peer('alice');

  const started = performance.now();
  await assert.rejects(alice.chat('What do you know about me?'), (error) => {
    assert.ok(error instanceof ServerError, `expected ServerError, got ${error?.constructor?.name}`);
    assert.equal(error.status, 501);
    return true;
  });
  assert.ok(performance.now() - started < 3_000);
});

test(
  'a peer-scoped key reads member messages but cannot list workspaces',
  { skip: authMode === 'enforced' ? false : 'awaiting auth/Task 11 publication' },
  async () => {
    const admin = client(authWorkspace);
    const member = await admin.peer(authPeer);
    const session = await admin.session(`ts-auth-${randomUUID().slice(0, 8)}`);
    await session.addPeers([member]);
    await session.addMessages([member.message('visible to the member')]);

    const scoped = client(authWorkspace, { apiKey: env.NACHOS_PEER_KEY });
    const scopedSession = await scoped.session(session.id);
    assert.deepEqual((await (await scopedSession.messages()).toArray()).map((m) => m.content), ['visible to the member']);

    await assert.rejects(scoped.workspaces(), (error) => {
      assert.ok(error instanceof AuthenticationError, `expected AuthenticationError, got ${error?.constructor?.name}`);
      assert.equal(error.status, 401);
      return true;
    });
  },
);
