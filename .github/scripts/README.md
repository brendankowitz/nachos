# Documentation validation

Requires Node.js 24.15+ (24.x LTS) and npm. From the repository root:

```powershell
npm --prefix .github\scripts ci --ignore-scripts --no-fund --no-audit
npm --prefix .github\scripts run check
```

On Linux/macOS use `.github/scripts` instead. `npm test` runs CLI-level behavioral
tests and mutation checks against disposable fixture copies beneath
`.github/scripts/.test-runs/`; the copies are removed after each test. Each CLI
invocation has a bounded 60-second timeout for cold-start headroom, with no retries.
`npm run validate` checks the repository's real `docs/` tree. Run either command
from `.github/scripts`, or use the `--prefix` option above. To check a different
repository tree, run `node .github/scripts/validate-docs.mjs PATH_TO_REPOSITORY`.

## Rules

- **Links:** a remark Markdown/GFM syntax tree supplies inline links, images, and
  reference definitions (including unused definitions). All local targets must
  exist. Code spans, indented code, and fenced Markdown examples are not links.
  URL schemes and protocol-relative URLs are ignored; nothing is crawled.
  Percent-encoded paths are decoded, query strings are ignored, and `/`-prefixed
  paths are repository-relative.
- **Fragments:** Markdown heading anchors use GitHub-style slugs, including
  repeated headings and Unicode; raw HTML and image alt text are excluded from
  heading text. Missing anchors fail. Fragments on
  non-Markdown targets fail explicitly as unsupported rather than silently
  passing. Raw HTML links and custom HTML anchors are not supported.
- **Sections:** references such as `§1`, `§1.2`, and `§1.2.3` in
  `docs/superpowers/specs/` must match a numbered Markdown heading in the same
  file, including references in code and comments. Whitespace after the section
  sign may include soft line breaks; diagnostics point to the section-sign line.
  Only the `research/` subtree is excluded from this rule.
- **Placeholders:** whole-word, uppercase `TBD`, `TODO`, and `FIXME` are forbidden
  anywhere in `docs/superpowers/specs/` and `docs/superpowers/plans/`, including
  code and comments, except under `research/`. Other prose is not normative.
- **Mermaid:** every Mermaid fence in `docs/`, including research, is parsed by
  the pinned Mermaid library's actual `mermaid.parse` API. This checks syntax,
  not layout/rendering. jsdom supplies the DOM used by Mermaid's label sanitizer;
  scripts and remote resources are disabled by default. **No browser executable,
  browser download, or separately provisioned browser dependency is required.**

Failures include document paths and line numbers (Mermaid errors identify the
opening fence and include the parser's own diagnostic). Missing dependencies,
missing/empty input trees, unsupported fragments, and parser failures exit
nonzero. Install exactly the public-registry lockfile with `npm ci`; the local
`.npmrc` avoids inheriting a machine-specific registry mirror. There are no skip
switches or success fallbacks.

CI runs both the tests and the real validator for pushes and pull requests
affecting docs, scripts, or the workflow. It uses read-only repository access,
does not persist checkout credentials, and requires no secrets.

## Dependency license exception

[`eng/license-exceptions.json`](../../eng/license-exceptions.json) records the
reviewed EPL-2.0 exception for npm `elkjs` **0.9.3** only, used by Mermaid syntax
validation in `.github/scripts`. The installed
`.github/scripts/node_modules/elkjs/LICENSE.md` contains the Eclipse Public
License v2.0 text.

This exception permits only unmodified, non-distributed developer/CI tooling.
It does **not** permit shipping Mermaid/ELK assets in NuGet packages, CLI or
container outputs, or published docs-site assets. There are no shipping product
artifacts in this PR; this record does not implement or imply emitted-artifact
license scanning.
