import { spawnSync } from 'node:child_process';
import { rm } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { metadata, regularFile, snapshotInputs, validateEvidence, verifyProvenance, writeEvidence } from '../integrations/output-evidence.mjs';
import { generateReferences } from '../integrations/reference-stage.mjs';
import { verifyRenderedLinks } from '../integrations/base-links.mjs';

const require = createRequire(import.meta.url);
const [tool, ...args] = process.argv.slice(2);
if (!['astro', 'playwright'].includes(tool)) throw new Error(`Unsupported local tool: ${tool}`);
const root = fileURLToPath(new URL('../', import.meta.url));
const repository = resolve(root, '..', '..');
const output = resolve(root, 'dist');
const pending = resolve(root, '.astro', 'nachos-output.pending.json');
const building = tool === 'astro' && args[0] === 'build';
let inputs;
let receipt;
if (building) {
  if (args.length !== 1 || resolve(process.cwd()) !== resolve(root)) {
    throw new Error('Provenance build must run from docs/site with no output/root overrides');
  }
  for (const path of [...metadata.map((name) => resolve(output, name)), pending]) {
    try {
      await regularFile(path);
      await rm(path);
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
    }
  }
  receipt = await generateReferences(repository, root);
  inputs = await snapshotInputs(root);
}
if (tool === 'astro' && ['dev', 'preview'].includes(args[0])) receipt = await generateReferences(repository, root);
const manifest = require.resolve(`${tool}/package.json`);
const cli = resolve(dirname(manifest), require(manifest).bin[tool]);
const result = spawnSync(process.execPath, [cli, ...args], {
  stdio: 'inherit',
  env: {
    ...process.env,
    ASTRO_TELEMETRY_DISABLED: '1',
    NACHOS_REFERENCE_RECEIPT: receipt ?? '',
    PLAYWRIGHT_BROWSERS_PATH: process.env.PLAYWRIGHT_BROWSERS_PATH ?? fileURLToPath(new URL('../.playwright/', import.meta.url)),
  },
});
if (result.error) throw result.error;
process.exitCode = result.status ?? 1;
if (building) {
  try {
    if (result.status === 0) {
      const data = JSON.parse(await regularFile(pending));
      await validateEvidence(root, output, data, inputs);
      await verifyRenderedLinks(output, data.build);
      await writeEvidence(output, data);
      await verifyProvenance(root, output);
      console.log(`Verified complete output provenance: ${data.outputs.length} files.`);
    }
  } catch (error) {
    for (const name of metadata) await rm(resolve(output, name), { force: true });
    throw error;
  } finally {
    await rm(pending, { force: true });
  }
}
