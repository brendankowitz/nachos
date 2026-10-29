import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { docsBuildSettings } from '../integrations/site-files.mjs';
import { generateReferences } from '../integrations/reference-stage.mjs';
import { verifyRenderedLinks } from '../integrations/base-links.mjs';

const require = createRequire(import.meta.url);
const [tool, ...args] = process.argv.slice(2);
if (!['astro', 'playwright'].includes(tool)) throw new Error(`Unsupported local tool: ${tool}`);
const root = fileURLToPath(new URL('../', import.meta.url));
const repository = resolve(root, '..', '..');
const output = resolve(root, 'dist');
const building = tool === 'astro' && args[0] === 'build';
let receipt;
if (building && (args.length !== 1 || resolve(process.cwd()) !== resolve(root))) {
  throw new Error('The site build must run from docs/site with no output/root overrides');
}
if (tool === 'astro' && ['build', 'dev', 'preview'].includes(args[0])) receipt = await generateReferences(repository, root);
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
if (building && result.status === 0) {
  await verifyRenderedLinks(output, docsBuildSettings());
  console.log('Verified rendered links.');
}
