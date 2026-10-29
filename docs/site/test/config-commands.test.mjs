import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { before, test } from 'node:test';
import { generateReferences } from '../integrations/reference-stage.mjs';

const site = fileURLToPath(new URL('../', import.meta.url));
let receipt;
before(async () => {
  receipt = await generateReferences(resolve(site, '../..'), site);
}, { timeout: 150_000 });

for (const command of ['build', 'dev', 'preview']) {
  test(`trusted actual Astro config setup supports ${command} without a server`, { timeout: 150_000 }, () => {
    const result = spawnSync(process.execPath, [join(site, 'test/config-command-probe.mjs'), command], {
      cwd: site, encoding: 'utf8', timeout: 120_000,
      env: { ...process.env, NACHOS_REFERENCE_RECEIPT: receipt },
    });
    assert.ifError(result.error);
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.match(result.stdout, new RegExp(`accepted ${command}; no build or server was started`));
  });
}
