import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { digest } from '../integrations/output-evidence.mjs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import * as references from '../integrations/reference-stage.mjs';

const runs = fileURLToPath(new URL('../.test-runs/', import.meta.url));
await mkdir(runs, { recursive: true });

test('mandatory reference gate builds real producers and requires all four hash-bound outputs', () => {
  assert.equal(typeof references.generateReferences, 'function', 'missing mandatory generation, not an optional skip');
  assert.equal(typeof references.assertReferencesReady, 'function');
});

test('the Astro config guard rejects absent, missing and stale generated input receipts', async () => {
  const root = await mkdtemp(join(runs, 'receipt-'));
  try {
    await assert.rejects(references.assertReferencesReady(root), /Mandatory reference generation/);
    const hashes = {};
    for (const name of references.referenceNames) {
      const file = join(root, 'src/content/docs/reference/_generated', name);
      await mkdir(dirname(file), { recursive: true });
      await writeFile(file, 'actual test bytes ' + name);
      hashes[name] = digest('actual test bytes ' + name);
    }
    await references.assertReferencesReady(root, JSON.stringify(hashes));
    await writeFile(join(root, 'src/content/docs/reference/_generated/cli.md'), 'changed');
    await assert.rejects(references.assertReferencesReady(root, JSON.stringify(hashes)), /output changed/);
    delete hashes['cli.md'];
    await assert.rejects(references.assertReferencesReady(root, JSON.stringify(hashes)), /incomplete/);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('missing actual source producer fails and preserves old files without a completion receipt', async () => {
  assert.equal(typeof references.generateReferences, 'function');
  const root = await mkdtemp(join(runs, 'stage-'));
  try {
    const site = join(root, 'docs', 'site');
    const old = join(site, 'src/content/docs/reference/_generated/cli.md');
    await mkdir(dirname(old), { recursive: true });
    await writeFile(old, 'old valid reference');
    await assert.rejects(references.generateReferences(root, site), /reference generation/i);
    const { readFile } = await import('node:fs/promises');
    assert.equal(await readFile(old, 'utf8'), 'old valid reference');
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('private-home generation replaces stale OpenAPI even on repeated no-op builds', { timeout: 360_000 }, async () => {
  const site = fileURLToPath(new URL('../', import.meta.url));
  const repository = resolve(site, '../..');
  const api = join(repository, 'src/Nachos.Api/obj/Nachos.Api.json');
  const previous = await readFile(api).catch((error) => {
    if (error.code !== 'ENOENT') throw error;
  });
  try {
    for (let attempt = 0; attempt < 2; attempt++) {
      await writeFile(api, '{"stale":"must not be consumed"}');
      const receipt = await references.generateReferences(repository, site);
      await references.assertReferencesReady(site, receipt);
      const actual = await readFile(api);
      assert.ok(JSON.parse(actual).paths['/v3/keys']);
      assert.deepEqual(await readFile(join(site, 'src/content/docs/reference/_generated/rest-api.json')), actual);
    }
    const configuration = process.env.DOCS_CONFIGURATION ?? 'Debug';
    assert.ok((await stat(join(repository, `artifacts/docsgen/bin/${configuration}/net10.0/DocsGen.dll`))).isFile());
    assert.ok((await stat(join(repository, 'artifacts/docsgen/obj/site-home'))).isDirectory());
    for (const name of ['bin', 'obj']) {
      await assert.rejects(stat(join(repository, 'eng/DocsGen', name)), { code: 'ENOENT' },
        'reference generation must not recreate products or private homes under automation source');
    }
  } catch (error) {
    if (previous) await writeFile(api, previous);
    throw error;
  }
});
