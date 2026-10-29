import { createHash } from 'node:crypto';
import { lstat, readFile, readdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';

export const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');
const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;

export function docsBuildSettings() {
  return { site: process.env.DOCS_SITE ?? 'https://brendankowitz.github.io', base: process.env.DOCS_BASE ?? '/nachos' };
}

export function safePath(path) {
  if (typeof path !== 'string' || !path || path !== path.normalize('NFC') ||
      path.includes('\\') || path.split('/').some((segment) =>
        !segment || segment === '.' || segment === '..' || /[<>:"|?*\u0000-\u001f]/.test(segment) ||
        /[. ]$/.test(segment) || /^(con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|$)/i.test(segment))) {
    throw new Error(`Unsafe output path: ${path}`);
  }
  return path;
}

async function assertUnlinked(path) {
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
