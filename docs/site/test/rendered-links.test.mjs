import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

test('rendered dynamic plugin links must resolve to actual files and anchors at default and custom bases', async () => {
  const module = await import('../integrations/base-links.mjs');
  assert.equal(typeof module.verifyRenderedLinks, 'function', 'rendered routes need physical output validation');
  const runs = fileURLToPath(new URL('../.test-runs/', import.meta.url));
  await mkdir(runs, { recursive: true });
  const output = await mkdtemp(join(runs, 'links-'));
  try {
    for (const base of ['/nachos', '/']) {
      const prefix = base === '/' ? '' : base;
      await mkdir(join(output, 'reference/rest-api'), { recursive: true });
      await writeFile(join(output, 'index.html'), `<a href="${prefix}/reference/rest-api/#operation">API</a>`);
      await writeFile(join(output, 'reference/rest-api/index.html'), '<h1 id="operation">Real route</h1>');
      await module.verifyRenderedLinks(output, { site: 'https://docs.example.org', base });
      await writeFile(join(output, 'index.html'), `<a href="${prefix}/reference/rest-api/#missing">Missing anchor</a>`);
      await assert.rejects(module.verifyRenderedLinks(output, { site: 'https://docs.example.org', base }), /Rendered link/);
      await writeFile(join(output, 'index.html'), `<a href="${prefix}/missing/">Missing route</a>`);
      await assert.rejects(module.verifyRenderedLinks(output, { site: 'https://docs.example.org', base }), /Rendered link/);
    }
  } finally { await rm(output, { recursive: true, force: true }); }
});
