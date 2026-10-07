import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';

const scripts = import.meta.dirname;
const fixture = join(scripts, 'fixtures', 'valid');
const runs = join(scripts, '.test-runs');
const spec = 'docs/superpowers/specs/design.md';
mkdirSync(runs, { recursive: true });

function withFixture(action) {
  const root = mkdtempSync(join(runs, 'case-'));
  try {
    cpSync(fixture, root, { recursive: true });
    return action(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function run(root, script = join(scripts, 'validate-docs.mjs')) {
  const result = spawnSync(process.execPath, [script, root], {
    encoding: 'utf8',
    timeout: 60_000,
  });
  assert.ifError(result.error);
  return { status: result.status, output: result.stdout + result.stderr };
}

function append(root, path, text) {
  const file = join(root, path);
  const previous = readFileSync(file, 'utf8');
  writeFileSync(file, previous + text);
  return previous.split('\n').length;
}

test('valid Markdown, references, fragments, and real Mermaid grammars pass', () => {
  withFixture((root) => {
    const result = run(root);
    assert.equal(result.status, 0, result.output);
    assert.match(result.output, /4 Markdown files/);
    assert.match(result.output, /3 Mermaid blocks/);
  });
});

test('image-alt exclusion accepts the GitHub badge heading fragment', () => {
  withFixture((root) => {
    append(root, 'docs/guide.md', '\n# Awesome [![Awesome](https://example.invalid/badge.svg)](https://example.invalid)\n\n[Jump](#awesome-)\n');
    const result = run(root);
    assert.equal(result.status, 0, result.output);
  });
});

test('image-alt exclusion rejects the nonexistent alt-text heading fragment', () => {
  withFixture((root) => {
    const firstLine = append(root, 'docs/guide.md', '\n# Awesome [![Awesome](https://example.invalid/badge.svg)](https://example.invalid)\n\n[Jump](#awesome-awesome)\n');
    const result = run(root);
    assert.equal(result.status, 1, result.output);
    assert.ok(result.output.includes(`docs/guide.md:${firstLine + 3}:1 [fragment]`), result.output);
    assert.match(result.output, /Missing heading fragment "awesome-awesome"/);
  });
});

test('wrapped section reference accepts an existing numbered heading', () => {
  withFixture((root) => {
    append(root, spec, '\nSee §\n1.1.1.\n');
    const result = run(root);
    assert.equal(result.status, 0, result.output);
  });
});

for (const [context, text, signLine] of [
  ['prose', '\nSee §\n999.2.\n', 1],
  ['code', '\n```text\nSee §\n999.2.\n```\n', 2],
  ['comment', '\n<!-- See §\n999.2. -->\n', 1],
]) {
  test(`wrapped section reference rejects a missing heading in ${context} at the section-sign line`, () => {
    withFixture((root) => {
      const firstLine = append(root, spec, text);
      const result = run(root);
      assert.equal(result.status, 1, result.output);
      assert.ok(result.output.includes(`${spec}:${firstLine + signLine}:1 [section]`), result.output);
      assert.match(result.output, /No numbered heading for section 999\.2/);
    });
  });
}

const mutations = [
  ['missing inline target', spec, '\n[Broken](missing.md)\n', 'link', 'missing.md'],
  ['missing reference target', spec, '\n[Broken][bad]\n\n[bad]: missing.md\n', 'link', 'missing.md'],
  ['missing collapsed target', spec, '\n[bad][]\n\n[bad]: missing.md\n', 'link', 'missing.md'],
  ['missing shortcut target', spec, '\n[bad]\n\n[bad]: missing.md\n', 'link', 'missing.md'],
  ['missing table target', spec, '\n| Link |\n| --- |\n| [bad](missing.md) |\n', 'link', 'missing.md'],
  ['missing image target', spec, '\n![Broken](missing.png)\n', 'link', 'missing.png'],
  ['missing fragment', spec, '\n[Broken](../../guide.md#absent)\n', 'fragment', 'absent'],
  ['missing self fragment', spec, '\n[Broken](#absent)\n', 'fragment', 'absent'],
  ['invalid URL encoding', spec, '\n[Broken](bad%ZZ.md)\n', 'link', 'bad%ZZ.md'],
  ['unsupported asset fragment', spec, '\n[Asset](../../asset%20name.txt#absent)\n', 'fragment', 'absent'],
  ['missing section', spec, '\nSee §999.2.\n', 'section', '999.2'],
  ['code section reference', spec, '\n```text\nSee §999.2.\n```\n', 'section', '999.2'],
  ['code heading cannot satisfy section', spec, '\nSee §404.\n', 'section', '404'],
  ['different document cannot satisfy section', spec, '\nSee §2.\n', 'section', '2'],
  ['malformed Mermaid grammar', spec, '\n```mermaid\nflowchart LR\n A[broken\n```\n', 'mermaid', 'Parse error'],
  ['unknown Mermaid type', spec, '\n```mermaid\nnot-a-real-diagram\n```\n', 'mermaid', 'diagram'],
  ['research Mermaid is checked', 'docs/superpowers/specs/research/evidence.md', '\n```mermaid\nflowchart LR\n A[broken\n```\n', 'mermaid', 'Parse error'],
  ['research links are checked', 'docs/superpowers/specs/research/evidence.md', '\n[Broken](missing.md)\n', 'link', 'missing.md'],
  ['spec placeholder', spec, '\nThis is TBD.\n', 'placeholder', 'TBD'],
  ['plan placeholder', 'docs/superpowers/plans/plan.md', '\n- TODO\n', 'placeholder', 'TODO'],
  ['code placeholder', spec, '\n```text\nFIXME\n```\n', 'placeholder', 'FIXME'],
  ['comment placeholder', spec, '\n<!-- TODO -->\n', 'placeholder', 'TODO'],
];

for (const [name, path, text, kind, subject] of mutations) {
  test(`mutation rejects ${name} with a path/line diagnostic`, () => {
    withFixture((root) => {
      // This heading belongs to another document, never to the spec.
      append(root, 'docs/guide.md', '\n## 2. Elsewhere\n');
      const firstLine = append(root, path, text);
      const result = run(root);
      assert.equal(result.status, 1, result.output);
      const diagnostic = result.output.split('\n').find(
        (line) => line.startsWith(`${path}:`) && line.includes(`[${kind}]`) && line.includes(subject),
      );
      assert.ok(diagnostic, result.output);
      const line = Number(diagnostic.slice(path.length + 1).split(':')[0]);
      assert.ok(line >= firstLine, diagnostic);
      assert.ok(line <= firstLine + text.split('\n').length, diagnostic);
    });
  });
}

test('missing docs directory fails instead of validating zero inputs', () => {
  withFixture((root) => {
    rmSync(join(root, 'docs'), { recursive: true });
    const result = run(root);
    assert.equal(result.status, 1, result.output);
    assert.match(result.output, /docs.*ENOENT|ENOENT.*docs/s);
  });
});

test('empty docs directory fails instead of validating zero inputs', () => {
  withFixture((root) => {
    rmSync(join(root, 'docs'), { recursive: true });
    mkdirSync(join(root, 'docs'));
    const result = run(root);
    assert.equal(result.status, 1, result.output);
    assert.match(result.output, /No Markdown files/);
  });
});

test('an unavailable dependency fails rather than skipping validation', () => {
  withFixture((root) => {
    const isolated = join(root, 'isolated');
    mkdirSync(isolated);
    cpSync(join(scripts, 'validate-docs.mjs'), join(isolated, 'validate-docs.mjs'));
    writeFileSync(join(isolated, 'package.json'), '{"type":"module"}');
    // An explicit broken local package blocks ancestor node_modules resolution.
    const missing = join(isolated, 'node_modules', 'mermaid');
    mkdirSync(missing, { recursive: true });
    writeFileSync(join(missing, 'package.json'), '{"type":"module","exports":"./unavailable.mjs"}');
    const result = run(root, join(isolated, 'validate-docs.mjs'));
    assert.equal(result.status, 1, result.output);
    assert.match(result.output, /ERR_MODULE_NOT_FOUND/);
    assert.match(result.output, /mermaid/);
  });
});
