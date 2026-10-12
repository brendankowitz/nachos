import { copyFile, mkdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const source = new URL('../../assets/nachos-logo.svg', import.meta.url);
process.env.PLAYWRIGHT_BROWSERS_PATH ??= fileURLToPath(new URL('../.playwright/', import.meta.url));
const { chromium } = await import('playwright');
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: 350, height: 350 }, deviceScaleFactor: 1 });
  await page.route('**/*', (route) => route.abort());
  await page.setContent(`<style>body { margin: 0; background: transparent; }</style>${await readFile(source, 'utf8')}`);
  await page.locator('svg').screenshot({
    path: fileURLToPath(new URL('../../assets/nachos-logo.png', import.meta.url)),
    omitBackground: true,
    animations: 'disabled',
  });
} finally {
  await browser.close();
}
await mkdir(new URL('../public/', import.meta.url), { recursive: true });
await copyFile(source, new URL('../public/favicon.svg', import.meta.url));
