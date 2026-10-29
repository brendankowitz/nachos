// First-party mutation helpers. Inputs are known-valid archives produced by tar,
// not an alternate verifier or a production acceptance implementation.
export function records(bytes) {
  const result = [];
  let offset = 0;
  while (offset < bytes.length && bytes.subarray(offset, offset + 512).some((byte) => byte !== 0)) {
    const size = Number.parseInt(bytes.subarray(offset + 124, offset + 136).toString('ascii'), 8);
    result.push({ offset, size, type: String.fromCharCode(bytes[offset + 156]), data: offset + 512 });
    offset += 512 + Math.ceil(size / 512) * 512;
  }
  return { entries: result, end: offset };
}

export function checksum(header) {
  header.fill(32, 148, 156);
  const sum = header.reduce((total, value) => total + value, 0);
  header.write(sum.toString(8).padStart(6, '0') + '\0 ', 148, 'ascii');
}

function field(header, offset, length, value) {
  header.fill(0, offset, offset + length);
  header.write(value, offset, length, 'utf8');
}

function directory(bytes, name) {
  const header = Buffer.from(bytes.subarray(0, 512));
  field(header, 0, 100, name);
  field(header, 124, 12, '00000000000');
  field(header, 156, 1, '5');
  header.fill(0, 157, 257);
  header.fill(0, 345, 500);
  checksum(header);
  return header;
}

export const mutationNames = [
  'unknown-directory', 'traversal-directory', 'absolute-directory', 'drive-directory',
  'duplicate-root', 'duplicate-directory', 'case-alias-directory', 'file-directory-conflict',
  'dot-segment-directory', 'double-slash-directory', 'invalid-utf8-directory',
  'bad-magic', 'bad-version', 'header-padding', 'name-after-nul', 'empty-name-with-prefix', 'linkname-on-file',
  'invalid-mode', 'invalid-uid', 'invalid-gid', 'invalid-time', 'numeric-terminal-lf', 'numeric-terminal-cr', 'base256-size',
  'maximum-size', 'embedded-nul-size', 'device-number',
  'payload-padding', 'truncated-padding', 'no-end-blocks', 'one-end-block',
  'partial-end-block', 'nonzero-trailer', 'partial-zero-trailer',
  'missing-directory', 'bad-checksum', 'symlink', 'duplicate-file', 'missing-file',
  'unknown-file', 'changed-payload',
];

export function mutateArchive(original, mutation) {
  const bytes = Buffer.from(original);
  const { entries, end } = records(bytes);
  const file = entries.find((entry) => entry.type === '0' && entry.size > 0 && entry.size % 512);
  const dir = entries.find((entry, index) => index > 0 && entry.type === '5');
  const fileName = bytes.subarray(file.offset, file.offset + 100).toString('utf8').replace(/\0.*$/s, '').replace(/^\.\//, '');
  const append = (header) => Buffer.concat([bytes.subarray(0, end), header, bytes.subarray(end)]);
  const directories = {
    'unknown-directory': 'surprise/', 'traversal-directory': '../outside/',
    'absolute-directory': '/outside/', 'drive-directory': 'C:/outside/',
    'duplicate-root': './', 'case-alias-directory': './.NACHOS/',
    'file-directory-conflict': fileName + '/',
    'dot-segment-directory': './.nachos/../outside/', 'double-slash-directory': './.nachos//',
  };
  if (Object.hasOwn(directories, mutation)) return append(directory(bytes, directories[mutation]));
  if (mutation === 'duplicate-directory') return append(bytes.subarray(dir.offset, dir.offset + 512));
  if (mutation === 'invalid-utf8-directory') {
    const header = directory(bytes, 'outside/');
    header[0] = 255;
    checksum(header);
    return append(header);
  }
  if (mutation === 'missing-directory') return Buffer.concat([bytes.subarray(0, dir.offset), bytes.subarray(dir.offset + 512)]);
  if (mutation === 'empty-name-with-prefix') {
    const header = bytes.subarray(dir.offset, dir.offset + 512);
    const name = header.subarray(0, 100).toString('utf8').replace(/\0.*$/s, '').replace(/^\.\//, '').replace(/\/$/, '');
    field(header, 0, 100, '');
    field(header, 345, 155, name);
    checksum(header);
    return bytes;
  }
  if (mutation === 'no-end-blocks') return bytes.subarray(0, end);
  if (mutation === 'one-end-block') return bytes.subarray(0, end + 512);
  if (mutation === 'partial-end-block') return bytes.subarray(0, end + 1023);
  if (mutation === 'nonzero-trailer') return Buffer.concat([bytes, Buffer.alloc(512, 1)]);
  if (mutation === 'partial-zero-trailer') return Buffer.concat([bytes, Buffer.alloc(1)]);
  if (mutation === 'payload-padding') { bytes[file.data + file.size] = 1; return bytes; }
  if (mutation === 'truncated-padding') return bytes.subarray(0, file.data + file.size);
  if (mutation === 'changed-payload') { bytes[file.data] ^= 1; return bytes; }
  const recordSize = 512 + Math.ceil(file.size / 512) * 512;
  if (mutation === 'duplicate-file') return append(bytes.subarray(file.offset, file.offset + recordSize));
  if (mutation === 'missing-file') return Buffer.concat([bytes.subarray(0, file.offset), bytes.subarray(file.offset + recordSize)]);
  const header = bytes.subarray(file.offset, file.offset + 512);
  switch (mutation) {
    case 'bad-magic': field(header, 257, 6, 'notar'); break;
    case 'bad-version': field(header, 263, 2, '99'); break;
    case 'header-padding': header[511] = 1; break;
    case 'name-after-nul': header[99] = 1; break;
    case 'linkname-on-file': field(header, 157, 100, 'hidden-link'); break;
    case 'invalid-mode': field(header, 100, 8, 'bad'); break;
    case 'invalid-uid': field(header, 108, 8, '8'); break;
    case 'invalid-gid': field(header, 116, 8, '-1'); break;
    case 'invalid-time': field(header, 136, 12, '9'); break;
    case 'numeric-terminal-lf': field(header, 100, 8, '0000777\n'); break;
    case 'numeric-terminal-cr': field(header, 108, 8, '0000000\r'); break;
    case 'base256-size': header[124] = 128; break;
    case 'maximum-size': field(header, 124, 12, '77777777777'); break;
    case 'embedded-nul-size': header[125] = 0; break;
    case 'device-number': field(header, 329, 8, '0000001'); break;
    case 'symlink': field(header, 156, 1, '2'); break;
    case 'unknown-file': field(header, 0, 100, './unknown'); break;
    case 'bad-checksum': header[148] ^= 1; return bytes;
    default: throw new Error(`Unknown test mutation: ${mutation}`);
  }
  checksum(header);
  return bytes;
}
