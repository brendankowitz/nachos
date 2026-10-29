import { fromHtml } from 'hast-util-from-html';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);

export default function mermaidSvg() {
  return async (tree, file) => {
    const diagrams = [];
    function visit(parent) {
      for (const [index, node] of (parent.children ?? []).entries()) {
        const code = node.tagName === 'pre' && node.children?.length === 1 ? node.children[0] : null;
        if (code?.tagName === 'code' && code.properties?.className?.includes('language-mermaid')) {
          diagrams.push({ parent, index, source: code.children.map((child) => child.value ?? '').join('') });
        } else {
          visit(node);
        }
      }
    }
    visit(tree);
    if (!diagrams.length) return;

    // Astro disposes its config module runner before this hook. Native require
    // keeps delayed browser loading independent of that runner's lifetime.
    const { chromium } = require('playwright');
    const browser = await chromium.launch();
    try {
      const page = await browser.newPage();
      await page.route('**/*', (route) => route.abort());
      await page.setContent('<!doctype html><html><body></body></html>');
      await page.addScriptTag({ path: fileURLToPath(import.meta.resolve('mermaid/dist/mermaid.js')) });
      await page.evaluate(() => {
        mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'neutral', htmlLabels: false });
      });
      for (const [index, diagram] of diagrams.entries()) {
        const svg = await page.evaluate(async ({ source, id }) => {
          const result = await mermaid.render(id, source);
          return result.svg;
        }, { source: diagram.source, id: `mermaid-${index}` });
        const node = fromHtml(svg, { fragment: true }).children.find((child) => child.tagName === 'svg');
        if (!node) throw new Error('Mermaid did not produce an SVG element.');
        diagram.parent.children[diagram.index] = node;
      }
    } catch (error) {
      throw new Error(`Mermaid SVG rendering failed in ${file.path}: ${error.message}`, { cause: error });
    } finally {
      await browser.close();
    }
  };
}
