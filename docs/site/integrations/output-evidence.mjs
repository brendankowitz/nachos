import { createHash } from 'node:crypto';
import { lstat, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { isDeepStrictEqual } from 'node:util';

export const metadata = ['.nachos/bundle-modules.json', '.nachos/output-provenance.v1.json'];
const outputBoundProducers = new Set([
  'vite-chunk', 'vite-asset', 'copy', 'expressive-stylesheet', 'expressive-script', 'sitemap', 'pagefind',
]);
export const producers = new Set([
  ...outputBoundProducers, 'astro-render', 'expressive-html', 'expressive-inline-style', 'mermaid-svg',
]);
export const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');
export const slash = (path) => path.replaceAll('\\', '/');
export const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;
export const ordered = (values) => [...new Set(values)].sort(compare);
const inputFiles = ['package.json', 'package-lock.json', 'astro.config.mjs', 'tsconfig.json', '.npmrc'];
const inputTrees = ['src', 'public', 'integrations', 'scripts'];
const hashPattern = /^[a-f0-9]{64}$/;
const same = isDeepStrictEqual;

export function docsBuildSettings() {
  return { site: process.env.DOCS_SITE ?? 'https://brendankowitz.github.io', base: process.env.DOCS_BASE ?? '/nachos' };
}

export function safePath(path) {
  if (typeof path !== 'string' || !path || path !== path.normalize('NFC') ||
      path.includes('\\') || path.split('/').some((segment) =>
        !segment || segment === '.' || segment === '..' || /[<>:"|?*\u0000-\u001f]/.test(segment) ||
        /[. ]$/.test(segment) || /^(con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|$)/i.test(segment))) {
    throw new Error(`Unsafe provenance path: ${path}`);
  }
  return path;
}

function uniquePaths(entries) {
  const seen = new Set();
  for (const { path } of entries) {
    const key = safePath(path).toLowerCase();
    if (seen.has(key)) throw new Error(`Duplicate provenance path: ${path}`);
    seen.add(key);
  }
}

export async function assertUnlinked(path) {
  let current = resolve(path);
  while (true) {
    try {
      if ((await lstat(current)).isSymbolicLink()) throw new Error(`Unverifiable symlink: ${current}`);
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
    }
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }
}

export async function regularFile(path) {
  await assertUnlinked(path);
  if (!(await lstat(path)).isFile()) throw new Error(`Not a regular file: ${path}`);
  return readFile(path);
}

export async function files(directory) {
  const root = await lstat(directory);
  if (root.isSymbolicLink() || !root.isDirectory()) throw new Error(`Unsafe directory: ${directory}`);
  const result = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isSymbolicLink()) throw new Error(`Unverifiable symlink: ${path}`);
    if (entry.isDirectory()) result.push(...await files(path));
    else if (entry.isFile()) result.push(path);
    else throw new Error(`Not a regular file: ${path}`);
  }
  return result.sort(compare);
}

export async function snapshotInputs(root) {
  const paths = inputFiles.map((name) => ({ path: `site/${name}`, file: join(root, name) }));
  for (const tree of inputTrees) {
    const content = await files(join(root, tree));
    if (!content.length) throw new Error(`Empty source input tree: ${tree}`);
    paths.push(...content.map((file) => ({ path: `site/${slash(relative(root, file))}`, file })));
  }
  const assetRoot = resolve(root, '..', 'assets');
  const assets = await files(assetRoot);
  if (!assets.length) throw new Error('Empty assets input tree');
  paths.push(...assets.map((file) => ({ path: `assets/${slash(relative(assetRoot, file))}`, file })));
  const inputs = [];
  for (const { path, file } of paths) inputs.push({ path, sha256: digest(await regularFile(file)) });
  uniquePaths(inputs);
  return inputs.sort((a, b) => compare(a.path, b.path));
}

async function installedPath(root, path) {
  safePath(path);
  // Node resolves nested fixture installations through ancestors too. Production
  // consumers must anchor this lookup to their independently selected npm root.
  let current = resolve(root);
  while (true) {
    const file = join(current, path);
    try {
      await regularFile(file);
      return file;
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
    }
    const parent = dirname(current);
    if (current === parent) throw new Error(`Missing installed source: ${path}`);
    current = parent;
  }
}

