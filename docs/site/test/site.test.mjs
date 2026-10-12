import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cp, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { verifyProvenance } from '../integrations/output-evidence.mjs';
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

test('real references build with rendered Mermaid, search and exact distribution provenance', { timeout: 450_000 }, async () => {
  const result = build(site);
  assert.equal(result.status, 0, result.output);
  assert.match(result.output, /All.*links.*valid|No.*broken|successfully validated/i);
  const landing = await readFile(join(site, 'dist', 'index.html'), 'utf8');
  assert.match(landing, /Memory for stateful agents/);
  assert.match(landing, /\/nachos\//);
  const architecture = await readFile(join(site, 'dist', 'architecture', 'index.html'), 'utf8');
  assert.match(architecture, /<svg[^>]+.*mermaid/s);
  assert.ok(!architecture.includes('language-mermaid'), 'diagram must render, not remain a code block');
  await readFile(join(site, 'dist', 'pagefind', 'pagefind.js'));
  const manifest = JSON.parse(await readFile(join(site, 'dist', '.nachos', 'bundle-modules.json')));
  for (const name of ['@astrojs/starlight', '@pagefind/default-ui', 'pagefind', 'mermaid', '@expressive-code/plugin-shiki']) {
    assert.ok(manifest.some((entry) => entry.package === name), `missing shipping package ${name}`);
  }
  for (const name of ['elkjs', 'rehype-mermaid', 'playwright', 'sharp', 'starlight-links-validator']) {
    assert.ok(!manifest.some((entry) => entry.package === name), `build-only package mislabeled: ${name}`);
  }
  const assets = await readdir(join(site, 'dist', '_astro'));
  assert.ok(!assets.some((name) => /mermaid|elk/i.test(name)), 'no renderer library chunks may ship');
  const evidence = await verifyProvenance(site);
  const standalone = spawnSync(process.execPath, [join(site, 'scripts/verify-provenance.mjs')], {
    cwd: site, encoding: 'utf8', timeout: 120_000, env: process.env,
  });
  assert.ifError(standalone.error);
  assert.equal(standalone.status, 0, standalone.stdout + standalone.stderr);
  for (const path of ['reference/cli/index.html', 'reference/options/index.html', 'reference/compatibility/index.html',
    'reference/rest-api/index.html', 'reference/rest-api/operations/v3admingrants/index.html']) {
    assert.ok(evidence.outputs.some((entry) => entry.path === path), `missing real reference page ${path}`);
  }
  assert.ok(!evidence.outputs.some((entry) => entry.path.includes('/_generated/')), 'generated source must not become duplicate pages');
  assert.match(await readFile(join(site, 'dist/reference/cli/index.html'), 'utf8'), /--signing-secret-env/);
  assert.match(await readFile(join(site, 'dist/reference/options/index.html'), 'utf8'), /MaxCustomInstructionsTokens/);
  const compatibility = await readFile(join(site, 'dist/reference/compatibility/index.html'), 'utf8');
  assert.match(compatibility, /not-run/);
  assert.match(compatibility, /staged-501/);
  const api = await readFile(join(site, 'dist/reference/rest-api/operations/v3admingrants/index.html'), 'utf8');
  assert.match(api, /204/);
  assert.match(api, /not-assessed/);
  assert.ok(evidence.sources.some((entry) => entry.package === 'starlight-openapi'));
  for (const module of ['index.ts', 'libs/vite.ts']) {
    assert.ok(evidence.sources.some((entry) => entry.path === `npm/node_modules/starlight-openapi/${module}`),
      `missing trusted OpenAPI construction/data source ${module}`);
  }
  assert.ok(evidence.inputs.some((entry) => entry.path.endsWith('/_generated/rest-api.json')));
  assert.ok(evidence.sources.some((entry) => entry.path.endsWith('/components-internals/Icons.js')),
    'inline icon data must be bound, not just its importing Astro template');
  const rendered = evidence.outputs.find((entry) => entry.path === 'architecture/index.html');
  assert.ok(rendered.evidence.some((entry) => entry.producer === 'mermaid-svg'));
  assert.ok(rendered.evidence.some((entry) => entry.producer === 'expressive-html'));
  assert.ok(rendered.packages.some((entry) => entry.package === 'mermaid'));
  const search = evidence.outputs.filter((entry) => entry.path.startsWith('pagefind/'));
  assert.ok(search.length > 0);
  assert.ok(search.every((entry) => entry.packages.some((pkg) => pkg.package === 'mermaid')),
    'search index inherits embedded page contributors conservatively');
  const backend = `@pagefind/${process.platform === 'win32' ? 'windows' : process.platform}-${process.arch}`;
  assert.ok(search.every((entry) => entry.packages.some((pkg) => pkg.package === backend)),
    'Pagefind native response supplies the embedded JS/CSS/WASM, not just execution');
  for (const name of ['pagefind-component-ui.css', 'pagefind-component-ui.js', 'wasm.unknown.pagefind']) {
    assert.ok((await readFile(join(site, 'dist', 'pagefind', name))).length > 0, `native output was not flushed: ${name}`);
  }
});

test('real site build fails on a missing internal Markdown link', { timeout: 150_000 }, async () => {
  await fixture(async (root) => {
    await writeFile(join(root, 'src', 'content', 'docs', 'broken.mdx'), '---\ntitle: Broken\n---\n[Missing](/nachos/does-not-exist/)\n');
    const result = build(root);
    assert.notEqual(result.status, 0, result.output);
    assert.match(result.output, /Links validation failed/);
    assert.match(result.output, /does-not-exist/);
    await assert.rejects(readFile(join(root, 'dist', '.nachos', 'output-provenance.v1.json')), { code: 'ENOENT' });
  });

  test('a missing producer cannot bless stale site metadata or modify the old references', { timeout: 150_000 }, async () => {
    await fixture(async (root) => {
      const runner = join(root, 'scripts/run.mjs');
      const script = await readFile(runner, 'utf8');
      await writeFile(runner, script.replace(/const repository = .*;/,
        `const repository = ${JSON.stringify(join(root, 'missing-repository'))};`));
      const reference = join(root, 'src/content/docs/reference/_generated/cli.md');
      const old = await readFile(reference);
      await mkdir(join(root, 'dist/.nachos'), { recursive: true });
      await writeFile(join(root, 'dist/.nachos/output-provenance.v1.json'), '{"stale":true}');
      await writeFile(join(root, 'dist/.nachos/bundle-modules.json'), '[]');
      const result = build(root);
      assert.notEqual(result.status, 0);
      assert.match(result.output, /Mandatory reference generation requires all actual source producers/);
      assert.deepEqual(await readFile(reference), old);
      await assert.rejects(readFile(join(root, 'dist/.nachos/output-provenance.v1.json')), { code: 'ENOENT' });
      await assert.rejects(readFile(join(root, 'dist/.nachos/bundle-modules.json')), { code: 'ENOENT' });
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
    await assert.rejects(readFile(join(root, 'dist', '.nachos', 'output-provenance.v1.json')), { code: 'ENOENT' });
  });

});

test('a late failing real build hook cannot leave success-shaped output evidence', { timeout: 150_000 }, async () => {
  await fixture(async (root) => {
    const config = await readFile(join(root, 'astro.config.mjs'), 'utf8');
    await writeFile(join(root, 'astro.config.mjs'), config.replace('provenance.integration,',
      `provenance.integration,
      { name: 'late-failure', hooks: { 'astro:build:done': () => { throw new Error('deliberate late failure'); } } },`));
    const result = build(root);
    assert.notEqual(result.status, 0, result.output);
    assert.match(result.output, /deliberate late failure/);
    await assert.rejects(readFile(join(root, 'dist', '.nachos', 'output-provenance.v1.json')), { code: 'ENOENT' });
    await assert.rejects(readFile(join(root, 'dist', '.nachos', 'bundle-modules.json')), { code: 'ENOENT' });
  });
});

test('custom-domain build passes the documented Node and C# verification commands', { timeout: 570_000 }, async () => {
  const env = { ...process.env, DOCS_SITE: 'https://docs.example.org', DOCS_BASE: '/' };
  const checker = spawnSync('dotnet', ['build', 'eng/license-check', '-c', 'Release', '--no-restore',
    '-m:1', '-nr:false', '-p:UseSharedCompilation=false'], {
    cwd: resolve(site, '../..'), encoding: 'utf8', timeout: 120_000, env,
  });
  assert.ifError(checker.error);
  assert.equal(checker.status, 0, checker.stdout + checker.stderr);
  const result = build(site, env);
  assert.equal(result.status, 0, result.output);
  const html = await readFile(join(site, 'dist', 'index.html'), 'utf8');
  assert.ok(!html.includes('href="/nachos/'), 'authored links must follow the configured base');
  assert.match(html, /href="\/concepts\/workspaces-and-peers\/"/);

  const readme = await readFile(join(site, 'README.md'), 'utf8');
  const instructions = readme.slice(readme.indexOf('After the final build:'));
  const commands = [...instructions.matchAll(/```powershell\r?\n([\s\S]*?)```/g)];
  assert.equal(commands.length, 2, 'execute both documented verifier blocks');
  const verification = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command',
    "$ErrorActionPreference = 'Stop'; $PSNativeCommandUseErrorActionPreference = $true;\n"
      + commands.map((match) => match[1]).join('\n')], {
    cwd: resolve(site, '../..'), encoding: 'utf8', timeout: 120_000, env,
  });
  assert.ifError(verification.error);
  assert.equal(verification.status, 0, verification.stdout + verification.stderr);
  assert.match(verification.stdout, /Verified complete output provenance/);
  assert.match(verification.stdout, /Docs provenance verified/);
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

for (const [change, expected] of [
  ["await writeFile(new URL('extra.js', dir), 'unexpected');", /Output closure/],
  ["await writeFile(new URL('./src/late.mdx', import.meta.url), 'late source');", /source\/input closure/],
]) {
  test(`successful late hooks cannot change the sealed closure: ${expected.source}`, { timeout: 150_000 }, async () => {
    await fixture(async (root) => {
      const config = await readFile(join(root, 'astro.config.mjs'), 'utf8');
      await writeFile(join(root, 'astro.config.mjs'),
        "import { writeFile } from 'node:fs/promises';\n" + config.replace('provenance.integration,',
          `provenance.integration,
          { name: 'late-mutation', hooks: { 'astro:build:done': async ({ dir }) => { ${change} } } },`));
      const result = build(root);
      assert.notEqual(result.status, 0, result.output);
      assert.match(result.output, expected);
      await assert.rejects(readFile(join(root, 'dist', '.nachos', 'output-provenance.v1.json')), { code: 'ENOENT' });
      await assert.rejects(readFile(join(root, 'dist', '.nachos', 'bundle-modules.json')), { code: 'ENOENT' });
    });
  });
}

test('Starlight configuration plugins cannot introduce custom EC callbacks', { timeout: 150_000 }, async () => {
  await fixture(async (root) => {
    const config = await readFile(join(root, 'astro.config.mjs'), 'utf8');
    await writeFile(join(root, 'astro.config.mjs'), config.replace(
      'expressiveCode: {},',
      `expressiveCode: {}, plugins: [{
        name: 'unregistered-ec-insertion',
        hooks: { 'config:setup': ({ updateConfig }) => updateConfig({
          expressiveCode: { plugins: [{ name: 'Starlight Plugin', hooks: {
            postprocessRenderedBlock({ renderData }) {
              renderData.blockAst.children.push({ type: 'text', value: 'unregistered content' });
            }
          } }] }
        }) }
      }],`));
    const result = build(root);
    assert.notEqual(result.status, 0, result.output);
    assert.match(result.output, /Unknown Expressive Code content producer: custom configuration/);
  });
});

test('external EC configuration is rejected before it can execute', { timeout: 150_000 }, async () => {
  await fixture(async (root) => {
    await writeFile(join(root, 'ec.config.mjs'), 'throw new Error("external config executed");');
    const result = build(root);
    assert.notEqual(result.status, 0, result.output);
    assert.match(result.output, /Custom ec\.config\.mjs is unsupported/);
    assert.ok(!result.output.includes('external config executed'));
  });
});

test('unregistered Starlight plugins cannot capture and reuse the native renderer callback', { timeout: 150_000 }, async () => {
  await fixture(async (root) => {
    const config = await readFile(join(root, 'astro.config.mjs'), 'utf8');
    const imports = `import { createRequire } from 'node:module';
      const { preprocessThemes } = createRequire(import.meta.url)(fileURLToPath(new URL(
        './integrations/expressive-code/theming.js', import.meta.resolve('@astrojs/starlight'))));\n`;
    await writeFile(join(root, 'astro.config.mjs'), imports + config.replace(
      'expressiveCode: {},',
      `expressiveCode: {}, plugins: [{
        name: 'capture-native-renderer',
        hooks: { 'config:setup': ({ config, addIntegration }) => {
          const captured = config.expressiveCode.customCreateRenderer;
          addIntegration({ name: 'reuse-native-renderer', hooks: { 'astro:build:setup': async () => {
            const renderer = await captured({ themes: preprocessThemes(), plugins: [
              { name: 'Frames', hooks: { postprocessRenderedBlock({ renderData }) {
                renderData.blockAst.children.push({ type: 'text', value: 'unregistered native wrapper' });
              } } }, { name: 'Starlight Plugin' }
            ] });
            const result = await renderer.ec.render({ code: 'const value = 1;', language: 'js' });
            if (JSON.stringify(result.renderedGroupAst).includes('unregistered native wrapper')) {
              throw new Error('unregistered native wrapper executed');
            }
          } } });
        } }
      }],`));
    const result = build(root);
    assert.notEqual(result.status, 0, result.output);
    assert.match(result.output, /Unknown Expressive Code content producer: custom configuration/);
  });
});
