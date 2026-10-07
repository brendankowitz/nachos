import { readdir, readFile, stat } from 'node:fs/promises';
import { dirname, extname, join, relative, resolve, sep } from 'node:path';
import GithubSlugger from 'github-slugger';
import { JSDOM } from 'jsdom';
import { toString } from 'mdast-util-to-string';
import remarkGfm from 'remark-gfm';
import remarkParse from 'remark-parse';
import { unified } from 'unified';

// Mermaid's parser sanitizes labels through DOMPurify. No browser, rendering,
// script execution, or remote resource loading is needed for syntax validation.
const dom = new JSDOM('');
globalThis.window = dom.window;
globalThis.document = dom.window.document;
const { default: mermaid } = await import('mermaid');
mermaid.initialize({ startOnLoad: false, securityLevel: 'strict' });

const root = resolve(process.argv[2] ?? join(import.meta.dirname, '..', '..'));
const parser = unified().use(remarkParse).use(remarkGfm);
const documents = new Map();
let errors = 0;
let diagrams = 0;

function display(path) {
  return relative(root, path).split(sep).join('/');
}

function report(path, line, kind, message) {
  console.error(`${display(path)}:${line}:1 [${kind}] ${message}`);
  errors++;
}

function* walk(node) {
  yield node;
  for (const child of node.children ?? []) {
    yield* walk(child);
  }
}

async function markdownFiles(directory) {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...await markdownFiles(path));
    } else if (entry.isFile() && extname(path).toLowerCase() === '.md') {
      files.push(path);
    }
  }
  return files.sort();
}

async function loadDocument(path) {
  if (!documents.has(path)) {
    const source = await readFile(path, 'utf8');
    const nodes = [...walk(parser.parse(source))];
    const anchors = new Set();
    const sections = new Set();
    const slugger = new GithubSlugger();
    for (const node of nodes) {
      if (node.type !== 'heading') continue;
      const heading = toString(node, { includeHtml: false, includeImageAlt: false });
      anchors.add(slugger.slug(heading));
      const number = heading.match(/^(\d+(?:\.\d+)*)(?:\.?\s|\.?$)/)?.[1];
      if (number) sections.add(number);
    }
    documents.set(path, { source, nodes, anchors, sections });
  }
  return documents.get(path);
}

async function validateLink(path, node) {
  const url = node.url;
  if (/^(?:[a-z][a-z\d+.-]*:|\/\/)/i.test(url)) return;
  const line = node.position.start.line;
  const hash = url.indexOf('#');
  const location = hash < 0 ? url : url.slice(0, hash);
  let target;
  let fragment;
  try {
    const pathname = decodeURIComponent(location.split('?')[0]);
    fragment = hash < 0 ? '' : decodeURIComponent(url.slice(hash + 1));
    target = pathname
      ? resolve(pathname.startsWith('/') ? root : dirname(path), pathname.replace(/^\/+/, ''))
      : path;
  } catch (error) {
    report(path, line, 'link', `Invalid local URL "${url}": ${error.message}`);
    return;
  }

  let targetInfo;
  try {
    targetInfo = await stat(target);
  } catch (error) {
    report(path, line, 'link', `Cannot resolve local target "${url}": ${error.message}`);
    return;
  }
  if (!fragment) return;
  if (!targetInfo.isFile() || extname(target).toLowerCase() !== '.md') {
    report(path, line, 'fragment', `Cannot validate fragment "${fragment}" on non-Markdown target "${url}".`);
    return;
  }
  const targetDocument = await loadDocument(target);
  if (!targetDocument.anchors.has(fragment)) {
    report(path, line, 'fragment', `Missing heading fragment "${fragment}" in "${url}".`);
  }
}

async function validateDocument(path) {
  const { source, nodes, sections } = await loadDocument(path);
  const name = display(path);
  const research = name.split('/').includes('research');
  const normative = /^docs\/superpowers\/(?:specs|plans)\//.test(name) && !research;
  const spec = /^docs\/superpowers\/specs\//.test(name) && !research;

  if (normative) {
    for (const [index, line] of source.split('\n').entries()) {
      for (const match of line.matchAll(/\b(?:TBD|TODO|FIXME)\b/g)) {
        report(path, index + 1, 'placeholder', `Forbidden placeholder "${match[0]}".`);
      }
    }
  }

  if (spec) {
    for (const match of source.matchAll(/\u00a7\s*(\d+(?:\.\d+)*)\b/g)) {
      if (!sections.has(match[1])) {
        const line = source.slice(0, match.index).split('\n').length;
        report(path, line, 'section', `No numbered heading for section ${match[1]} in this document.`);
      }
    }
  }

  for (const node of nodes) {
    if (['link', 'image', 'definition'].includes(node.type)) {
      await validateLink(path, node);
    }
    if (node.type === 'code' && node.lang?.toLowerCase() === 'mermaid') {
      diagrams++;
      try {
        await mermaid.parse(node.value, { suppressErrors: false });
      } catch (error) {
        report(path, node.position.start.line, 'mermaid', String(error.message ?? error).replace(/\s+/g, ' '));
      }
    }
  }
}

try {
  if (process.argv.length > 3) throw new Error('Usage: node validate-docs.mjs [repository-root]');
  const files = await markdownFiles(join(root, 'docs'));
  if (!files.length) throw new Error('No Markdown files found under docs/.');
  for (const file of files) await validateDocument(file);
  if (errors) {
    console.error(`Documentation validation failed: ${errors} error(s).`);
    process.exitCode = 1;
  } else {
    console.log(`Validated ${files.length} Markdown files and ${diagrams} Mermaid blocks.`);
  }
} catch (error) {
  console.error(`[input] ${error.stack ?? error}`);
  process.exitCode = 1;
} finally {
  dom.window.close();
}