export async function sourceEvidence(root, file) {
  const absolute = resolve(file);
  const normalized = slash(absolute);
  const marker = normalized.indexOf('/node_modules/');
  const sha256 = digest(await regularFile(absolute));
  if (marker === -1) {
    const sitePath = slash(relative(root, absolute));
    const assetPath = slash(relative(resolve(root, '..', 'assets'), absolute));
    const path = !sitePath.startsWith('../') && !isAbsolute(sitePath) ? `site/${sitePath}` : `assets/${assetPath}`;
    safePath(path);
    return { path, sha256, kind: 'first-party' };
  }
  const npmRoot = normalized.slice(0, marker);
  const path = `npm/${normalized.slice(marker + 1)}`;
  safePath(path);
  let directory = dirname(absolute);
  while (slash(directory).startsWith(`${npmRoot}/node_modules/`)) {
    let bytes;
    try { bytes = await regularFile(join(directory, 'package.json')); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (bytes) {
      const pkg = JSON.parse(bytes);
      if (!pkg.name || !pkg.version || typeof pkg.name !== 'string' || typeof pkg.version !== 'string') {
        throw new Error(`Missing package identity for shipping source: ${file}`);
      }
      const packagePath = slash(relative(npmRoot, directory));
      const lock = JSON.parse(await regularFile(join(root, 'package-lock.json')));
      const record = lock.packages?.[packagePath];
      const inferredName = packagePath.split('node_modules/').at(-1);
      if (!record || record.version !== pkg.version || (record.name ?? inferredName) !== pkg.name || record.link) {
        throw new Error(`Installed source does not match lock evidence: ${packagePath}`);
      }
      return { path, sha256, kind: 'package', package: pkg.name, version: pkg.version,
        packagePath, packageJsonSha256: digest(bytes) };
    }
    directory = dirname(directory);
  }
  throw new Error(`Unknown package source: ${file}`);
}

export function packageUnion(sources) {
  const pairs = new Map();
  for (const source of sources) {
    if (source.kind === 'package') {
      const pair = { package: source.package, version: source.version };
      pairs.set(JSON.stringify(pair), pair);
    }
  }
  return [...pairs.values()].sort((a, b) => compare(a.package, b.package) || compare(a.version, b.version));
}

export async function outputInventory(directory) {
  const result = [];
  for (const file of await files(directory)) {
    const path = safePath(slash(relative(directory, file)));
    const bytes = await regularFile(file);
    if (!metadata.includes(path)) result.push({ path, sha256: digest(bytes) });
  }
  uniquePaths(result);
  if (!result.length) throw new Error('Empty output closure cannot establish completion');
  return result.sort((a, b) => compare(a.path, b.path));
}

export async function validateEvidence(root, directory, data, expectedInputs, expectedBuild = docsBuildSettings()) {
  if (!data || data.schemaVersion !== 1 ||
      !same(Object.keys(data).sort(), ['build', 'inputs', 'outputs', 'schemaVersion', 'sources'])) {
    throw new Error('Unsupported output provenance schema');
  }
  if (!data.build || typeof data.build.site !== 'string' || typeof data.build.base !== 'string' ||
      !same(Object.keys(data.build).sort(), ['base', 'site'])) throw new Error('Invalid build configuration');
  if (!same(data.build, expectedBuild)) throw new Error('Build settings differ from expected site/base');
  for (const entries of [data.inputs, data.sources, data.outputs]) {
    if (!Array.isArray(entries)) throw new Error('Invalid provenance records');
    uniquePaths(entries);
  }
  const inputs = await snapshotInputs(root);
  if (!same(inputs, data.inputs) || (expectedInputs && !same(inputs, expectedInputs))) {
    throw new Error('Current source/input closure differs from producer evidence');
  }
  const outputs = await outputInventory(directory);
  if (!same(outputs, data.outputs.map(({ path, sha256 }) => ({ path, sha256 })))) {
    throw new Error('Output closure missing, extra or changed since capture');
  }
  const sources = new Map();
  for (const entry of data.sources) {
    let file;
    if (entry.path.startsWith('site/')) file = join(root, entry.path.slice(5));
    else if (entry.path.startsWith('assets/')) file = join(root, '..', entry.path);
    else if (entry.path.startsWith('npm/node_modules/')) file = await installedPath(root, entry.path.slice(4));
    else throw new Error(`Unsafe source namespace: ${entry.path}`);
    const actual = await sourceEvidence(root, file);
    if (!same(actual, entry)) throw new Error(`Source evidence changed: ${entry.path}`);
    if (actual.kind === 'first-party' && !inputs.some((input) => input.path === actual.path && input.sha256 === actual.sha256)) {
      throw new Error(`Source outside fixed input closure: ${entry.path}`);
    }
    sources.set(entry.path, entry);
  }
  const used = new Set();
  for (const output of data.outputs) {
    if (!same(Object.keys(output).sort(), ['evidence', 'packages', 'path', 'sha256']) ||
        !Array.isArray(output.evidence) || !output.evidence.length) throw new Error(`Missing output evidence: ${output.path}`);
    const members = new Set();
    const records = new Set();
    for (const evidence of output.evidence) {
      if (!same(Object.keys(evidence).sort(), ['producer', 'sha256', 'sources']) ||
          !producers.has(evidence.producer) || !hashPattern.test(evidence.sha256) ||
          !Array.isArray(evidence.sources) || !evidence.sources.length ||
          !same(evidence.sources, ordered(evidence.sources))) {
        throw new Error(`Invalid producer evidence: ${output.path}`);
      }
      const key = JSON.stringify([evidence.producer, evidence.sources, evidence.sha256]);
      if (records.has(key)) throw new Error(`Duplicate producer evidence: ${output.path}`);
      records.add(key);
      for (const path of evidence.sources) {
        if (!sources.has(path)) throw new Error(`Missing source evidence: ${path}`);
        members.add(path);
        used.add(path);
      }
      if (evidence.producer === 'copy' && (evidence.sources.length !== 1 ||
          evidence.sha256 !== sources.get(evidence.sources[0]).sha256 || evidence.sha256 !== output.sha256)) {
        throw new Error(`Conflicting copy source/output hash: ${output.path}`);
      }
      if (outputBoundProducers.has(evidence.producer) &&
          evidence.sha256 !== output.sha256) throw new Error(`Conflicting producer output hash: ${output.path}`);
    }
    const bound = output.evidence.filter((entry) => outputBoundProducers.has(entry.producer));
    if (bound.length !== output.evidence.length) {
      const anchors = output.evidence.filter((entry) => entry.producer === 'astro-render');
      const html = bound.length === 0 && output.path.endsWith('.html');
      const pagefind = bound.length === 1 && bound[0].producer === 'pagefind';
      if (anchors.length !== 1 || (!html && !pagefind)) {
        throw new Error(`Invalid inline evidence context: require one render anchor in HTML or Pagefind: ${output.path}`);
      }
    } else if (bound.length !== 1) {
      throw new Error(`Invalid output producer context: require exactly one output-bound producer: ${output.path}`);
    }
    if (!same(output.packages, packageUnion([...members].map((path) => sources.get(path))))) {
      throw new Error(`Conflicting package evidence: ${output.path}`);
    }
  }
  if (!same(ordered(used), data.sources.map((entry) => entry.path))) throw new Error('Unreferenced or unordered source evidence');
  return data;
}

export async function writeEvidence(directory, data) {
  const target = join(directory, '.nachos');
  for (const name of metadata) await assertUnlinked(join(directory, name));
  await mkdir(target, { recursive: true });
  await writeFile(join(directory, metadata[0]), `${JSON.stringify(packageUnion(data.sources), null, 2)}\n`);
  await writeFile(join(directory, metadata[1]), `${JSON.stringify(data, null, 2)}\n`);
}

export async function verifyProvenance(root, directory = join(root, 'dist'), expectedBuild = docsBuildSettings()) {
  const data = JSON.parse(await regularFile(join(directory, metadata[1])));
  await validateEvidence(root, directory, data, undefined, expectedBuild);
  const flat = JSON.parse(await regularFile(join(directory, metadata[0])));
  if (!same(flat, packageUnion(data.sources))) throw new Error('Legacy package union differs from complete output evidence');
  return data;
}
