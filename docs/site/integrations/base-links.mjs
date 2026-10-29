import { relative } from 'node:path';
import { files, regularFile } from './site-files.mjs';

export async function verifyRenderedLinks(output, { site, base }) {
  const { fromHtml } = await import('hast-util-from-html');
  const paths = new Map((await files(output)).map((path) => [relative(output, path).replaceAll('\\', '/'), path]));
  const pages = new Map();
  const prefix = base.replace(/\/$/, '');
  for (const [path, file] of paths) {
    if (!path.endsWith('.html')) continue;
    const ids = new Set();
    const links = [];
    function visit(node) {
      if (node.properties?.id) ids.add(String(node.properties.id));
      if (node.tagName === 'a' && typeof node.properties?.href === 'string') links.push(node.properties.href);
      for (const child of node.children ?? []) visit(child);
    }
    visit(fromHtml((await regularFile(file)).toString('utf8')));
    pages.set(path, { ids, links });
  }
  for (const [path, page] of pages) {
    const current = new URL(`${prefix}/${path.replace(/index\.html$/, '')}`, site);
    for (const href of page.links) {
      const url = new URL(href, current);
      if (!['http:', 'https:'].includes(url.protocol) || url.origin !== new URL(site).origin) continue;
      if (prefix && url.pathname !== prefix && !url.pathname.startsWith(prefix + '/')) {
        throw new Error(`Rendered link leaves the configured base: ${path}`);
      }
      let target = decodeURIComponent(url.pathname.slice(prefix.length)).replace(/^\//, '');
      if (!target || target.endsWith('/')) target += 'index.html';
      else if (!paths.has(target) && paths.has(target + '/index.html')) target += '/index.html';
      if (!paths.has(target) || (url.hash && pages.has(target) && !pages.get(target).ids.has(decodeURIComponent(url.hash.slice(1))))) {
        throw new Error(`Rendered link has no actual output target: ${path} -> ${url.pathname}${url.hash}`);
      }
    }
  }
}

export function baseLinks({ base }) {
  return (tree) => {
    function visit(node) {
      if (typeof node.url === 'string' && node.url.startsWith('/nachos/')) {
        node.url = `${base.replace(/\/$/, '')}/${node.url.slice('/nachos/'.length)}`;
      }
      for (const child of node.children ?? []) visit(child);
    }
    visit(tree);
  };
}
