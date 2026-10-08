# License audit — M1 first delivery

Build `test/Nachos.LicenseCheck.Tests` (or the solution) in Release, restore/install
the locked dependencies, and publish **both** applications to fresh directories:

```powershell
dotnet publish src/Nachos.Api -c Release --no-restore -o eng/license-check/artifacts/api
dotnet publish src/Nachos.Cli -c Release --no-restore -o eng/license-check/artifacts/cli
npm --prefix docs/site run build
dotnet eng/license-check/bin/Release/net10.0/Nachos.LicenseCheck.dll fetch-npm `
  --lock .github/scripts/package-lock.json --npm-archives eng/license-check/artifacts/npm-archives
dotnet eng/license-check/bin/Release/net10.0/Nachos.LicenseCheck.dll fetch-npm `
  --lock docs/site/package-lock.json --npm-archives eng/license-check/artifacts/npm-archives
pwsh eng/license-check/Check-Licenses.ps1 `
  -ApiPublishRoot eng/license-check/artifacts/api `
  -CliPublishRoot eng/license-check/artifacts/cli `
  -NpmArchives eng/license-check/artifacts/npm-archives
```

The wrapper generates `dotnet list package --include-transitive --format json
--no-restore` output and resolves the NuGet cache location. It does not install,
restore, or publish. Collection is offline unless explicit `-PnpmLocks` inputs
request archive provisioning as described below. `-NugetInventory` and `-NugetCache` allow
explicit existing inputs. Results go to `eng/license-check/artifacts` (override
with `-OutputDirectory`). Exit 0 means every discovered input passed; exit 1 is an
audit failure; invalid command-line arguments return 2. Failed packages and input
errors are explicit in `license-report.json` and stderr.

## Evidence and distribution boundaries

- NuGet: inventory must cover every solution project and match each
  `obj/project.assets.json` restore graph. Identity, metadata and license entries
  are read from each resolved `.nupkg`; no metadata-only approval.
- npm: discover every `package-lock.json` outside dependency/build outputs.
  Require a root record matching the manifest and closure of required dependency
  and peer edges, resolving nested/hoisted locations. Missing optional edges and
  explicitly optional peers are allowed, but **every existing locked record is
  audited**, including incompatible platforms. Package identity, license and
  dependency declarations must agree with installed or verified-archive metadata.
  Root npm version overrides follow logical dependency edges, retaining nested
  ancestor context even for hoisted packages. Exact effective versions are checked
  in every reachable context; metadata still matches the original lock declarations.
  This is not a replacement semver solver: npm's fresh lock-consistent
  installation resolves ordinary ranges. Nested/scoped packages are
  supported; npm workspaces/links, aliases and unsupported lock formats fail explicitly.
  Tooling must be freshly installed using `npm ci
  --ignore-scripts`; the elkjs exception is **only** for unmodified
  `.github/scripts`, version 0.9.3 and its existing reviewers.
- pnpm: discover `pnpm-lock.yaml` only with its exact declared pnpm manager and
  supported two-document contract below. Both manager and application graphs
  require verified archives, including optional/non-host and installed packages.
  npm/pnpm dual locks or a missing/mismatched declared lock fail rather than
  selecting another manager's evidence. Existing `.github/scripts/package-lock.json`
  remains required; this collector does not migrate that producer.
- Python: each `requirements.lock` requires `-PythonArchives <directory>`.
  The collector reads wheel/zip-sdist/tar.gz-sdist metadata and license entries
  only, **never SDK implementation source**. Exact `name==version` lines and
  continued `--hash=sha256:...` values are supported; ambiguous requirements,
  unsupported formats, missing archives, identity and hash mismatches fail.
  Modern `License-Expression`, legacy `License` and licensing classifiers are
  reconciled; conflicting, ambiguous or prohibited declarations are not ignored.
  Wheels require one top-level metadata identity. Repeated sdist metadata must
  agree on identity, licensing and declared evidence paths. Vendored licenses
  are supplemental, not package-primary evidence.
  No Python lock means no Python audit is claimed.
- API/CLI: explicit publish roots require their respective `Nachos.Api.deps.json`
  and `Nachos.Cli.deps.json`, their project-library entries, and application
  assemblies. An empty substitute deps file is not provenance. All package libraries are distributed. Copied files
  are matched by bytes against **all** resolved NuGet archives (including
  tooling); unknown content provenance fails. First-party project outputs,
  generated host/config files, and unchanged project appsettings are recognized.
- Docs: if `docs/site` has a package manifest/lock, its producer must emit
  `docs/site/dist/.nachos/bundle-modules.json`: a JSON array of **unique**
  `{ "package": "...", "version": "..." }` pairs, sorted ordinally by package,
  then version. Every package represented in a client chunk or copied asset must
  appear. `[]` is valid only for a producer that emitted no third-party assets.
  The checker consumes this provenance; it does not forge it, infer it from dev
  flags, or assume another workflow's filesystem exists.

The allowlist is spec §3. EPL/MPL require an exact reviewed tooling exception;
a tooling-excepted package found in **any** emitted artifact fails. The one
owner-authorized shipped exception below is separate. GPL/AGPL/LGPL/SSPL
in metadata or license text fail before any override or SPDX choice is applied.
License text recognition consumes **complete normalized canonical documents**,
not identifying fragments, titles, SPDX tags or URLs. Templates and full offline
fixtures come from SPDX `license-list-data` release `v3.27.0`.
`Import-LicenseTemplates.ps1` refreshes that reference data explicitly; it never
runs during audit/CI, approves observed packages, or creates review entries.
Normalization is limited to whitespace/case, typographic quotation marks,
decorative Markdown, exact notice headings, the nonoperative sentence
`This license is GPL-compatible.`, and copyright **years**. MIT/ISC titles may be
omitted; operative paragraphs may not. Arbitrary copyright-holder text is **not**
deleted: it can conceal restrictions. Attribution variants outside these
templates therefore require genuine reviewed exact-version evidence.
Truncation, modified grants and unexplained appended terms fail closed.
Supplemental notices do not establish a package's primary license, and a
missing explicitly declared license file cannot be replaced by an unrelated
MIT file. Vendor software-license terms and unknown separately delimited
licensing sections remain disallowed evidence even when the same document
also contains a permissive third-party notice. A wrapper or repository's MIT
license does not relicense its dependencies or copied binaries.
Primary metadata is reconciled only against primary evidence. Supplemental
obligations are then evaluated conjunctively; unknown or prohibited supplemental
terms cannot be erased by a primary-license override.

For example, the published [DacFx 170.5.96 license file](https://www.nuget.org/packages/Microsoft.SqlServer.DacFx/170.5.96/License)
contains Microsoft SQL Server Data-Tier Application Framework software-license
terms, not MIT. Microsoft.Build.Sql's wrapper license is not evidence that
these terms are permissive. Its future exact dependency and publish inventory
must be audited separately when that producer lands; no exception is implied.
The owner [approved the scoped DacFx `170.4.83` shipped API/CLI exception](https://github.com/brendankowitz/nachos/issues/2#issuecomment-6048060868).
The checker implements that exact engineering-policy exception, **not** a general
proprietary permission or a finding of redistribution compliance.

SPDX `AND`, `OR` and parentheses are parsed strictly (`WITH` is unsupported and
fails). Every selected AND obligation must qualify; OR requires a recorded
complete branch. A reviewed `eng/license-overrides.json` entry has `ecosystem`,
`package`, exact `version`, `license`, `evidenceUrl` (HTTPS), nonempty `reviewers`,
and `review` (HTTPS); `selectedLicense` records an OR choice. Duplicate or
unreviewed entries fail. Overrides can supply missing text or resolve metadata
disagreement, but cannot erase observed components, declared AND obligations or
prohibited licenses. **The checker never writes reviews or exceptions.**

## Exact DacFx approval and notice-copy contract

`eng/license-exceptions.json` records the authoritative owner approval with
`tier: "shipped"`, mapped explicitly to the audit's `distributed` classification.
The shipped-record validator admits only NuGet `Microsoft.SqlServer.DacFx`
`170.4.83`, the recorded owner reference, the fixed primary-license fingerprint
and API/CLI artifact scopes. `repository-owner` identifies the role in that
approval reference; it is not an invented source-review author.
The corresponding amended spec is recorded at commit
`b8f04881b8137cf87acb95c347a0d2a1dd487cce`.

The exact `license.txt` bytes were extracted from the existing public NuGet
package without installation or execution. They are retained at
`eng/licenses/Microsoft.SqlServer.DacFx/170.4.83/license.txt`, with provenance in
the adjacent JSON file. SHA-256:
`f6b3be3e53b8b6836b9c08fee9e9fb24e698df2ab2a7e4afd1f77ebf10c22a83`.
The local attributes file prevents Git from rewriting this vendor text's line
endings. The checker independently verifies both the source bytes and the
collected nupkg's primary entry; a matching title or metadata assertion is
insufficient. It reports `LicenseRef-Microsoft-SQL-Server-DacFx-170.4.83`, never
MIT. Only that exact primary document is recognized through the exception.
Supplemental documents, primary disagreement, AND obligations, OR choices and
prohibited components retain their ordinary checks. Generic license-evidence
overrides cannot be combined with this approval.

Every API/CLI publish containing this package must include these exact
source-backed files at these relative paths:

| Repository source | Publish-relative destination |
| --- | --- |
| `THIRD-PARTY-NOTICES.md` | `THIRD-PARTY-NOTICES.md` |
| `eng/licenses/Microsoft.SqlServer.DacFx/170.4.83/license.txt` | `eng/licenses/Microsoft.SqlServer.DacFx/170.4.83/license.txt` |

The API project now copies both to build and publish output. **CLI handoff to
Cortado:** add the same two content items in `src/Nachos.Cli/Nachos.Cli.csproj`,
using `..\..\` source prefixes, the destinations above as `Link`/target paths,
`CopyToOutputDirectory="PreserveNewest"` and
`CopyToPublishDirectory="Always"`. The CLI project is not changed in this
delivery. CLI package and API image producers must preserve that layout and
verify their own final contents; a publish-directory check is not image or
tool-package verification.

Missing or changed copies fail. Only these two proved source paths receive
notice provenance; there is no general Markdown/text whitelist. The license
fingerprint is byte-exact. The root notice's reviewed engineering copy-contract
fingerprint permits only Markdown line-ending normalization; published bytes
must still equal their source bytes. Notice-content changes require an
intentional update to `DacFxApproval.NoticeHash` and review of the contract.
Copied notice documents alone do not establish that DacFx binaries shipped.

`THIRD-PARTY-NOTICES.md` includes the section 3(b)(ii) protective downstream
agreement and 3(b)(iii) indemnification conditions. Required downstream terms,
assent, indemnification and other redistribution obligations remain an explicit
owner release gate. Neither file copying nor an audit records EULA acceptance
or certifies legal compliance. The current API is still a skeleton: no integrated
DacFx deployment, container distribution, or complete notice inventory is claimed.

## npm archive evidence and provisioning

Supported npm version overrides are exact replacement versions and nested package
scopes, optionally qualified by an exact version. A qualified selector matches
the **original edge request**, not the overridden result: exact requests and the
same-base `~x.y.z` intersection used by the current tooling graph are supported.
Other qualified-selector intersections, range selectors/replacements, `$`
references and `.` forms fail explicitly. Direct dependencies cannot be changed
to a different specification through overrides. These are package-version
resolution rules, not license-policy approvals.

If an installed package directory exists, it must be valid; a broken installation
does not silently fall back. Only absent directories use `-NpmArchives`.
Installed metadata and license files are containment/reparse-checked before
reading; license directories are checked before descending into them. Linked
files and junctions fail rather than importing external evidence.
The separate `fetch-npm --lock <path> --npm-archives <directory>` command downloads
those missing records without installing, extracting or executing anything.
Audit collection and fixture tests are offline. CI provisions both current locks
before auditing; new lock producers must add their own fetch step.

Both fetch and audit require an HTTPS `registry.npmjs.org` package/version tarball
URL without credentials, custom port, query or fragment, plus locked SHA-512 SRI.
Redirects and other registries fail. Cache files are named by lowercase
SHA-256 **of the decoded SHA-512 digest bytes**, plus `.tgz`. Existing cache files
are verified rather than silently replaced. Downloads use a two-minute timeout
including body streaming and bounded `.partial` files in that cache, cleaned on
failure and moved into place only after verification.

The shared npm/pnpm tar reader verifies integrity on the same open stream it reads, with limits
of 128 MiB compressed, 512 MiB expanded, 20,000 entries and 2 MiB per metadata or
license document. Absolute/traversal/drive/backslash paths, duplicates, links,
special entries, malformed archives and identity/license disagreement fail.
An archive must have one root: `package`, the exact package basename, or, only
for `@types/*`, its basename followed by ` v<major>.<minor>` matching the locked
version. These bounded forms cover actual DefinitelyTyped archives without
accepting arbitrary or mixed roots; evidence paths retain the actual root.
Empty regular implementation files are valid, but empty license documents
remain evidence and fail ordinary license recognition.
Only package metadata and documentary evidence are decoded; skipped source bytes
still count toward the expanded limit. Implicit npm source/update scripts such
as `license-update.mjs` are ignored, never executed or used as license evidence.
Only known implementation extensions are excluded implicitly. Remaining
license-like candidates with unsupported suffixes (for example `COPYING.GPL`
or `LICENSE.custom`) fail explicitly, in installations and verified archives;
an unsupported document never disappears because its suffix is unfamiliar.
Explicit `SEE LICENSE IN` declarations still require a safe supported document;
missing or source-like declarations fail.
`.BSD` is a supported documentary suffix, including pnpm's vendored
`packaging/LICENSE.BSD`; its complete contents still require recognition and
conjunctive evaluation. A filename never grants permission.

## pnpm 12 evidence: two-document lock v9

This is a concrete collector, not a package-manager migration or general pnpm
lock reader. The lock directory must also contain `package.json` and
`pnpm-workspace.yaml`; `.npmrc` is optional. It supports the observed pnpm 12.8.2 shape:

- Exact `packageManager: "pnpm@12.x.y"` and exactly two ordered YAML documents:
  manager first, application second, both `lockfileVersion: '9.0'`.
- One `.` importer in each document. The manager has empty `configDependencies`
  and one matching `packageManagerDependencies.pnpm` pin. Application root
  dependency/dev/optional declarations are exact pins matching both importer
  specifiers and resolved snapshot identities.
- Application settings `autoInstallPeers: true` and
  `excludeLinksFromLockfile: false`. Workspace overrides must match the lock.
  Supported selectors are a package, `parent>child`, or
  `parent@exact-version>child`, with an exact replacement or `-` removal.
  Conflicting applicable overrides fail. Manager dependencies are not rewritten
  by application overrides, and a removed edge never hides another locked record.
- Public registry SHA-512 records, package snapshots, nested peer contexts,
  required/optional edges, platform declarations and archive metadata must agree.
  Supported dependency ranges are registry semver exact/partial/wildcard,
  comparator, caret, tilde, hyphen and OR forms, with prerelease exclusion.
  Optional peer metadata declared by the archive without a range is reconciled
  with pnpm's explicit `*`; absent archive declarations are never synthesized.

Multi-importer workspaces, aliases, file/git/URL dependencies, patched/config
dependencies, other registries, unrecognized fields and alternative layouts
fail explicitly. The supported workspace keys are `overrides`,
`minimumReleaseAge` and `minimumReleaseAgeStrict`; age settings are neither
changed nor enforced by archive fetching. The only supported non-comment
`.npmrc` directives are the public registry, `save-exact=true` and
`engine-strict=true`. Fresh/frozen installation and release-age enforcement
remain the producer's responsibility.

YamlDotNet is pinned centrally and referenced without a project-local version.
Parsing uses YAML data nodes, not CLR object deserialization. Anchors, aliases,
explicit tags, duplicate/merged/non-scalar keys and malformed mappings fail.
Input limits are 8 MiB per file, depth 64, 250,000 parser events and 65,536
characters per scalar.

```powershell
$checker = 'eng\license-check\bin\Release\net10.0\Nachos.LicenseCheck.dll'
dotnet $checker inventory-pnpm --lock path\to\pnpm-lock.yaml --report pnpm-inventory.json
dotnet $checker fetch-pnpm --lock path\to\pnpm-lock.yaml --npm-archives path\to\npm-archives
pwsh eng\license-check\Check-Licenses.ps1 `
  -ApiPublishRoot path\to\api -CliPublishRoot path\to\cli `
  -NpmArchives path\to\npm-archives -PnpmLocks path\to\pnpm-lock.yaml
```

The explicit wrapper fetch is optional: omit `-PnpmLocks` when the cache has
already been provisioned. It requires `-NpmArchives` and propagates provisioning
failures. Discovery still audits every relevant lock under the repository, not
only the locks named for fetching. Each inventory record reports document,
package/version, SRI, canonical archive URL and all snapshot keys. Every
occurrence/context is validated before identity-level policy deduplication;
conflicting cross-document metadata or integrity cannot be hidden.

Unlike npm's absent-install fallback, pnpm archives are mandatory for **all**
locked identities. This covers the manager graph and non-host native binaries
without relying on the host installation or hashed peer-directory names.
Collection decodes only metadata and documentary evidence, never package
implementation source, and does not extract, install or execute packages.
Existing physical packages under `node_modules/.pnpm/<context>/node_modules`
must have locked identities and metadata/documents matching the verified
archives. Changed, missing or additional nested documents fail. Linked
metadata/documents and escaping or broken store dependency links fail; ordinary
contained dependency links are not mistaken for separate physical packages.
Store directories must bind to a locked snapshot, using pnpm 12's exact
scoped/nested-peer encoding and SHA-256-shortened names where applicable.
Physical slot names and metadata must match that context's own package.
Every dependency or shared-hoist link must resolve to an indexed physical
package with the declared name and exact snapshot binding, not merely an
existing directory within the store. Shared `node_modules` aliases are checked;
unbound physical packages there are not skipped. Every indexed package still
requires its archive metadata and documentary comparison before an audit passes.
Peer contexts are reconciled in both directions: optional absence cannot leave
a bound context, and every transitive name must trace to a child's declared
peer requirement. Forwarded bindings must agree exactly; locally provided peers
and genuinely unresolved optional transitive peers remain supported. A cycle of
transitive declarations without an originating peer requirement is not evidence.

Docs distribution classification still comes from the actual bundle manifest.
Archive availability, graph reconciliation and absence from that manifest do
not approve a license. Complete primary evidence, supplemental/prohibited
checks, OR decisions, exact existing exceptions and API/CLI producer gates are
unchanged. The real graph can therefore be fully inventoried/fetched while the
license audit correctly remains red. This delivery changes neither live
workflows nor producer package-manager configuration.

## Remaining M1 integration

The initial CI builds the current solution, runs the implemented architecture
and license suites, and creates API/CLI/docs artifacts inside its license job.
It is not the final Task 17 gate. SQL integration, SQL-mode API/idempotency, both
dacpacs and SQL2025 DeployReport, FTS/AppHost, all subsequent portable suites,
offline Bicep validation, and SDK conformance/download producers remain to wire
as their tasks land. Packages/images and new output formats also need explicit
artifact collectors when introduced; the current publish roots are not evidence
for future container layers or packed NuGet contents.
Their producers must also package the required license texts/notices, with the
repository notice inventory in `THIRD-PARTY-NOTICES.md`; this audit is not a
replacement for that notice-generation/distribution work.

NuGet currently uses the **resolved restore graph**, not committed
`packages.lock.json` files (not yet present in the skeleton). Repository owners
still need to introduce committed restore locks and locked-mode CI. Neither
that pending producer work nor real license-evidence failures are waived by
passing checker fixture tests.
