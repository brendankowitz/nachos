import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { after, before, test } from 'node:test';
import { validateEvidence, verifyProvenance } from '../integrations/output-evidence.mjs';
import { useRealReferenceRepository } from './fixture-references.mjs';

const site = fileURLToPath(new URL('../', import.meta.url));
let directory;
let root;
let data;
let inlineRoot;
let inlineData;

async function buildFixture(parent, inline) {
  const target = join(parent, 'site');
  await mkdir(target, { recursive: true });
  for (const name of ['src', 'integrations', 'scripts', 'public', 'astro.config.mjs',
    'package.json', 'package-lock.json', '.npmrc', 'tsconfig.json']) {
    await cp(join(site, name), join(target, name), { recursive: true });
  }
  await cp(join(site, '..', 'assets'), join(parent, 'assets'), { recursive: true });
  await useRealReferenceRepository(target);
  if (inline) {
    const config = await readFile(join(target, 'astro.config.mjs'), 'utf8');
    assert.ok(config.includes('expressiveCode: {'));
    await writeFile(join(target, 'astro.config.mjs'),
      config.replace('expressiveCode: {', 'expressiveCode: { emitExternalStylesheet: false,'));
  }
  const result = spawnSync(process.execPath, [join(target, 'scripts', 'run.mjs'), 'astro', 'build'], {
    cwd: target, encoding: 'utf8', timeout: 150_000, env: process.env,
  });
  assert.ifError(result.error);
  assert.equal(result.status, 0, result.stdout + result.stderr);
  return target;
}

before(async () => {
  await mkdir(join(site, '.test-runs'), { recursive: true });
  directory = await mkdtemp(join(site, '.test-runs', 'style-evidence-'));
  root = await buildFixture(join(directory, 'external'), false);
  data = await verifyProvenance(root);
  inlineRoot = await buildFixture(join(directory, 'inline'), true);
  inlineData = await verifyProvenance(inlineRoot);
}, { timeout: 360_000 });

after(async () => {
  if (directory) await rm(directory, { recursive: true, force: true });
});

test('actual standalone Expressive Code CSS rejects only its evidence hash being changed', async () => {
  const modified = structuredClone(data);
  const css = modified.outputs.find((entry) => /^_astro\/ec\..+\.css$/.test(entry.path));
  assert.ok(css, 'test requires the real emitted EC stylesheet');
  assert.equal(css.evidence.length, 1);
  const bytes = await readFile(join(root, 'dist', css.path));
  css.evidence[0].sha256 = '0'.repeat(64);
  await assert.rejects(validateEvidence(root, join(root, 'dist'), modified), /hash|evidence/i);
  assert.deepEqual(await readFile(join(root, 'dist', css.path)), bytes);
});

test('real inline styles survive HTML serialization', async () => {
  const html = await readFile(join(inlineRoot, 'dist', 'getting-started', 'quick-start', 'index.html'), 'utf8');
  assert.ok([...html.matchAll(/<style\b[^>]*>([\s\S]*?)<\/style>/g)]
    .some((match) => match[1].includes('.expressive-code')), 'real code-block page must contain inline EC CSS');
  assert.ok(!inlineData.outputs.some((entry) => /^_astro\/ec\..+\.css$/.test(entry.path)));
  await validateEvidence(inlineRoot, join(inlineRoot, 'dist'), inlineData);
});

test('a standalone stylesheet cannot claim the inline-style role', async () => {
  const modified = structuredClone(data);
  const css = modified.outputs.find((entry) => /^_astro\/ec\..+\.css$/.test(entry.path));
  css.evidence[0].producer = 'expressive-inline-style';
  await assert.rejects(validateEvidence(root, join(root, 'dist'), modified), /producer|inline|context/i);
});

test('standalone and inline EC styles have distinct closed roles', async () => {
  const css = data.outputs.find((entry) => /^_astro\/ec\..+\.css$/.test(entry.path));
  assert.equal(css.evidence[0].producer, 'expressive-stylesheet');
  const html = inlineData.outputs.find((entry) => entry.path === 'getting-started/quick-start/index.html');
  assert.ok(html.evidence.some((entry) => entry.producer === 'expressive-inline-style'));
});

test('the old ambiguous expressive-style role is rejected', async () => {
  const modified = structuredClone(data);
  modified.outputs.find((entry) => /^_astro\/ec\..+\.css$/.test(entry.path)).evidence[0].producer = 'expressive-style';
  await assert.rejects(validateEvidence(root, join(root, 'dist'), modified), /producer/i);
});

for (const path of ['architecture/index.html']) {
  test(`inline intermediates require their render anchor in ${path}`, async () => {
    const modified = structuredClone(data);
    const output = modified.outputs.find((entry) => entry.path === path);
    const anchor = output.evidence.find((entry) => entry.producer === 'astro-render');
    output.evidence = output.evidence.filter((entry) => entry !== anchor);
    const inline = output.evidence.find((entry) => entry.producer === 'expressive-html');
    inline.sources = [...new Set([...inline.sources, ...anchor.sources])].sort();
    await assert.rejects(validateEvidence(root, join(root, 'dist'), modified), /inline|context|anchor/i);
  });
}

test('non-HTML files cannot masquerade as rendered HTML to admit inline evidence', async () => {
  const modified = structuredClone(data);
  const css = modified.outputs.find((entry) => /^_astro\/ec\..+\.css$/.test(entry.path));
  const html = modified.outputs.find((entry) => entry.path === 'architecture/index.html');
  css.evidence = structuredClone(html.evidence);
  css.packages = structuredClone(html.packages);
  await assert.rejects(validateEvidence(root, join(root, 'dist'), modified), /inline|context|HTML/i);
});
