import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
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
