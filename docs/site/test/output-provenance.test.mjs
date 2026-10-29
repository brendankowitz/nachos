import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import * as producer from '../integrations/bundle-provenance.mjs';

const site = fileURLToPath(new URL('../', import.meta.url));
const runs = join(site, '.test-runs');
const manifestName = '.nachos/output-provenance.v1.json';
const hash = (value) => createHash('sha256').update(value).digest('hex');
await mkdir(runs, { recursive: true });

async function fixture(action) {
  const directory = await mkdtemp(join(runs, 'output-'));
  const root = join(directory, 'site');
  const dist = join(root, 'dist');
  const put = async (path, value) => {
    await mkdir(dirname(join(root, path)), { recursive: true });
    await writeFile(join(root, path), value);
  };
  try {
    await put('package.json', '{"name":"fixture","version":"1.0.0"}');
    await put('package-lock.json', JSON.stringify({
      lockfileVersion: 3, packages: {
        '': { name: 'fixture', version: '1.0.0' },
        'node_modules/fixture-content': { version: '2.3.4' },
      },
    }));
    await put('astro.config.mjs', 'export default {};');
    await put('tsconfig.json', '{}');
    await put('.npmrc', '');
    await put('src/page.html', '<html>Original source</html>');
    await put('public/icon.svg', '<svg/>');
    await put('integrations/producer.mjs', 'export const producer = true;');
    await put('scripts/build.mjs', 'console.log("build");');
    await put('../assets/logo.svg', '<svg>logo</svg>');
    await put('node_modules/fixture-content/package.json', '{"name":"fixture-content","version":"2.3.4"}');
    await mkdir(dist);
    const provenance = producer.createProvenance(root);
    const source = join(root, 'src', 'page.html');
    await action({ root, dist, put, provenance, source });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

async function complete(f) {
  await f.provenance.copyAsset(f.source, join(f.dist, 'index.html'));
  await f.provenance.write(f.dist);
}

test('persists exact nonempty output closure and first-party source evidence beside the flat array', async () => {
  await fixture(async (f) => {
    await complete(f);
    const data = JSON.parse(await readFile(join(f.dist, manifestName)));
    assert.equal(data.schemaVersion, 1);
    assert.equal(data.outputs.length, 1);
    assert.equal(data.outputs[0].path, 'index.html');
    assert.equal(data.outputs[0].sha256, hash('<html>Original source</html>'));
    assert.deepEqual(data.outputs[0].packages, []);
    assert.ok(data.sources.some((s) => s.path === 'site/src/page.html' && s.kind === 'first-party'));
    assert.ok(data.inputs.some((s) => s.path === 'site/package-lock.json'));
    assert.deepEqual(JSON.parse(await readFile(join(f.dist, '.nachos/bundle-modules.json'))), []);
  });
});

for (const [name, bytes] of [
  ['client.js', 'console.log("supplied");'], ['styles.css', 'body{color:red}'],
  ['font.woff2', 'controlled font bytes'], ['icon.svg', '<svg>supplied</svg>'],
  ['embedded.png', 'controlled image with embedded content'],
]) {
  test(`persists actual copied package ownership for ${name}`, async () => {
    await fixture(async (f) => {
      await f.put(`node_modules/fixture-content/${name}`, bytes);
      await f.provenance.copyAsset(join(f.root, 'node_modules/fixture-content', name), join(f.dist, name));
      await f.provenance.write(f.dist);
      const data = JSON.parse(await readFile(join(f.dist, manifestName)));
      assert.equal(data.outputs[0].sha256, hash(bytes));
      assert.deepEqual(data.outputs[0].packages, [{ package: 'fixture-content', version: '2.3.4' }]);
      assert.ok(data.sources.some((s) => s.package === 'fixture-content' && s.sha256 === hash(bytes)));
    });
  });
}

test('empty and manifest-only directories cannot establish producer completion', async () => {
  await fixture(async (f) => {
    await assert.rejects(f.provenance.write(f.dist), /empty|output|completion/i);
    await f.put('dist/.nachos/bundle-modules.json', '[]');
    await assert.rejects(f.provenance.write(f.dist), /empty|output|completion/i);
  });
});

test('HTML without source evidence is rejected rather than blanket labeled first-party', async () => {
  await fixture(async (f) => {
    await f.put('dist/index.html', '<html>unattributed inline content</html>');
    await assert.rejects(async () => {
      await f.provenance.recordFile(join(f.dist, 'index.html'), []);
      await f.provenance.write(f.dist);
    }, /source|evidence/i);
  });
});

test('removing a recorded output before finalization is rejected', async () => {
  await fixture(async (f) => {
    await f.provenance.copyAsset(f.source, join(f.dist, 'index.html'));
    await f.provenance.copyAsset(f.source, join(f.dist, 'other.html'));
    await rm(join(f.dist, 'other.html'));
    await assert.rejects(f.provenance.write(f.dist), /missing|closure/i);
  });
});

test('conflicting bytes or ownership for one output cannot overwrite captured evidence', async () => {
  await fixture(async (f) => {
    await f.provenance.copyAsset(f.source, join(f.dist, 'index.html'));
    await f.put('dist/index.html', 'replacement');
    await assert.rejects(f.provenance.recordFile(join(f.dist, 'index.html'), [f.source]), /conflict|changed/i);
  });
});

test('identical generated bytes with conflicting sources are rejected', async () => {
  await fixture(async (f) => {
    await f.provenance.registerGenerated('generated', [f.source]);
    await assert.rejects(
      f.provenance.registerGenerated('generated', [join(f.root, 'public/icon.svg')]), /conflict/i);
  });

});

test('copy producer cannot bless output different from its claimed source bytes', async () => {
  await fixture(async (f) => {
    await f.put('dist/index.html', 'unrelated');
    await assert.rejects(f.provenance.recordFile(join(f.dist, 'index.html'), [f.source], 'copy'), /changed|copy/i);
  });
});

test('unknown generated producer names fail even with real source bytes', async () => {
  await fixture(async (f) => {
    await assert.rejects(f.provenance.registerGenerated('generated', [f.source], 'invented-renderer'), /producer/i);
  });
});

for (const path of ['../escape.js', '/absolute.js', 'bad\\name.js', 'a/../b.js', 'AUX.txt', 'a:stream', 'trailing.']) {
  test(`rejects unsafe persisted path ${JSON.stringify(path)}`, async () => {
    assert.equal(typeof producer.verifyProvenance, 'function', 'independent verifier is required');
    await fixture(async (f) => {
      await complete(f);
      const data = JSON.parse(await readFile(join(f.dist, manifestName)));
      data.outputs[0].path = path;
      await f.put(`dist/${manifestName}`, JSON.stringify(data));
      await assert.rejects(producer.verifyProvenance(f.root, f.dist), /path|closure|unsafe/i);
    });
  });
}

for (const change of ['added', 'removed', 'changed', 'input-added', 'input-missing', 'lock', 'duplicate',
  'package-omitted', 'source-omitted', 'source-changed', 'metadata-only', 'extra-metadata', 'partial', 'symlink']) {
  test(`independent verification rejects ${change} after producer completion`, async () => {
    assert.equal(typeof producer.verifyProvenance, 'function', 'independent verifier is required');
    await fixture(async (f) => {
      await complete(f);
      const data = JSON.parse(await readFile(join(f.dist, manifestName)));
      switch (change) {
        case 'added': await f.put('dist/extra.js', 'extra'); break;
        case 'removed': await rm(join(f.dist, 'index.html')); break;
        case 'changed': await f.put('dist/index.html', 'changed'); break;
        case 'input-added': await f.put('src/extra.mdx', 'new source'); break;
        case 'input-missing': await rm(join(f.root, 'src/page.html')); break;
        case 'lock': await f.put('package-lock.json', '{}'); break;
        case 'duplicate': data.outputs.push(data.outputs[0]); break;
        case 'package-omitted':
          await f.put('dist/.nachos/bundle-modules.json', '[{"package":"phantom","version":"1"}]');
          break;
        case 'source-omitted': data.sources = []; break;
        case 'source-changed': await f.put('src/page.html', 'replaced'); break;
        case 'metadata-only': await rm(join(f.dist, 'index.html')); break;
        case 'extra-metadata': await f.put('dist/.nachos/hidden.js', 'hidden'); break;
        case 'partial': data.outputs = []; break;
        case 'symlink':
          await symlink(join(f.root, 'public'), join(f.dist, 'linked'), 'junction');
          break;
      }
      await f.put(`dist/${manifestName}`, JSON.stringify(data));
      await assert.rejects(producer.verifyProvenance(f.root, f.dist));
    });
  });
}

test('independent verification accepts legitimate package output and is read-only', async () => {
  assert.equal(typeof producer.verifyProvenance, 'function', 'independent verifier is required');
  await fixture(async (f) => {
    await f.put('node_modules/fixture-content/font.woff2', 'font');
    await f.provenance.copyAsset(join(f.root, 'node_modules/fixture-content/font.woff2'), join(f.dist, 'font.woff2'));
    await complete(f);
    const before = await readFile(join(f.dist, manifestName));
    const data = await producer.verifyProvenance(f.root, f.dist);
    assert.equal(data.outputs.length, 2);
    assert.deepEqual(await readFile(join(f.dist, manifestName)), before);
  });
});

for (const change of ['case-duplicate', 'unknown-producer', 'copy-hash', 'copy-substitution',
  'package-source-changed', 'package-json-changed', 'package-version-unlocked', 'source-symlink', 'metadata-symlink', 'settings']) {
  test(`rejects conflicting producer or source binding: ${change}`, async () => {
    await fixture(async (f) => {
      await f.put('node_modules/fixture-content/icon.svg', '<svg>package</svg>');
      await f.provenance.copyAsset(join(f.root, 'node_modules/fixture-content/icon.svg'), join(f.dist, 'package.svg'));
      await complete(f);
      const data = JSON.parse(await readFile(join(f.dist, manifestName)));
      switch (change) {
        case 'case-duplicate': data.outputs.push({ ...data.outputs[0], path: 'INDEX.html' }); break;
        case 'unknown-producer': data.outputs[0].evidence[0].producer = 'unknown'; break;
        case 'copy-hash': data.outputs[0].evidence[0].sha256 = '0'.repeat(64); break;
        case 'copy-substitution':
          await f.put('dist/index.html', 'unrelated');
          data.outputs[0].sha256 = hash('unrelated');
          break;
        case 'package-source-changed': await f.put('node_modules/fixture-content/icon.svg', 'replacement'); break;
        case 'package-json-changed':
          await f.put('node_modules/fixture-content/package.json', '{"name":"spoofed","version":"2.3.4"}'); break;
        case 'package-version-unlocked':
          await f.put('node_modules/fixture-content/package.json', '{"name":"fixture-content","version":"9.9.9"}'); break;
        case 'source-symlink':
          await rm(join(f.root, 'node_modules/fixture-content'), { recursive: true });
          await symlink(join(f.root, 'public'), join(f.root, 'node_modules/fixture-content'), 'junction'); break;
        case 'metadata-symlink':
          await rm(join(f.dist, '.nachos'), { recursive: true });
          await symlink(join(f.root, 'public'), join(f.dist, '.nachos'), 'junction');
          await assert.rejects(producer.verifyProvenance(f.root, f.dist));
          return;
        case 'settings': data.build.base = '/stale'; break;
      }
      await f.put(`dist/${manifestName}`, JSON.stringify(data));
      await assert.rejects(producer.verifyProvenance(f.root, f.dist));
    });
  });
}

test('copy refuses linked destination parents before writing through them', async () => {
  await fixture(async (f) => {
    await symlink(join(f.root, 'public'), join(f.dist, 'redirect'), 'junction');
    await assert.rejects(f.provenance.copyAsset(f.source, join(f.dist, 'redirect', 'unexpected.html')), /symlink/i);
    await assert.rejects(readFile(join(f.root, 'public', 'unexpected.html')), { code: 'ENOENT' });
  });
});

test('generated files are explicitly emitted from producer bytes before sealing', async () => {
  await fixture(async (f) => {
    assert.equal(typeof f.provenance.emitGenerated, 'function', 'generated output requires an awaited byte producer');
    await f.provenance.emitGenerated(join(f.dist, 'search.js'), 'generated search', [f.source], 'pagefind');
    assert.equal(await readFile(join(f.dist, 'search.js'), 'utf8'), 'generated search');
    await f.provenance.write(f.dist);
    await producer.verifyProvenance(f.root, f.dist);
    await f.put('dist/search.js', 'tampered');
    await assert.rejects(f.provenance.write(f.dist), /changed/i);
  });
});

test('duplicate evidence cannot hide behind reordered JSON object properties', async () => {
  await fixture(async (f) => {
    await complete(f);
    const data = JSON.parse(await readFile(join(f.dist, manifestName)));
    const { sha256, sources, producer: name } = data.outputs[0].evidence[0];
    data.outputs[0].evidence.push({ sha256, sources, producer: name });
    await f.put(`dist/${manifestName}`, JSON.stringify(data));
    await assert.rejects(producer.verifyProvenance(f.root, f.dist), /duplicate/i);
  });
});
