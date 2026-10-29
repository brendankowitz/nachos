import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { after, before, test } from 'node:test';
import { archiveTree, snapshotTree, verifyArchive } from './docs-artifact.mjs';
import { checksum, mutateArchive, mutationNames, records } from './docs-artifact.fixtures.mjs';

let fixture;
let original;
let seal;
before(async () => {
  const root = fileURLToPath(new URL('../../', import.meta.url));
  const runs = join(root, '.github/scripts/.test-runs');
  await mkdir(runs, { recursive: true });
  fixture = await mkdtemp(join(runs, 'ustar-'));
  const output = join(fixture, 'dist');
  await mkdir(join(output, '.nachos'), { recursive: true });
  await mkdir(join(output, 'reference/api'), { recursive: true });
  await writeFile(join(output, 'index.html'), '<h1>Actual first-party fixture</h1>');
  await writeFile(join(output, '.nachos/metadata.json'), '{"fixture":true}');
  await writeFile(join(output, 'reference/api/index.html'), '<p>Nested file</p>');
  seal = await snapshotTree(output);
  const archive = join(fixture, 'original.tar');
  await archiveTree(output, archive, seal);
  original = await readFile(archive);
});
after(async () => { if (fixture) await rm(fixture, { recursive: true, force: true }); });

test('native USTAR retains every file, hidden metadata and only its expected directories', async () => {
  await verifyArchive(join(fixture, 'original.tar'), seal);
  const { entries } = records(original);
  assert.equal(entries.filter((entry) => entry.type === '0').length, 3);
  assert.equal(entries.filter((entry) => entry.type === '5').length, 4);
});

test('unused device fields may be zero-filled on regular-file and directory USTAR records', async () => {
  const bytes = Buffer.from(original);
  for (const { offset } of records(bytes).entries) {
    const header = bytes.subarray(offset, offset + 512);
    header.fill(0, 329, 345);
    checksum(header);
  }
  const path = join(fixture, 'zero-unused-device-fields.tar');
  await writeFile(path, bytes);
  await verifyArchive(path, seal);
});

for (const mutation of mutationNames) {
  test(`closed USTAR rejects ${mutation} even when sealed file payloads can remain intact`, async () => {
    const path = join(fixture, mutation + '.tar');
    await writeFile(path, mutateArchive(original, mutation));
    await assert.rejects(verifyArchive(path, seal), undefined, `accepted ${mutation}`);
    assert.deepEqual(await readFile(join(fixture, 'original.tar')), original);
  });
}

test('archive verification preserves hidden metadata and rejects every changed tree member', async () => {
  const root = fileURLToPath(new URL('../../', import.meta.url));
  const runs = join(root, '.github/scripts/.test-runs');
  await mkdir(runs, { recursive: true });
  const dir = await mkdtemp(join(runs, 'artifact-'));
  try {
    const module = await import('./docs-artifact.mjs').catch(() => ({}));
    assert.equal(typeof module.snapshotTree, 'function');
    const output = join(dir, 'dist');
    await mkdir(join(output, '.nachos'), { recursive: true });
    await writeFile(join(output, 'index.html'), '<h1>real test bytes</h1>');
    await writeFile(join(output, '.nachos/metadata.json'), '{"test":"metadata bytes"}');
    const seal = await module.snapshotTree(output);
    assert.equal(seal.length, 2);
    assert.ok(seal.some((entry) => entry.path === '.nachos/metadata.json'));
    const archive = join(dir, 'artifact.tar');
    await module.archiveTree(output, archive, seal);
    await module.verifyArchive(archive, seal);
    const bytes = await readFile(archive);
    bytes[512] ^= 1;
    await writeFile(archive, bytes);
    await assert.rejects(module.verifyArchive(archive, seal));
    await writeFile(join(output, 'index.html'), 'changed');
    await assert.rejects(module.archiveTree(output, archive, seal), /changed/);
  } finally { await rm(dir, { recursive: true, force: true }); }
});
