import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { docsProvenance } from '../integrations/docs-provenance.mjs';

const site = fileURLToPath(new URL('../', import.meta.url));
const { preprocessThemes } = await import(new URL(
  './integrations/expressive-code/theming.js', import.meta.resolve('@astrojs/starlight')).href);

test('unknown Expressive Code content plugins fail at the real renderer boundary', async () => {
  await assert.rejects(docsProvenance(site).createRenderer({
    themes: preprocessThemes(),
    plugins: [{ name: 'unreviewed-content', baseStyles: 'body{color:red}' }],
  }), /Unknown Expressive Code content producer/);
});

test('external theme data cannot inherit the recorded Starlight theme sources', async () => {
  await assert.rejects(docsProvenance(site).createRenderer({ themes: ['dracula'] }), /theme.*source|source.*theme/i);
});

test('changed theme data cannot be authenticated merely by its known theme name', async () => {
  const themes = preprocessThemes();
  themes[0].colors['editor.foreground'] = '#123456';
  await assert.rejects(docsProvenance(site).createRenderer({ themes }), /theme.*source|source.*theme/i);
});

test('fixed native theme retention is not admitted as caller configuration', async () => {
  await assert.rejects(docsProvenance(site).starlight({
    expressiveCode: { removeUnusedThemes: false },
  }), /custom configuration is unsupported/);
});
