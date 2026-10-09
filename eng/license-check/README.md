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
a tooling-excepted package found in **any** emitted artifact fails. The exact
owner-authorized Microsoft-primary shipped records below are separate. GPL/AGPL/LGPL/SSPL
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
MIT file. Unapproved vendor software-license terms and unknown separately delimited
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
must be audited separately when that producer lands; no exact technical admission
is implied by the owner policy.
The owner [approved the scoped DacFx `170.4.83` shipped API/CLI exception](https://github.com/brendankowitz/nachos/issues/2#issuecomment-6048060868).
The checker implements that exact engineering-policy exception, **not** a general
proprietary permission or a finding of redistribution compliance.
The owner's [later Microsoft-library policy acceptance](https://github.com/brendankowitz/nachos/pull/6#issuecomment-6083936795)
also accepts Microsoft primary library terms as engineering policy. It does not
waive origin, identity, complete text, third-party obligations, required copies,
or publisher governing terms. New Microsoft libraries need technical review of
those facts, not another blanket policy question.

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
The `approvalType: "dacfx"` validator admits only NuGet `Microsoft.SqlServer.DacFx`
`170.4.83`, the recorded owner reference, the fixed primary-license fingerprint
and API/CLI artifact scopes. `repository-owner` identifies the role in that
approval reference; it is not an invented source-review author.
The historical record without `approvalType` remains compatible only through
this same exact DacFx validator; it is not a catch-all shipped approval.
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

API/CLI producer owners must retain the same two content items, using `..\..\`
source prefixes, the destinations above as `Link`/target paths,
`CopyToOutputDirectory="PreserveNewest"` and `CopyToPublishDirectory="Always"`.
This policy delivery changes neither project; it does not reassert the older
capture's CLI path defect as a current project defect. CLI package and API image
producers must preserve that layout and
verify their own final contents; a publish-directory check is not image or
tool-package verification.

Missing or changed copies fail. For DacFx, only these two proved source paths receive
notice provenance; there is no general Markdown/text whitelist. The license
fingerprint is byte-exact. The root notice's reviewed engineering copy-contract
fingerprint permits only Markdown line-ending normalization; published bytes
must still equal their source bytes. Notice-content changes require an
intentional update to `DacFxApproval.NoticeHash` and review of the contract.
Copied notice documents alone do not establish that DacFx binaries shipped.
The coordinated notice integration adds the exact SNI/Types entries below to
the root notice while retaining the DacFx conditions. Its normalized SHA-256
copy-contract pin is now
`d727a48b88eba908f2f52b0809277e3f55d27bb322641fb8dfb779b6b9034a04`.
This changes only the reviewed root-notice content fingerprint, not the
DacFx vendor license fingerprint or validator behavior. The root notice now
links all three primary documents but remains an incomplete dependency inventory.
The current SNI heading and links name 6.0.3; the complete SNI issuer terms,
DacFx conditions and Types release qualification are unchanged.

`THIRD-PARTY-NOTICES.md` includes the section 3(b)(ii) protective downstream
agreement and 3(b)(iii) indemnification conditions. Required downstream terms,
assent, indemnification and other redistribution obligations remain an explicit
owner release gate. Neither file copying nor an audit records EULA acceptance
or certifies legal compliance. No integrated DacFx release assent, container
distribution, or complete notice inventory is
established by this policy check.

## Microsoft primary library engineering policy

The retained REST evidence in
`eng/licenses/microsoft-library-policy/owner-ms-license-6083936795.json` binds the
actual untagged instruction by `brendankowitz`, dated `2026-10-09T15:26:15Z`:
“I'm ok accepting any of the Ms library licenses, no issues there”.
Its full SHA-256 is
`7a843523fecd8769a984ed13ca94f6daa891e90c5dda873da504bb9694736753`.
`reviewers: ["brendankowitz"]` identifies that actual policy decision, **not**
an independent technical review. Independent review of this implementation remains
pending. No source-reviewer names or publisher permissions are invented.

`approvalType: "microsoft-primary"` admits these current reviewed NuGet identities:

| Package | Exact version | Primary entry | Reported license |
| --- | --- | --- | --- |
| Microsoft.Data.SqlClient.SNI.runtime | 6.0.3 | `LICENSE.txt` | `LicenseRef-Microsoft-SqlClient-SNI-6.0.3` |
| Microsoft.SqlServer.Types | 170.1000.7 | `license.md` | `LicenseRef-Microsoft-SQL-Server-Types-170.1000.7` |

Historical SNI 6.0.2 retains its own unchanged exact record, descriptor,
documentary evidence and regression cases. It is not asserted to be the current
runtime. Its primary license bytes equal 6.0.3, but its archive and nuspec do
not: neither record authorizes the other version. Current 6.0.3 evidence binds
the official archive SHA-256
`b9df07c20101398f77cf16b209afefafcc7190d6ac0b6e244e81a2fed4c96f5f`
and nuspec SHA-256
`d5384233109efc8ca42e51d7e1f7f3d35d47eb5a878843e3002c77550babcd82`.
The parent retrieved that official exact-version archive and verified equality
with the restored artifact; the supplied bytes were rechecked in this stage.
This is technical origin evidence under the existing owner decision, not a
new publisher-signature verification or grant of rights.

The exception records pin the source/entry path, complete raw primary SHA-256,
reviewed nuspec path/SHA-256, **entire archive SHA-256**, actual owner reference
and retained owner-evidence SHA-256. Adjacent `provenance.json` files explain the
captured Microsoft-origin evidence and its limits. The fixed archive pin is
checked on the same stream used to read the nuspec and primary entry. Altered
payloads/repacking, metadata or duplicate/changed license entries cannot reuse an
approval. Source license/nuspec/owner bytes use `-text` attributes; no license
newline normalization is allowed. URLs are provenance, not audit-time downloads.
This is not a new publisher-signature verification.

There is no `Microsoft.*` or authors-string bypass. Missing/ambiguous primary
text, another publisher/version, unknown approval type, an altered owner
reference, scope or fingerprint fails closed. A primary admission changes only
that primary document's identity to its explicit `LicenseRef`, never MIT.
Generic overrides cannot be combined with it. Supplementary GPL/AGPL/LGPL/SSPL,
unknown components, metadata AND obligations and OR selections retain their
ordinary checks. Other Microsoft packages with MIT metadata but no primary text
do not become accepted. This is separate from complete-document normalization.

The initial records retain the shipped API/CLI boundary: actual distribution
must be proved, and docs/tooling scope is not authorized by these records.
Every containing API/CLI artifact must include the exact source-backed file:

| Repository source and publish-relative destination | SHA-256 |
| --- | --- |
| `eng/licenses/Microsoft.Data.SqlClient.SNI.runtime/6.0.3/LICENSE.txt` | `9335e8bad875dd7be4eebd55d2335eb6433d1cea61aadb3817af7807bef8932a` |
| `eng/licenses/Microsoft.SqlServer.Types/170.1000.7/license.md` | `e4b4088d14de78a57d485d0bc53f3250f3e1d2376993ddb2c5b165eca3d59d40` |

**Current copy integration:** API and CLI target SNI 6.0.3 and Types 170.1000.7 with
`..\..\` source prefixes, exact `Link` paths, `CopyToOutputDirectory="PreserveNewest"`
and `CopyToPublishDirectory="Always"`. There are no `Exists` conditions.
The [specific CLI partner amendment](https://github.com/brendankowitz/nachos/pull/6#issuecomment-6086048102)
authorizes replacing only the SNI source/link and existing notice tuple with
6.0.3. All original DacFx/root-notice/old-path assertions and the actual publish
harness remain intact; the Types item is unchanged. Evidence source files must
land **before or in the same push** as consuming links, never afterward.

Final image/tool-package producers must preserve this layout and verify bytes
there separately; local publish directories are not that evidence. Notice-only
copies never establish that package binaries shipped. Wrong/missing copies
still fail even when policy is accepted. DacFx's primary fingerprint and
two-copy behavior are unchanged; only its root-notice content pin was updated
for the new linked entries. The SNI/Types license copies are additional, not
replacements. No dependency/CPM/lock or CLI runtime change is part of this work.

The actual API and CLI graphs resolve SqlClient 6.1.7 -> **SNI.runtime 6.0.3**.
Its exact record/evidence and the authorized API/CLI item/tuple targets are now
present. A copied historical 6.0.2 document does not satisfy the 6.0.3 path contract,
despite identical primary bytes. Inspect actual API and CLI publishes
independently. Types and DacFx release qualifications remain unchanged, and
neither matching identities nor correct copies establish release clearance.

**Governing terms remain release gates.** SNI's Microsoft Software License Terms
retain their separate Distributable Code conditions, downstream agreements,
indemnification, data duties and supplier notices. Types retains the full
**MICROSOFT PRE-RELEASE SOFTWARE LICENSE TERMS / MICROSOFT SQL SERVER VNEXT
COMMUNITY PREVIEW**, internal-evaluation restrictions and **TERM 09/30/2022**.
Its `governingTerms: "unresolved-pre-release-2022"` qualification is required and
reported in decision evidence. Owner engineering acceptance does **not**
establish production/redistribution rights, supersede that document, resolve a
possible publisher packaging mistake, or record EULA assent. Resolve governing
terms before release; a passing fixture/audit is not that resolution.

The original public064 + held48 captured graph used SqlClient 6.1.5 for SNI and
DacFx 170.4.83 for Types. A later SqlClient 6.1.7 pin does not by itself prove
the current resolved SNI version. Technical identity/notice checks use the
supplied actual inventory; this policy delivery is not a fresh current71 audit.
The frozen report's 4 accepted instances, 958 findings (920 evidence findings)
remain unchanged. Sharp/libvips, DOMPurify choices, Unicode, missing evidence and
other non-Microsoft gates are not waived.

### Offline policy regression fixtures

`MicrosoftPrimaryApprovalTests` uses three genuine nupkgs, not fabricated
Microsoft metadata. Ordinary restore provisions them through exact-version
`PackageDownload` items in the license-check test project: `[6.0.2];[6.0.3]` for
`Microsoft.Data.SqlClient.SNI.runtime` and `[170.1000.7]` for
`Microsoft.SqlServer.Types`. These are **test data downloads**, not
`PackageReference` dependencies; their assemblies, native binaries and build
targets are not used for compilation or runtime, and their dependency graphs
are not added to the test or product graphs.

Build copies the three restored cache archives and the already checked-in
`eng/licenses/microsoft-library-policy/owner-ms-license-6083936795.json` into
`MicrosoftPrimaryEvidence` beside the test assembly. No private session files,
second owner copy, environment variable or test-time networking is required.
Tests verify the pinned complete archive and owner SHA-256 values; missing or
changed fixtures fail explicitly, never skip or fall back to other evidence.
The content items are excluded from pack and publish, and the test project
retains its existing `IsPackable=false`.

Normal developer and CI entry points use their configured NuGet sources
(normally nuget.org) during restore. For deterministic offline validation, use
an explicitly configured local feed containing the exact existing packages;
do not substitute same-name archives or disable fingerprint checks. An empty
cache needs that configured source, not a prepopulated private fixture folder.
Run from the source checkout (the repository-record and restore-graph tests
also consume checked-in/generated evidence relative to ordinary test output):

```powershell
dotnet restore test\Nachos.LicenseCheck.Tests
dotnet build test\Nachos.LicenseCheck.Tests -c Release --no-restore `
  -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false -nr:false
dotnet test test\Nachos.LicenseCheck.Tests -c Release --no-restore `
  --no-build -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false -nr:false
```

`dotnet test test\Nachos.LicenseCheck.Tests -c Release` also performs the normal
implicit restore and build. Existing CI test entry points need no separate
fixture provisioning step. As usual, `--no-restore` requires a successful
restore and `--no-build` requires a matching successful build.

The fixture archives are not committed or made product dependencies. Restoring
them as test data does not grant distribution rights, resolve Types' governing
terms, wire API/CLI notices, or establish a green real-artifact audit.

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
Entry admission checks links before directory classification, including dangling
and file-target links. Store/context/scoped containers must be physical
directories; non-directory dependency slots fail. Regular store-root metadata
files are not package slots. `.bin` and physical packages directly under
top-level `node_modules/<name>` remain outside this installed-store check;
license decisions still require the locked archives and shipped bundle inventory.
Peer contexts are reconciled in both directions: optional absence cannot leave
a bound context, and every transitive name must trace to a child's declared
peer requirement. Forwarded bindings must agree exactly; locally provided peers
and genuinely unresolved optional transitive peers remain supported. A cycle of
transitive declarations without an originating peer requirement is not evidence.
Only effective **external** peer requirements propagate: supported removal
overrides and ordinary/optional dependencies declared by the child's archive
can satisfy or remove a requirement. A resolved snapshot edge alone cannot
prove local satisfaction, since external peers also appear in those edges.
Collection first loads integrity-verified archives, validates their identities
and peer/platform declarations against the lock, and caches the decoded evidence.
Graph reconciliation then uses those archive declarations regardless of package
enumeration order. Missing or rejected child metadata fails explicitly; stale
transitive annotations cannot recreate a removed or locally satisfied requirement.
Semver build metadata is ignored for precedence only after every dot-separated
identifier has been validated as nonempty ASCII alphanumeric/hyphen text, for
both range tokens and exact locked/archive identities.

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
