import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cp, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { files } from '../integrations/site-files.mjs';
import { useRealReferenceRepository } from './fixture-references.mjs';

const site = fileURLToPath(new URL('../', import.meta.url));
const browser = process.env.PLAYWRIGHT_BROWSERS_PATH ?? join(site, '.playwright');
const runs = join(site, '.test-runs');
await mkdir(runs, { recursive: true });

function build(root, env = {}) {
  const result = spawnSync(process.execPath, [join(root, 'scripts', 'run.mjs'), 'astro', 'build'], {
    cwd: root, encoding: 'utf8', timeout: 300_000,
    env: { ...process.env, PLAYWRIGHT_BROWSERS_PATH: browser, ASTRO_TELEMETRY_DISABLED: '1', ...env },
  });
  assert.ifError(result.error && new Error(`${result.error.message}\n${result.stdout}\n${result.stderr}`));
  return { status: result.status, output: result.stdout + result.stderr };
}

async function fixture(action) {
  const directory = await mkdtemp(join(runs, 'site-'));
  const root = join(directory, 'site');
  await mkdir(root);
  try {
    for (const name of ['src', 'integrations', 'scripts', 'public', 'astro.config.mjs', 'package.json', 'package-lock.json', '.npmrc', 'tsconfig.json']) {
      await cp(join(site, name), join(root, name), { recursive: true });
    }
    await cp(join(site, '..', 'assets'), join(directory, 'assets'), { recursive: true });
    await useRealReferenceRepository(root);
    return await action(root);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

test('real references build with rendered Mermaid, search and generated reference pages', { timeout: 450_000 }, async () => {
  const result = build(site);
  assert.equal(result.status, 0, result.output);
  assert.match(result.output, /All.*links.*valid|No.*broken|successfully validated/i);
  assert.match(result.output, /Verified rendered links/);
  const landing = await readFile(join(site, 'dist', 'index.html'), 'utf8');
  assert.match(landing, /Memory for stateful agents/);
  assert.match(landing, /\/nachos\//);
  const architecture = await readFile(join(site, 'dist', 'architecture', 'index.html'), 'utf8');
  assert.match(architecture, /<svg[^>]+.*mermaid/s);
  assert.ok(!architecture.includes('language-mermaid'), 'diagram must render, not remain a code block');
  await readFile(join(site, 'dist', 'pagefind', 'pagefind.js'));
  const assets = await readdir(join(site, 'dist', '_astro'));
  assert.ok(!assets.some((name) => /mermaid|elk/i.test(name)), 'no renderer library chunks may ship');
  const outputs = (await files(join(site, 'dist'))).map((file) => relative(join(site, 'dist'), file).replaceAll('\\', '/'));
  for (const path of ['reference/cli/index.html', 'reference/options/index.html', 'reference/compatibility/index.html',
    'reference/rest-api/index.html', 'reference/rest-api/operations/v3admingrants/index.html']) {
    assert.ok(outputs.includes(path), `missing real reference page ${path}`);
  }
  assert.ok(!outputs.some((path) => path.includes('/_generated/')), 'generated source must not become duplicate pages');
  assert.match(await readFile(join(site, 'dist/reference/cli/index.html'), 'utf8'), /--signing-secret-env/);
  assert.match(await readFile(join(site, 'dist/reference/options/index.html'), 'utf8'), /MaxCustomInstructionsTokens/);
  const compatibility = await readFile(join(site, 'dist/reference/compatibility/index.html'), 'utf8');
  assert.match(compatibility, /not-run/);
  assert.match(compatibility, /staged-501/);
  const api = await readFile(join(site, 'dist/reference/rest-api/operations/v3admingrants/index.html'), 'utf8');
  assert.match(api, /204/);
  assert.match(api, /not-assessed/);
  for (const content of [api, await readFile(join(site, 'dist/reference/rest-api/index.html'), 'utf8')]) {
    assert.equal((content.match(/aria-label="Declaration evidence only"/g) ?? []).length, 1);
    assert.match(content, /conformance\.<\/strong>\s+SDK conformance/);
  }
});

test('real site build fails on a missing internal Markdown link', { timeout: 150_000 }, async () => {
  await fixture(async (root) => {
    await writeFile(join(root, 'src', 'content', 'docs', 'broken.mdx'), '---\ntitle: Broken\n---\n[Missing](/nachos/does-not-exist/)\n');
    const result = build(root);
    assert.notEqual(result.status, 0, result.output);
    assert.match(result.output, /Links validation failed/);
    assert.match(result.output, /does-not-exist/);
  });

  test('a missing producer cannot modify the old references', { timeout: 150_000 }, async () => {
    await fixture(async (root) => {
      const runner = join(root, 'scripts/run.mjs');
      const script = await readFile(runner, 'utf8');
      await writeFile(runner, script.replace(/const repository = .*;/,
        `const repository = ${JSON.stringify(join(root, 'missing-repository'))};`));
      const reference = join(root, 'src/content/docs/reference/_generated/cli.md');
      const old = await readFile(reference);
      const result = build(root);
      assert.notEqual(result.status, 0);
      assert.match(result.output, /Mandatory reference generation requires all actual source producers/);
      assert.deepEqual(await readFile(reference), old);
    });
  });

  test('direct Astro builds cannot substitute stale references for the mandatory runner', { timeout: 150_000 }, async () => {
    const result = spawnSync(process.execPath, [join(site, 'node_modules/astro/bin/astro.mjs'), 'build'], {
      cwd: site, encoding: 'utf8', timeout: 120_000,
      env: { ...process.env, NACHOS_REFERENCE_RECEIPT: '' },
    });
    assert.ifError(result.error);
    assert.notEqual(result.status, 0);
    assert.match(result.stdout + result.stderr, /Mandatory reference generation required/);
  });
});

test('real site build fails on malformed Mermaid instead of publishing raw code', { timeout: 150_000 }, async () => {
  await fixture(async (root) => {
    await writeFile(join(root, 'src', 'content', 'docs', 'broken.mdx'), '---\ntitle: Broken\n---\n```mermaid\nflowchart LR\nA[broken\n```\n');
    const result = build(root);
    assert.notEqual(result.status, 0, result.output);
    assert.match(result.output, /Parse error/);
  });

});

test('custom-domain build follows the configured base', { timeout: 570_000 }, async () => {
  const env = { ...process.env, DOCS_SITE: 'https://docs.example.org', DOCS_BASE: '/' };
  const result = build(site, env);
  assert.equal(result.status, 0, result.output);
  const html = await readFile(join(site, 'dist', 'index.html'), 'utf8');
  assert.ok(!html.includes('href="/nachos/'), 'authored links must follow the configured base');
  assert.match(html, /href="\/concepts\/workspaces-and-peers\/"/);
});

test('site renders Mermaid with image/font tooling imports explicitly rejected', { timeout: 150_000 }, async () => {
  await fixture(async (root) => {
    const guard = new URL('./reject-image-font-tools.mjs', import.meta.url).href;
    const result = build(root, { NODE_OPTIONS: `--import=${guard}` });
    assert.equal(result.status, 0, result.output);
    const html = await readFile(join(root, 'dist', 'architecture', 'index.html'), 'utf8');
    assert.match(html, /<svg[^>]+.*mermaid/s);
    assert.ok(!html.includes('language-mermaid'));
  });
});
