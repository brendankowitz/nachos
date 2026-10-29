import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const site = fileURLToPath(new URL('../', import.meta.url));
const guard = new URL('./reject-image-font-tools.mjs', import.meta.url).href;
process.env.PLAYWRIGHT_BROWSERS_PATH = fileURLToPath(new URL('../.playwright/', import.meta.url));
const { chromium } = await import('playwright');

test('original logo exports through Chromium without Sharp and has transparent pixels', async () => {
  const result = spawnSync(process.execPath, ['--import', guard, 'scripts/export-logo.mjs'], {
    cwd: site, encoding: 'utf8', timeout: 60_000, env: process.env,
  });
  assert.ifError(result.error);
  assert.equal(result.status, 0, result.stdout + result.stderr);
  const png = await readFile(new URL('../../assets/nachos-logo.png', import.meta.url));
  assert.equal(png.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
  assert.equal(png.readUInt32BE(16), 350);
  assert.equal(png.readUInt32BE(20), 350);
  assert.equal(png[25], 6, 'PNG must contain RGBA pixels');
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage();
    const pixels = await page.evaluate(async (base64) => {
      const image = new Image();
      image.src = `data:image/png;base64,${base64}`;
      await image.decode();
      const canvas = document.createElement('canvas');
      canvas.width = canvas.height = 350;
      const context = canvas.getContext('2d');
      context.drawImage(image, 0, 0);
      return {
        corner: [...context.getImageData(0, 0, 1, 1).data],
        chip: [...context.getImageData(175, 210, 1, 1).data],
      };
    }, png.toString('base64'));
    assert.equal(pixels.corner[3], 0);
    assert.equal(pixels.chip[3], 255);
  } finally {
    await browser.close();
  }
  assert.deepEqual(await readFile(new URL('../public/favicon.svg', import.meta.url)),
    await readFile(new URL('../../assets/nachos-logo.svg', import.meta.url)));
});
