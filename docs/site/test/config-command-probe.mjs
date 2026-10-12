import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';

const command = process.argv[2];
assert.ok(['build', 'dev', 'preview'].includes(command));
const astro = new URL('./', import.meta.resolve('astro/package.json'));
const { resolveConfig } = await import(new URL('dist/core/config/config.js', astro));
const { createSettings } = await import(new URL('dist/core/config/settings.js', astro));
const { loadOrCreateNodeLogger } = await import(new URL('dist/core/logger/load.js', astro));
const { runHookConfigSetup } = await import(new URL('dist/integrations/hooks.js', astro));
const root = fileURLToPath(new URL('../', import.meta.url));
const { astroConfig } = await resolveConfig({ root, logLevel: 'error' }, command);
const logger = await loadOrCreateNodeLogger(astroConfig, { logLevel: 'error' });
const settings = await createSettings(astroConfig, 'error', root);
await runHookConfigSetup({ settings, command, logger });
console.log(`Actual Astro config setup accepted ${command}; no build or server was started.`);
