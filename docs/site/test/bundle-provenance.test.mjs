import assert from 'node:assert/strict';
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { build } from 'vite';
import { createProvenance } from '../integrations/bundle-provenance.mjs';

const site = fileURLToPath(new URL('../', import.meta.url));
const runs = join(site, '.test-runs');
await mkdir(runs, { recursive: true });

async function fixture(action) {
  const directory = await mkdtemp(join(runs, 'provenance-'));
  const root = join(directory, 'site');
  try {
    await mkdir(join(root, 'src'), { recursive: true });
    for (const name of ['integrations', 'scripts', 'public', 'package.json', 'package-lock.json', '.npmrc', 'astro.config.mjs', 'tsconfig.json']) {
      await cp(join(site, name), join(root, name), { recursive: true });
    }
    await cp(join(site, '..', 'assets'), join(directory, 'assets'), { recursive: true });
    await writeFile(join(root, 'src', 'index.html'), '<script type="module" src="./entry.js"></script>');
    await writeFile(join(root, 'src', 'entry.js'), "import { slug } from 'github-slugger'; console.log(slug(location.hash));");
    return await action(root, join(root, 'dist'));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

async function bundle(root, provenance, plugins = [], ssr = false) {
  await build({
    root, configFile: false, logLevel: 'silent', publicDir: false,
    plugins: [provenance.vitePlugin, ...plugins],
    build: { ssr: ssr ? join(root, 'src', 'entry.js') : false,
      rollupOptions: ssr ? undefined : { input: join(root, 'src', 'index.html') } },
  });
}

test('real client chunks and copied package assets produce sorted exact package versions', async () => {
  await fixture(async (root, dist) => {
    const provenance = createProvenance(root);
    await bundle(root, provenance);
    const source = join(site, 'node_modules', '@pagefind', 'default-ui', 'css', 'ui.css');
    await provenance.copyAsset(source, join(dist, 'search.css'));
    await provenance.write(dist);
    const manifest = await readFile(join(dist, '.nachos', 'bundle-modules.json'), 'utf8');
    assert.deepEqual(JSON.parse(manifest), [
      { package: '@pagefind/default-ui', version: '1.5.2' },
      { package: 'github-slugger', version: '2.0.0' },
      { package: 'vite', version: '8.3.1' },
    ]);
    assert.deepEqual(await readFile(join(dist, 'search.css')), await readFile(source));
    assert.ok(!manifest.includes('rehype-mermaid'), 'build-only tooling must not be labeled client distribution');
    await provenance.write(dist);
    assert.equal(await readFile(join(dist, '.nachos', 'bundle-modules.json'), 'utf8'), manifest);
  });
});

test('package assets emitted by a real Vite plugin retain their source provenance', async () => {
  await fixture(async (root, dist) => {
    const source = join(site, 'node_modules', '@pagefind', 'default-ui', 'css', 'ui.css');
    const provenance = createProvenance(root);
    await bundle(root, provenance, [{
      name: 'fixture-asset',
      async buildStart() {
        this.emitFile({ type: 'asset', fileName: 'emitted.css', originalFileName: source, source: await readFile(source) });
      },
    }]);
    await provenance.write(dist);
    const manifest = JSON.parse(await readFile(join(dist, '.nachos', 'bundle-modules.json')));
    assert.ok(manifest.some((entry) => entry.package === '@pagefind/default-ui' && entry.version === '1.5.2'));
  });
});

test('an SSR-only package does not enter the distribution manifest', async () => {
  await fixture(async (root, dist) => {
    const provenance = createProvenance(root);
    await bundle(root, provenance, [], true);
    // SSR files are not public site output. Audit a separate client directory.
    const client = join(root, 'client');
    await mkdir(client);
    await provenance.copyAsset(join(root, 'src', 'index.html'), join(client, 'index.html'));
    await provenance.write(client);
    assert.deepEqual(JSON.parse(await readFile(join(client, '.nachos', 'bundle-modules.json'))), []);
  });
});

test('unknown emitted asset provenance fails instead of being silently omitted', async () => {
  await fixture(async (root) => {
    const provenance = createProvenance(root);
    await assert.rejects(bundle(root, provenance, [{
      name: 'unattributed-asset',
      buildStart() { this.emitFile({ type: 'asset', fileName: 'unknown.js', source: 'alert(1)' }); },
    }]), /provenance.*unknown\.js/i);
  });
});

test('invented original filenames do not establish shipping asset provenance', async () => {
  await fixture(async (root) => {
    await assert.rejects(bundle(root, createProvenance(root), [{
      name: 'invented-source',
      buildStart() {
        this.emitFile({ type: 'asset', fileName: 'unknown.css', originalFileName: join(root, 'nonexistent.css'), source: 'body { color: red }' });
      },
    }]), /ENOENT|provenance/i);
  });
});

test('untracked files copied after bundling fail the final artifact inventory', async () => {
  await fixture(async (root, dist) => {
    const provenance = createProvenance(root);
    await bundle(root, provenance);
    await writeFile(join(dist, 'untracked.wasm'), 'unknown binary');
    await assert.rejects(provenance.write(dist), /provenance.*untracked\.wasm/i);
  });
});

test('untracked HTML cannot hide copied third-party scripts from the inventory', async () => {
  await fixture(async (root, dist) => {
    const provenance = createProvenance(root);
    await bundle(root, provenance);
    await writeFile(join(dist, 'untracked.html'), '<script>alert("unattributed")</script>');
    await assert.rejects(provenance.write(dist), /provenance.*untracked\.html/i);
  });
});

test('manifest directory is not a hiding place for untracked shipping assets', async () => {
  await fixture(async (root, dist) => {
    const provenance = createProvenance(root);
    await bundle(root, provenance);
    await mkdir(join(dist, '.nachos'));
    await writeFile(join(dist, '.nachos', 'hidden.js'), 'console.log("unattributed");');
    await assert.rejects(provenance.write(dist), /provenance.*hidden\.js/i);
  });
});

test('a shipping package without version evidence fails', async () => {
  await fixture(async (root) => {
    const dependency = join(root, 'node_modules', 'unversioned');
    await mkdir(dependency, { recursive: true });
    await writeFile(join(dependency, 'package.json'), '{"name":"unversioned","type":"module","exports":"./index.js"}');
    await writeFile(join(dependency, 'index.js'), 'console.log("shipping");');
    await writeFile(join(root, 'src', 'entry.js'), 'import "unversioned";');
    await assert.rejects(bundle(root, createProvenance(root)), /Missing package identity/);
  });
});

test('modified copied assets fail instead of retaining stale provenance', async () => {
  await fixture(async (root, dist) => {
    const provenance = createProvenance(root);
    await bundle(root, provenance);
    await provenance.copyAsset(join(site, 'node_modules', '@pagefind', 'default-ui', 'css', 'ui.css'), join(dist, 'search.css'));
    await writeFile(join(dist, 'search.css'), 'unrelated code');
    await assert.rejects(provenance.write(dist), /changed.*search\.css/i);
  });
});
