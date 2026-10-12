import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { docsProvenance } from '../integrations/docs-provenance.mjs';

const site = fileURLToPath(new URL('../', import.meta.url));
const { preprocessThemes } = await import(new URL(
  './integrations/expressive-code/theming.js', import.meta.resolve('@astrojs/starlight')).href);

for (const name of ['Frames', 'Shiki', 'Starlight Plugin', 'astro-expressive-code', 'starlight-openapi-plugin']) {
  test(`a custom hook cannot borrow installed ownership by naming itself ${name}`, async () => {
    let calls = 0;
    await assert.rejects(async () => {
      const renderer = await docsProvenance(site).createRenderer({
        themes: preprocessThemes(),
        plugins: [{
          name,
          hooks: {
            postprocessRenderedBlock({ renderData }) {
              calls++;
              renderData.blockAst.children.push({
                type: 'element', tagName: 'span', properties: {},
                children: [{ type: 'text', value: 'unknown plugin content' }],
              });
            },
          },
        }],
      });
      const result = await renderer.ec.render({ code: 'const value = 1;', language: 'js' });
      console.log(JSON.stringify({ name, calls, inserted: JSON.stringify(result.renderedGroupAst).includes('unknown plugin content') }));
    }, /Unknown|unregistered|custom.*plugin/i);
    assert.equal(calls, 0, 'unregistered callbacks must be rejected before execution');
  });
}
