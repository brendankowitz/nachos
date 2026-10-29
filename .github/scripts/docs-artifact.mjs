import { spawnSync } from 'node:child_process';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { digest, files, regularFile, safePath } from '../../docs/site/integrations/site-files.mjs';

export async function snapshotTree(output) {
  return Promise.all((await files(output)).map(async (file) => ({
    path: safePath(relative(output, file).replaceAll('\\', '/')),
    sha256: digest(await regularFile(file)),
  })));
}

export async function verifyArchive(archive, seal) {
  const bytes = await regularFile(archive);
  if (bytes.length < 1024 || bytes.length % 512 !== 0) throw new Error('Pages archive must contain complete USTAR records.');
  if (!Array.isArray(seal) || seal.length === 0) throw new Error('Pages archive requires a nonempty file seal.');
  const expectedFiles = new Set();
  const expectedDirectories = new Set(['']);
  const aliases = new Map();
  for (const item of seal) {
    if (!item || Object.keys(item).sort().join(',') !== 'path,sha256' ||
        typeof item.sha256 !== 'string' || item.sha256.length !== 64 || !/^[0-9a-f]+$/.test(item.sha256)) {
      throw new Error('Invalid Pages archive seal.');
    }
    const path = safePath(item.path);
    if (expectedFiles.has(path)) throw new Error('Duplicate sealed file.');
    expectedFiles.add(path);
    const segments = path.split('/');
    for (let length = 1; length < segments.length; length++) expectedDirectories.add(segments.slice(0, length).join('/'));
  }
  for (const path of [...expectedDirectories, ...expectedFiles]) {
    const key = path.toLowerCase();
    if (aliases.has(key)) throw new Error('Sealed file/directory conflict or case alias.');
    aliases.set(key, path);
  }
  const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
  const entries = [];
  const directories = new Set();
  const seen = new Set();
  let terminated = false;
  for (let offset = 0; offset < bytes.length;) {
    const header = bytes.subarray(offset, offset + 512);
    if (header.every((byte) => byte === 0)) {
      if (bytes.length - offset < 1024 || !bytes.subarray(offset).every((byte) => byte === 0)) {
        throw new Error('Pages archive requires two zero end records and only complete zero trailing records.');
      }
      terminated = true;
      break;
    }
    if (!header.subarray(257, 263).equals(Buffer.from('ustar\0')) ||
        !header.subarray(263, 265).equals(Buffer.from('00')) ||
        !header.subarray(500).every((byte) => byte === 0)) throw new Error('Invalid USTAR header framing.');
    const text = (start, end) => {
      const field = header.subarray(start, end);
      const nul = field.indexOf(0);
      if (nul >= 0 && !field.subarray(nul).every((byte) => byte === 0)) throw new Error('Nonzero USTAR text padding.');
      return decoder.decode(nul < 0 ? field : field.subarray(0, nul));
    };
    const octal = (start, end, unused = false) => {
      const value = header.subarray(start, end).toString('latin1');
      if (unused && /^[\0 ]+$/.test(value)) return 0;
      if (!/^ *[0-7]+[\0 ]*$/.test(value)) throw new Error('Invalid USTAR octal field.');
      const number = Number.parseInt(value.trimStart(), 8);
      if (!Number.isSafeInteger(number) || number < 0) throw new Error('Unbounded USTAR numeric field.');
      return number;
    };
    const checksum = header.reduce((sum, byte, index) => sum + (index >= 148 && index < 156 ? 32 : byte), 0);
    if (checksum !== octal(148, 156)) throw new Error('Archive header checksum changed.');
    for (const [start, end] of [[100, 108], [108, 116], [116, 124], [136, 148]]) octal(start, end);
    if (octal(329, 337, true) !== 0 || octal(337, 345, true) !== 0 || text(157, 257) !== '') {
      throw new Error('Pages archive cannot carry link or device metadata.');
    }
    text(265, 297);
    text(297, 329);
    const size = octal(124, 136);
    const type = header[156];
    if (type !== 0 && type !== 48 && type !== 53) throw new Error('Pages archive must contain only regular files and directories.');
    const prefix = text(345, 500);
    const filename = text(0, 100);
    if (!filename) throw new Error('USTAR entry requires a nonempty name field.');
    const name = (prefix ? prefix + '/' : '') + filename;
    const directory = type === 53;
    const root = directory && (name === '.' || name === './');
    const relativeName = name.startsWith('./') ? name.slice(2) : name;
    const path = root ? '' : safePath(directory && relativeName.endsWith('/') ? relativeName.slice(0, -1) : relativeName);
    if (seen.has(path)) throw new Error('Duplicate archive file or directory.');
    seen.add(path);
    offset += 512;
    const padding = (512 - size % 512) % 512;
    // Compare against remaining bytes before addition; never round or step past
    // the buffer using an unchecked size from an untrusted header.
    if (size > bytes.length - offset || padding > bytes.length - offset - size) throw new Error('Truncated USTAR payload or padding.');
    if (!bytes.subarray(offset + size, offset + size + padding).every((byte) => byte === 0)) {
      throw new Error('Nonzero USTAR payload padding.');
    }
    if (!directory) {
      if (!expectedFiles.has(path)) throw new Error('Unknown archive file.');
      entries.push({ path, sha256: digest(bytes.subarray(offset, offset + size)) });
    } else {
      if (size !== 0 || !expectedDirectories.has(path)) throw new Error('Unknown or nonempty archive directory.');
      directories.add(path);
    }
    offset += size + padding;
  }
  if (!terminated) throw new Error('Missing two-record USTAR terminator.');
  if (directories.size !== expectedDirectories.size) throw new Error('Missing expected archive directory.');
  entries.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  if (JSON.stringify(entries) !== JSON.stringify(seal)) throw new Error('Archived tree changed from the audited seal.');
}

export async function archiveTree(output, archive, seal) {
  if (JSON.stringify(await snapshotTree(output)) !== JSON.stringify(seal)) throw new Error('Output changed after audit seal.');
  await mkdir(dirname(archive), { recursive: true });
  try {
    const result = spawnSync('tar', ['--format=ustar', '-cf', archive, '-C', output, '.'], {
      encoding: 'utf8', timeout: 30_000, maxBuffer: 1024 * 1024,
    });
    if (result.error || result.status !== 0) throw new Error('Pages archive creation failed.');
    if (JSON.stringify(await snapshotTree(output)) !== JSON.stringify(seal)) throw new Error('Output changed during archiving.');
    await verifyArchive(archive, seal);
  } catch (error) {
    await rm(archive, { force: true });
    throw error;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [command, repository, sealPath, archive] = process.argv.slice(2);
  if (!['seal', 'archive', 'verify-archive'].includes(command) || !repository || !sealPath ||
      ((command === 'archive' || command === 'verify-archive') && !archive)) throw new Error('Invalid Pages artifact arguments.');
  const output = join(resolve(repository), 'docs/site/dist');
  if (command === 'seal') {
    await mkdir(dirname(resolve(sealPath)), { recursive: true });
    await writeFile(sealPath, JSON.stringify(await snapshotTree(output), null, 2) + '\n');
  } else {
    const seal = JSON.parse(await readFile(sealPath, 'utf8'));
    if (command === 'archive') await archiveTree(output, resolve(archive), seal);
    else {
      if (JSON.stringify(await snapshotTree(output)) !== JSON.stringify(seal)) throw new Error('Output changed after audit.');
      await verifyArchive(resolve(archive), seal);
    }
  }
}
