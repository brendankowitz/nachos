import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const site = fileURLToPath(new URL('../', import.meta.url));

test('committed third-party notices match generator inputs', () => {
  const result = spawnSync(process.execPath, [join(site, 'scripts', 'notices.mjs'), '--check'], {
    cwd: site, encoding: 'utf8', timeout: 30_000,
  });
  assert.ifError(result.error);
  assert.equal(result.status, 0, result.stdout + result.stderr);
});

test('committed third-party notices are platform-independent', async () => {
  const text = await readFile(join(site, 'public', 'THIRD-PARTY-NOTICES.txt'), 'utf8');
  assert.equal(text.includes('\r'), false, 'notices must be byte-identical on Windows and Linux checkouts');
  const paths = [...text.matchAll(/^File: (.+)$/gm)].map((match) => match[1]);
  assert.ok(paths.length > 0);
  assert.deepEqual(paths.filter((path) => path.includes('\\')), []);
});
