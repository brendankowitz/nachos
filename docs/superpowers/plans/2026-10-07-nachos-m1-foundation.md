# Nachos M1 — Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: execute each owned task with the `implement-task-next` skill (task slug `issue-2-<name>`), under the `multi-agent-pr-next` protocol. Steps use checkbox (`- [ ]`) syntax for tracking. Do not start a task until every task in its **Depends on** line is merged on the shared branch.

**Goal:** A deployable Nachos skeleton:
- Honcho-v3-compatible CRUD for workspaces, peers, sessions, membership, and messages;
- scoped auth, health, and the bootstrap CLI;
- SQL Server and in-memory providers;
- Aspire local dev, offline-validated Bicep/azd;
- README and docs-site scaffold;
- upstream-SDK CRUD conformance.

**Architecture:**
- `Nachos.Abstractions` holds the wire contracts, domain records, store interfaces, and filter AST, shared by every layer.
- `Nachos.Core` implements `INachosClient` over `IMemoryStore`.
- Two providers implement the store: `DataLayer.SqlServer` (EF Core query mapping plus SqlClient, with a DacFx-deployed schema) and `DataLayer.InMemory`.
- `Nachos.Api` exposes `/v3` with two auth schemes, and `Nachos.Client` mirrors it over HTTP.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 (SqlServer), Microsoft.Data.SqlClient, Microsoft.Build.Sql, Microsoft.SqlServer.DacFx, Microsoft.ML.Tokenizers, Microsoft.Identity.Web, System.CommandLine, .NET Aspire, Bicep/azd, xUnit 2.9.3 + Shouldly + NSubstitute, Testcontainers.MsSql, pytest + `honcho-ai`, node:test + `@honcho-ai/sdk`, Astro + Starlight.

**Spec:** [`docs/superpowers/specs/2026-10-07-nachos-design.md`](../specs/2026-10-07-nachos-design.md) · Roadmap: [`2026-10-07-nachos-roadmap.md`](2026-10-07-nachos-roadmap.md)

## Global Constraints

The [roadmap's Global Constraints](2026-10-07-nachos-roadmap.md#global-constraints) apply to every task. In addition:

- **IDs** (workspace, peer, session) match `^[a-zA-Z0-9_-]+$`, length 1–512. Anything else → 422.
- **IDs are case-sensitive:** `Name` and `PublicId` columns use `COLLATE Latin1_General_100_BIN2_UTF8`, and the in-memory provider uses ordinal comparison.
- **Message batch:** 1–100 messages. `content` length 0–25,000. `created_at` is optional (backdating is allowed). `token_count` is computed with `o200k_base`.
- **Pagination:** `page` ≥ 1 (default 1), `size` 1–100 (default 50), `reverse` (default false). Envelope `{items,total,page,size,pages}` with `pages = ceil(total/size)` (0 when `total = 0`).
- **Errors:**
  - Request-shape validation → `422 {"detail":[{"loc":[…],"msg":"…","type":"…"}]}` (HTTPValidationError shape).
  - Domain errors → `{"detail":"…"}` with 404 / 409 / 422.
  - **Every** auth failure, including scope mismatch → `401 {"detail":"…"}`.
  - All error bodies also carry the RFC 9457 `type`, `title`, and `status` fields.
- **Configuration limits:** `summary.messages_per_short_summary` ≥ 10 and `messages_per_long_summary` ≥ 20. Other configuration fields are nullable booleans or strings exactly as in the wire manifest.
- **Get-or-create** returns 200 whether the resource is new or existing. An existing resource is returned unchanged, except that `SessionCreate.peers` always ensures membership and sets each listed peer's config.
- **Message create** get-or-creates sender peers and adds them as session members (`JoinedAt = now`) if they aren't members already. Messages are ordered by `Seq` (allocated atomically per session), never by `created_at`.
- **Filters:**
  - Unknown top-level filter keys on list endpoints are **ignored**. Value-type errors → 422.
  - `NOT [c1..cn]` means `NOT(c1 OR … OR cn)`, and it includes rows where the field is unset.
  - `"*"` matches all. A bare list means `in`, except inside `metadata`, where it means containment. An empty `in` matches nothing.
- **Owner gates (spec §18.3):** the only M1 Azure actions are listed in "Owner actions requested" at the end of this plan. No task runs them.

## Ownership and sequencing

| Task | Owner | Depends on |
|---|---|---|
| 1 Solution skeleton, ServiceDefaults, wire manifest | Cortado | — |
| 2 Abstractions: contracts, records, store interfaces, store contract tests | Cortado | 1 |
| 3 Filter AST, parser, conformance cases | Cortado | 2 |
| 4 Core: services, validation, tokens, config resolver, hosting | Cedar | 2, 3 |
| 5 Database projects (Azure + Sql2025) | Cortado | 1 |
| 6 SchemaDeployer | Cortado | 5 |
| 7 SQL Server provider | Cortado | 2, 3, 4 (`NachosBuilder`), 6 |
| 8 In-memory provider | Salsa | 2, 3, 4 (`NachosBuilder`) |
| 9 API: routes, JSON, errors, pagination, health, 501s | Cedar | 4, 8 |
| 10 Auth: NachosKey + Entra, keys and grants routes | Cedar | 9 |
| 11 Idempotency-Key on message create | Cedar | 9; 7 for SQL |
| 12 .NET client | Salsa | 9, 10, 11 |
| 13 Bootstrap CLI | Cortado | 6, 7, 10 (`IKeyIssuer`) |
| 14 Aspire AppHost + FTS image recipe | Cortado | 7, 9 |
| 15 Bicep + azd (offline) | Salsa | 1 (Bicep, `azure.yaml`, `InfraTests`); 13 (hook CLI verbs; until 13 lands, hooks are written against the documented verbs and only syntax-checked) |
| 16 Upstream-SDK conformance | Salsa | 10, 11, 13 |
| 17 CI build/test/schema/license workflow | Cedar | 1 (extend as tasks land) |
| 18 README, logo, docs site, DocsGen, Pages workflow | Cedar | 1 (site scaffold, logo, Pages workflow, README skeleton); 9 + 13 (DocsGen reference, README feature list, getting-started commands) |

Parallel tracks after Task 2:
- **Cortado:** 3 → 5 → 6 → 7 → 13 → 14.
- **Cedar:** 17 and the 18 scaffold can start right after Task 1. Then 4 → 9 → 10 → 11 → 18 content.
- **Salsa:** 15 can start right after Task 1. Then 8 (after Task 4) → 12 → 16.

**Three agents (Cortado, Cedar, Salsa).** Each agent owns exactly the files in its tasks' **Files** lists. Shared files are assigned as follows:
- `Directory.Packages.props` and `Nachos.slnx`: owned by Cortado. Other agents request additions in a PR comment, and Cortado applies them within one heartbeat.
- Abstractions: additions requested the same way.
- **Fallback:** if Cortado's last heartbeat is more than 30 minutes old, the requester may append its own `PackageVersion` entry or project reference, announce it on the PR, and continue. Cortado reconciles later.
- **Project-file ownership.** Task 1 creates every project. After that, each `.csproj`/`.sqlproj` belongs to the agent that owns the task building it, and that owner adds its own package references, project references, and build settings (for example the build-time OpenAPI settings in `Nachos.Api.csproj`):
  - **Cortado:** `Nachos.Abstractions`, `Nachos.ServiceDefaults`, `Nachos.DataLayer.SqlServer`, both `.sqlproj` projects, `Nachos.Cli`, `Nachos.AppHost`, `test/Nachos.Testing`, `Nachos.Abstractions.Tests`, `Nachos.Architecture.Tests`, `Nachos.DataLayer.SqlServer.Tests`, `Nachos.Cli.Tests`, `Nachos.AppHost.Tests`.
  - **Cedar:** `Nachos.Core`, `Nachos.Hosting`, `Nachos.Api`, `Nachos.Core.Tests`, `Nachos.Api.Tests`, `Nachos.LicenseCheck.Tests`, `eng/DocsGen`.
  - **Salsa:** `Nachos.DataLayer.InMemory`, `Nachos.Client`, `Nachos.DataLayer.InMemory.Tests`, `Nachos.Client.Tests`, `Nachos.Infra.Tests`.
  - Files that tasks add inside `test/Nachos.Testing` (for example `NachosApiFactory.cs` from Task 9) belong to that task's owner. The project file itself stays Cortado's.

**Validation:**
- **Cedar** is the full-set validation owner.
- **Cortado and Salsa** (both with Docker) each run the Docker-backed suites for their own tasks, and cross-run each other's at the final SHA.

**Merge gate:** every agent that owns files in the M1 PR posts LGTM at the same head SHA.

## Spec clarifications landed with this plan

These are recorded in the spec by Cortado in the same PR, and need agreement from Cedar and Cortado before merge. Salsa's findings must also be resolved.

1. **Wire pin:** the public Honcho OpenAPI is now version **3.3.0**. Conformance pins the public document at `https://docs.honcho.dev/v3/openapi.json`, raw-bytes SHA-256 `6aa5dd7fa958719e5f12565592617621141f0b9ae0a6b5450802dc152d2e3fe9`. This replaces R4's 3.2.2 pin.
2. **Clean-room artifact:** the raw OpenAPI is never committed. Only a Nachos-generated manifest of interface facts (routes, methods, statuses, parameter and field names/types/required/bounds) is committed, at `test/contracts/honcho-v3-wire.json`.
3. **Azure-target DeployReport:** an Azure dacpac DeployReport needs an Azure SQL target (an owner-gated action), and `AllowIncompatiblePlatform` is banned. So CI publishes a DeployReport for the **Sql2025** dacpac only. The Azure report is produced by `nachos schema report` during owner-approved runs.
4. **Auth status codes:** every auth failure is 401, matching Honcho's public docs.
5. **Case-sensitive IDs** (binary collation), matching Honcho's behavior on Postgres.

## Review Focus

These are the input classes most likely to bite users. Each line names the test that pins it.

1. **Case-sensitive IDs:** `alice` and `Alice` are two different peers. On SQL Server's default case-insensitive collation they would silently merge. Pinned by `StoreContractTests.GetOrCreate_IdsAreCaseSensitive` (Task 2), which both providers run.
2. **Concurrent get-or-create of one ID** (SDKs lazily create in parallel) must yield one row and identical 200 responses. Pinned by `StoreContractTests.GetOrCreate_IsIdempotentUnderConcurrency` (32 parallel calls; Task 2).
3. **Unicode, emoji, and max-length content/metadata** must round-trip exactly, with correct `token_count`. Pinned by `StoreContractTests.Append_PreservesUnicodeAndMaxLengthContent` (Task 2) and `MessageEndpointsTests.Create_ContentOver25000_Returns422` (Task 9).
4. **Messages with identical or backdated `created_at`** must list in insertion order (`Seq`), and in reverse when `reverse = true`. Pinned by `StoreContractTests.ListMessages_OrdersBySeqNotTimestamp` (Task 2).
5. **Pagination edges:** a page past the end returns empty `items` with correct `total`/`pages`, `size = 101` returns 422, and an empty collection gives `pages = 0`. Pinned by `PaginationTests.PageBeyondEnd_ReturnsEmptyWithTotals` and `PaginationTests.SizeAbove100_Returns422` (Task 9).

---

### Task 1: Solution skeleton, ServiceDefaults, wire manifest (Cortado)

**Files:**
- Create:
  - `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `Nachos.slnx`, `.editorconfig`;
  - empty projects for every path in spec §6.2 (`src/…`, `test/…`, with `IsPackable=false` on tests and hosts);
  - `src/Nachos.ServiceDefaults/Extensions.cs`;
  - `eng/contracts/Export-WireManifest.ps1`, `test/contracts/honcho-v3-wire.json`;
  - `test/Nachos.Architecture.Tests/DependencyRuleTests.cs`.
- Modify: `.gitignore` (add `bin/`, `obj/`, `*.dacpac` outside `src/`, `.test-runs/`, `node_modules/`).

**Interfaces:**
- Produces:
  - `IHostApplicationBuilder.AddServiceDefaults()`, which adds OpenTelemetry (traces, metrics, logs), standard resilience on `HttpClient`, and default health checks;
  - `WebApplication.MapDefaultEndpoints()`, which maps `/health` (alias), `/health/live` (checks tagged `live`), and `/health/ready` (all checks);
  - `test/contracts/honcho-v3-wire.json` with schema `{ "source": url, "sha256": hex, "version": "3.3.0", "routes": [{ "method", "path", "query": [names], "statuses": [ints] }], "schemas": { name: { "required": [..], "properties": { name: { "type", "nullable", "min", "max", "pattern", "enum" } } } } }`. No descriptions, titles, or examples are copied.

- [ ] **Step 1:** Write `DependencyRuleTests`:
  - `Core_DoesNotReference_SqlOrAspNet`: assert `typeof(Nachos.Core.AssemblyMarker).Assembly.GetReferencedAssemblies()` contains none of `Microsoft.Data.SqlClient`, `Microsoft.EntityFrameworkCore*`, `Microsoft.AspNetCore*`;
  - `Abstractions_ReferencesOnlyBcl`: allowed prefixes are `System`, `Microsoft.Extensions.*.Abstractions`, and `netstandard`;
  - `Client_DoesNotReference_Core`.
  - `Hosting_DoesNotReference_DataLayer`. Allowed direction: `DataLayer.* → Hosting → Core → Abstractions`. `Hosting` never references a provider, and `Core` never references `Hosting` or any provider.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Architecture.Tests`. Expected: build fails because the projects don't exist yet.
- [ ] **Step 3:** Create the solution:
  - `global.json`: `{"sdk":{"version":"10.0.100","rollForward":"latestFeature"}}`.
  - `Directory.Build.props`: `TargetFramework net10.0`, `Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors true`, `LangVersion latest`, `AnalysisLevel latest-recommended`.
  - `Directory.Packages.props`, with versions pinned exactly:
    - known pins: `Microsoft.EntityFrameworkCore.SqlServer 10.0.9`, `Microsoft.SqlServer.DacFx 170.4.83`, `Microsoft.Data.SqlClient 6.1.5`, `xunit 2.9.3`, `xunit.runner.visualstudio 3.1.5`, `Shouldly 4.3.0`, `NSubstitute 5.3.0`;
    - everything else pinned to the latest stable version on nuget.org on the day of this task.
  - The `Microsoft.Build.Sql` SDK version (`2.2.0` or later) goes in the `.sqlproj` files.
  - Add `AssemblyMarker` classes in each src project.
- [ ] **Step 4:** Write `Export-WireManifest.ps1 -Url <url> -ExpectedSha256 <hex> -Out <path>`:
  - Download the raw bytes, verify the hash (exit 1 on mismatch), project only the fields listed above, and write sorted, stable JSON.
  - Run it with the URL and hash from "Spec clarifications" item 1, and commit the manifest.
- [ ] **Step 5:** Run `dotnet build Nachos.slnx -warnaserror` then `dotnet test test/Nachos.Architecture.Tests`. Expected: PASS (4 tests).
- [ ] **Step 6:** Commit `chore: solution skeleton, service defaults, Honcho v3 wire manifest`.

### Task 2: Abstractions: contracts, records, store interfaces, store contract tests (Cortado)

**Files:**
- Create:
  - `src/Nachos.Abstractions/Contracts/*.cs`: wire DTOs `Workspace`, `Peer`, `Session`, `Message`, `MessageCreate`, `SessionPeerConfig`, `Page<T>`, the `*Configuration` records, `KeyResponse`, `ErrorResponse`;
  - `src/Nachos.Abstractions/Domain/*.cs`: records and `LifecycleState`;
  - `src/Nachos.Abstractions/Stores/*.cs`;
  - `src/Nachos.Abstractions/INachosClient.cs`, `PublicId.cs`, `Exceptions.cs`, `PageRequest.cs`;
  - `test/Nachos.Testing/StoreContractTests.cs` (an abstract base class library, not a test project; Task 9 adds `NachosApiFactory` to the same library);
  - `test/Nachos.Abstractions.Tests/ContractShapeTests.cs`.

**Interfaces (Produces; every later task relies on these exact names):**
- **Wire DTOs** (`System.Text.Json` with `[JsonPropertyName]` snake_case; field sets equal the manifest's `required` + `properties`):
  - `Workspace(string Id, JsonObject Metadata, JsonObject Configuration, DateTimeOffset CreatedAt)`
  - `Peer(string Id, string WorkspaceId, DateTimeOffset CreatedAt, JsonObject Metadata, JsonObject Configuration)`
  - `Session(string Id, bool IsActive, string WorkspaceId, JsonObject Metadata, JsonObject Configuration, DateTimeOffset CreatedAt)`
  - `Message(string Id, string Content, string PeerId, string SessionId, JsonObject Metadata, DateTimeOffset CreatedAt, string WorkspaceId, int TokenCount)`
  - `MessageCreate(string Content, string PeerId, JsonObject? Metadata, MessageConfiguration? Configuration, DateTimeOffset? CreatedAt)`
  - `SessionPeerConfig(bool? ObserveMe, bool? ObserveOthers)`
  - `Page<T>(IReadOnlyList<T> Items, long Total, [property: JsonPropertyName("page")] int PageNumber, int Size, int Pages)`. The CLR member can't be named `Page`, because a record member may not share its enclosing type's name (CS0542). The wire field is still `page`. `ContractShapeTests.Page_SerializesPageField` asserts it.
  - `KeyResponse(string Key)`
  - `WorkspaceConfiguration`, `SessionConfiguration` (same shape), `MessageConfiguration`, `ReasoningConfiguration`, `PeerCardConfiguration`, `SummaryConfiguration`, `DreamConfiguration`, `DialecticConfiguration`, with the fields in the manifest.
- **Domain:**
  - `enum LifecycleState { Active, Inactive, Deleting }`
  - `enum PeerKind { Regular, Scope, All }`
  - `WorkspaceRecord(string Name, JsonObject Metadata, JsonObject Configuration, LifecycleState State, DateTimeOffset CreatedAt)`
  - `PeerRecord(string WorkspaceName, string Name, JsonObject Metadata, JsonObject Configuration, bool IsInternal, DateTimeOffset CreatedAt)`
  - `SessionRecord(string WorkspaceName, string Name, LifecycleState State, JsonObject Metadata, JsonObject Configuration, DateTimeOffset CreatedAt)`
  - `MessageRecord(string PublicId, string WorkspaceName, string SessionName, string PeerName, long Seq, string Content, int TokenCount, JsonObject Metadata, DateTimeOffset CreatedAt)`
  - `NewMessage(string PeerName, string Content, int TokenCount, JsonObject? Metadata, DateTimeOffset? CreatedAt)`
  - `PageRequest(int Page = 1, int Size = 50, bool Reverse = false)`
  - `GrantRecord(string ObjectId, string? WorkspaceName, string Role)`
  - `IdempotencyRecord(string Key, string RequestHash, int ResponseStatus, string ResponseBody, DateTimeOffset ExpiresAt)`
  - `IdempotencyWrite(string Key, string RequestHash, int ResponseStatus, Func<IReadOnlyList<MessageRecord>, string> SerializeResponse, TimeSpan Ttl)`
- **Stores:**
  - `IMemoryStore` exposes `IWorkspaceStore Workspaces`, `IPeerStore Peers`, `ISessionStore Sessions`, `IMessageStore Messages`, `IGrantStore Grants`, and `IIdempotencyStore Idempotency`.
  - `IWorkspaceStore`:
    - `GetOrCreateAsync(string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct) → Task<WorkspaceRecord>`
    - `GetAsync(name, ct) → Task<WorkspaceRecord?>`
    - `UpdateAsync(name, JsonObject? metadata, JsonObject? configuration, ct) → Task<WorkspaceRecord>`; a null argument leaves that field unchanged; throws `NotFoundException`
    - `ListAsync(FilterNode? filter, PageRequest page, ct) → Task<Page<WorkspaceRecord>>`
  - `IPeerStore`: `GetOrCreateAsync(ws, name, metadata, configuration, ct)`, `GetAsync`, `UpdateAsync`, `ListAsync(ws, FilterNode?, PeerKind, PageRequest, ct)`, and `ListSessionsForPeerAsync(ws, peer, FilterNode?, PageRequest, ct) → Page<SessionRecord>`.
  - `ISessionStore`:
    - `GetOrCreateAsync(ws, name, metadata, configuration, IReadOnlyDictionary<string, SessionPeerConfig>? peers, ct)`, `GetAsync`
    - `UpdateAsync(ws, name, metadata, configuration, ct)`, `ListAsync(ws, FilterNode?, PageRequest, ct)`
    - `AddPeersAsync(ws, session, IReadOnlyDictionary<string, SessionPeerConfig>, ct)`
    - `SetPeersAsync(ws, session, IReadOnlyDictionary<string, SessionPeerConfig>, ct)`: members not listed get `LeftAt = now`
    - `RemovePeersAsync(ws, session, IReadOnlyList<string> peerNames, ct)`: sets `LeftAt = now`
    - `ListPeersAsync(ws, session, PageRequest, ct) → Page<PeerRecord>`: active members only
    - `GetPeerConfigAsync(ws, session, peer, ct) → Task<SessionPeerConfig>`, `SetPeerConfigAsync(ws, session, peer, SessionPeerConfig, ct)`
    - `IsActiveMemberAsync(ws, session, peer, ct) → Task<bool>`
  - `IMessageStore`:
    - `AppendAsync(ws, session, IReadOnlyList<NewMessage>, IdempotencyWrite?, ct) → Task<IReadOnlyList<MessageRecord>>`, all in one transaction: upsert sender peers and memberships, allocate `Seq`, insert messages, insert the idempotency record if present
    - `GetAsync(ws, session, publicId, ct)`
    - `UpdateMetadataAsync(ws, session, publicId, JsonObject metadata, ct)`
    - `ListAsync(ws, session, FilterNode?, PageRequest, ct)`
  - `IIdempotencyStore`: `TryGetAsync(ws, key, ct) → Task<IdempotencyRecord?>` (ignores expired records). **Expired keys are reclaimed atomically inside `AppendAsync`:** in the same transaction, delete the expired row for `(workspace, key)` under an update/range lock, then insert. A key whose record has expired is therefore immediately reusable for a fresh operation, without relying on a cleanup worker.
  - `IGrantStore`: `AddAsync(GrantRecord, ct)`, `RemoveAsync(GrantRecord, ct)`, `ListAsync(string? objectId, ct)`, `GetWorkspacesAsync(string objectId, ct) → Task<IReadOnlySet<string>>`.
- **Exceptions:** `NachosException` (base) → `NotFoundException`, `ConflictException`, `NachosValidationException(string Detail)`, `RequestValidationException(IReadOnlyList<ValidationError>)`, `AuthException`, `IdempotencyKeyReusedException` (→ 422), and `IdempotencyDuplicateException(string Key)`. Stores throw the last one when an `IdempotencyWrite` key already exists. It is never surfaced over HTTP.
- **`PublicId.New() → string`:** 21 characters, alphabet `A-Za-z0-9_-`, from `RandomNumberGenerator`.
- **`INachosClient`:** one async method per M1 route, with names matching the routes. For example: `GetOrCreateWorkspaceAsync(string id, JsonObject? metadata = null, WorkspaceConfiguration? configuration = null, CancellationToken ct = default) → Task<Workspace>`, `ListWorkspacesAsync(JsonObject? filters, PageRequest page, ct) → Task<Page<Workspace>>`, and `CreateMessagesAsync(string workspaceId, string sessionId, IReadOnlyList<MessageCreate> messages, string? idempotencyKey = null, ct) → Task<IReadOnlyList<Message>>`. Also provide the extension `NachosPaging.EnumerateAsync<T>(Func<PageRequest, Task<Page<T>>>) → IAsyncEnumerable<T>`.
- **`abstract class StoreContractTests`** with `protected abstract IMemoryStore CreateStore(TimeProvider clock)`. Its tests are named in the steps below and use `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`).
- **Clock rule (both providers):** stores take `TimeProvider` from DI. Every time-based value is computed from the **app clock** and passed to SQL as a parameter (`@now`), never from the database clock (`SYSDATETIMEOFFSET()`/`SYSUTCDATETIME()`) in a query. That covers `CreatedAt` defaults, `JoinedAt`/`LeftAt`, `IdempotencyRecord.ExpiresAt = clock.GetUtcNow() + Ttl`, and expiry comparisons. The same `FakeTimeProvider`-driven contract test therefore behaves identically on SQL and in memory. The post-deploy `SchemaVersion.AppliedAt` is the only exception.

- [ ] **Step 1:** Write `ContractShapeTests`. For each wire DTO, serialize a sample and assert that the JSON property set equals the manifest schema's property set, and that required fields are non-null. For example, `Message_SerializesExactlyManifestFields`.
- [ ] **Step 2:** Write `StoreContractTests` (in `Nachos.Testing`):
  - `GetOrCreate_ReturnsSameRecordOnSecondCall`
  - `GetOrCreate_IdsAreCaseSensitive`: `alice` and `Alice` are two peers
  - `Message_GetByCaseFoldedPublicId_NotFound`
  - `GetOrCreate_IsIdempotentUnderConcurrency`: 32 parallel calls → 1 row, all results equal
  - `Update_NullLeavesFieldUnchanged`
  - `Update_Missing_ThrowsNotFound`
  - `List_FiltersAndPages` (filter `{"metadata":{"k":"v"}}`, size 2, 5 rows → `Total = 3`, `Pages = 2`)
  - `Append_AllocatesContiguousSeqAndJoinsSender`
  - `Append_PreservesUnicodeAndMaxLengthContent`: 25,000 characters including `😀`, `𝔘`, and RTL text, compared byte-exact
  - `ListMessages_OrdersBySeqNotTimestamp`: three messages, the second backdated 1 day; ascending order is m1, m2, m3; reverse is m3, m2, m1
  - `SetPeers_MarksUnlistedLeft`
  - `RemovePeers_ExcludesFromListPeers`
  - `Append_WithIdempotency_StoresRecordAtomically`: a fault injected after the insert leaves neither the messages nor the record
  - `Append_SameIdempotencyKeyTwice_SecondThrowsDuplicate`
  - `Append_ExpiredIdempotencyKey_IsReclaimedAndSucceeds`: the record expires, then a fresh append with the same key succeeds, and exactly one record exists, holding the new hash
  - `Append_ConcurrentReuseOfExpiredKey_OneWinner`: 8 parallel appends reuse one expired key; exactly one inserts, and the others throw `IdempotencyDuplicateException`
  - `Append_MaxLengthIdempotencyKey_Accepted`: a 255-character key
  - `Grants_AddListRemove`
- [ ] **Step 3:** Run `dotnet test test/Nachos.Abstractions.Tests`. Expected: FAIL (types missing).
- [ ] **Step 4:** Implement the types listed under Interfaces.
- [ ] **Step 5:** Run `dotnet test test/Nachos.Abstractions.Tests`. Expected: PASS. `StoreContractTests` compiles; providers run it in Tasks 7 and 8.
- [ ] **Step 6:** Commit `feat(abstractions): wire contracts, domain records, store interfaces, contract tests`.

### Task 3: Filter AST, parser, conformance cases (Cortado)

**Files:**
- Create: `src/Nachos.Abstractions/Filtering/FilterNode.cs`, `FilterParser.cs`, `ResourceFields.cs`; `test/Nachos.Testing/Filtering/filter-cases.json`, `FilterConformanceTests.cs` (abstract); `test/Nachos.Abstractions.Tests/FilterParserTests.cs`.

**Interfaces:**
- Produces:
  - `abstract record FilterNode` with the subtypes `And(IReadOnlyList<FilterNode>)`, `Or(...)`, `Not(IReadOnlyList<FilterNode>)` (meaning NOT any), `MatchAll`, `MatchNone`, `Field(string Column, FilterOp Op, JsonNode? Value)`, and `MetadataPath(IReadOnlyList<string> Path, FilterOp Op, JsonNode? Value)`.
  - `enum FilterOp { Eq, Ne, Gt, Gte, Lt, Lte, In, Contains, IContains, IsNull, NotNull, JsonContains }`.
  - `enum ResourceKind { Workspace, Peer, Session, Message }`.
  - `FilterParser.Parse(JsonNode? filters, ResourceKind kind) → FilterNode?`; throws `NachosValidationException` on value errors.
  - `ResourceFields.For(kind)`, which maps wire names to canonical columns and types:
    - Workspace: `id`/`name` → `Name` (text), `metadata`, `created_at`.
    - Peer: `id`/`peer_id` → `Name`, `metadata`, `created_at`.
    - Session: `id`/`session_id` → `Name`, `is_active` (bool), `peer_id` (an active member exists), `metadata`, `created_at`.
    - Message: `id` → `PublicId`, `session_id`, `peer_id`, `content` (text), `token_count` (int), `created_at`, `metadata`.
  - `abstract class FilterConformanceTests`, which loads `filter-cases.json` (a dataset plus `[{ "name", "resource", "filter", "expect": [ids] | "error" }]`) and requires `protected abstract Task<IReadOnlyList<string>> QueryAsync(ResourceKind, FilterNode?)` over the seeded dataset.

- [ ] **Step 1:** Write `filter-cases.json` with at least 40 cases. They cover:
  - equality, bare list = `in`, `in` containing `"*"`, empty `in` → none;
  - `ne` and `NOT` both including unset fields;
  - `{"field": null}` and `{"ne": null}`;
  - AND / OR / NOT nesting;
  - `gt`/`gte`/`lt`/`lte` on `created_at` (date-only `"2026-01-01"` = UTC midnight) and on `token_count` (with numeric string `"5"`);
  - `contains` (case-sensitive) vs `icontains`;
  - metadata: nested object equality, a bare list = containment, per-key `in`, `ne`, `gt`, wildcard values, and `{"contains":{...}}`;
  - error cases: `{"is_active":"true"}` → error, `{"metadata":{"ne":{}}}` → error, and an unknown top-level key → ignored (match all).
- [ ] **Step 2:** Write `FilterParserTests`: `UnknownTopLevelKey_IsIgnored`, `StringBoolean_Rejected`, `MetadataWholeObjectOperator_Rejected`, `BareListOutsideMetadata_IsIn`, `BareListInsideMetadata_IsJsonContains`, `Wildcard_IsMatchAll`, `EmptyIn_IsMatchNone`, `DateOnly_IsUtcMidnight`, `NumericString_ForTokenCount_Accepted`.
- [ ] **Step 3:** Run `dotnet test test/Nachos.Abstractions.Tests --filter FilterParser`. Expected: FAIL.
- [ ] **Step 4:** Implement `FilterParser`. It is a recursive descent over `JsonNode`. Operator keys are exactly `gt gte lt lte ne in contains icontains`. Logical keys are `AND OR NOT`, each taking an array.
- [ ] **Step 5:** Run it again. Expected: PASS.
- [ ] **Step 6:** Commit `feat(abstractions): filter AST, parser, shared conformance cases`.

### Task 4: Core: services, validation, tokens, config resolver, hosting (Cedar)

**Files:**
- Create: `src/Nachos.Core/NachosService.cs` (implements `INachosClient`), `Validation/IdValidator.cs`, `Validation/RequestValidator.cs`, `Tokens/ITokenCounter.cs`, `Tokens/TiktokenTokenCounter.cs`, `Configuration/IConfigurationResolver.cs`, `ConfigurationResolver.cs`, `ResolvedConfiguration.cs`, `NachosOptions.cs`; `src/Nachos.Hosting/NachosServiceCollectionExtensions.cs`, `NachosBuilder.cs`; `test/Nachos.Core.Tests/*`.

**Interfaces:**
- Consumes: everything from Tasks 2 and 3.
- Produces:
  - `services.AddNachos(Action<NachosBuilder> configure) → IServiceCollection`, which registers `INachosClient` → `NachosService` (scoped) along with the token counter and the resolver. `NachosBuilder.Services` is where providers register `IMemoryStore`.
  - `ITokenCounter.Count(string text) → int`, using o200k_base (`Microsoft.ML.Tokenizers` + `Microsoft.ML.Tokenizers.Data.O200kBase`).
  - `IdValidator.Validate(string id, string paramName)`.
  - `IConfigurationResolver.Resolve(JsonObject? workspace, JsonObject? session, JsonObject? message) → ResolvedConfiguration`, with precedence message > session > workspace > `NachosOptions` defaults: `Summary.MessagesPerShort = 20`, `Summary.MessagesPerLong = 60`, `Summary.MaxTokensShort = 1000`, `Summary.MaxTokensLong = 4000`, `Deriver.MaxCustomInstructionsTokens = 2000`. Each resolved value records its `Source` (`"message" | "session" | "workspace" | "global"`). Message-level configuration contributes `reasoning` only.

- [ ] **Step 1:** Write the tests:
  - `IdValidatorTests.Rejects` (`""`, 513 characters, `"a b"`, `"é"`) and `.Accepts` (`"a-Z_9"`);
  - `TokenCounterTests.KnownStrings` (`"hello world"` → 2; `""` → 0);
  - `ConfigurationResolverTests.MessageOverridesSessionOverridesWorkspace`, `.MessageConfigOnlyAffectsReasoning`, `.SummaryMinimumsEnforced` (short 9 → `NachosValidationException`);
  - `NachosServiceTests`, which use an NSubstitute `IMemoryStore` so that Task 4 never depends on Task 8: `CreateMessages_101_Throws422`, `CreateMessages_ComputesTokenCount`, `GetOrCreateSession_WithPeers_EnsuresMembership`, `ListPeers_DefaultKindExcludesInternal`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Core.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement. `NachosService` maps records to wire DTOs: `Session.IsActive = State == Active`, and `Message.Id = PublicId`. Filter JSON goes through `FilterParser.Parse`.
- [ ] **Step 4:** Run `dotnet test test/Nachos.Core.Tests`. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): NachosService, validation, token counting, config resolver, AddNachos`.

### Task 5: Database projects, Azure + Sql2025 (Cortado)

**Files:**
- Create:
  - `src/DataLayer/Nachos.DataLayer.SqlServer.Database/Nachos.DataLayer.SqlServer.Database.sqlproj` (`DSP = SqlAzureV12DatabaseSchemaProvider`, `ModelCollation 1033, CI`, `<ReadCommittedSnapshot>True</ReadCommittedSnapshot>`);
  - `Tables/{Workspaces,Peers,Sessions,SessionPeers,Messages,PrincipalGrants,IdempotencyRecords,SchemaVersion}.sql`;
  - `Scripts/Script.PostDeployment.sql`;
  - `src/DataLayer/Nachos.DataLayer.SqlServer.Database.Sql2025/Nachos.DataLayer.SqlServer.Database.Sql2025.sqlproj` (`DSP = Sql170DatabaseSchemaProvider`, `<Build Include="../Nachos.DataLayer.SqlServer.Database/Tables/**/*.sql" />`, and a `PostDeploy` item linking the same script).

**Interfaces:**
- Produces the tables below, matching spec §7.2 for these columns. Every `Name` column is `nvarchar(512) COLLATE Latin1_General_100_BIN2_UTF8`, and `Messages.PublicId` is `nvarchar(32) COLLATE Latin1_General_100_BIN2_UTF8`. Every `UNIQUE` constraint is explicitly `NONCLUSTERED`. Clustered keys are `bigint` surrogates only. Every index key must fit SQL limits (900 bytes clustered, 1,700 nonclustered), enforced by `SchemaDeployerTests.AllIndexKeys_WithinSqlLimits` (Task 6). For example, `(WorkspaceId, Name)` = 8 + 1,024 bytes. JSON columns use `json NOT NULL DEFAULT '{}'`. Timestamps are `datetimeoffset(7)`.
  - `Workspaces(Id bigint IDENTITY PK, Name unique, LifecycleState tinyint, LifecycleVersion int, DeletionJobId bigint NULL, Metadata, InternalMetadata, Configuration, CreatedAt)`
  - `Peers(Id, WorkspaceId FK, Name, IsInternal bit, Metadata, InternalMetadata, Configuration, CreatedAt, UNIQUE(WorkspaceId, Name))`
  - `Sessions(Id, WorkspaceId FK, Name, LifecycleState, LifecycleVersion, DeletionJobId NULL, NextMessageSeq bigint DEFAULT 1, Metadata, InternalMetadata, Configuration, CreatedAt, UNIQUE(WorkspaceId, Name))`
  - `SessionPeers(WorkspaceId, SessionId, PeerId, Configuration json, JoinedAt, LeftAt NULL, PK(WorkspaceId, SessionId, PeerId))`, with composite FKs that include `WorkspaceId`
  - `Messages(Id, WorkspaceId, SessionId, PeerId, PublicId nvarchar(32) COLLATE Latin1_General_100_BIN2_UTF8 unique, Seq bigint, Content nvarchar(max), TokenCount int, Metadata, InternalMetadata, CreatedAt, UNIQUE(SessionId, Seq))`
  - `PrincipalGrants(ObjectId nvarchar(64), WorkspaceId bigint NULL, Role nvarchar(32), UNIQUE)`
  - `IdempotencyRecords(Id bigint IDENTITY PRIMARY KEY CLUSTERED, WorkspaceId bigint, KeyHash binary(32), [Key] nvarchar(255), RequestHash char(64), ResponseStatus int, ResponseBody nvarchar(max), ExpiresAt, CONSTRAINT UQ_Idem UNIQUE NONCLUSTERED (WorkspaceId, KeyHash))`. `KeyHash` is the SHA-256 of the key. HTTP keys must match `^[\x21-\x7E]{1,255}$`; anything else returns 422.
  - `SchemaVersion(Id tinyint PK CHECK (Id = 1), Version int, AppliedAt)`
- The post-deploy script MERGEs `SchemaVersion(1, 1, SYSUTCDATETIME())`. The constant `SchemaInfo.CurrentVersion = 1` lives in Task 6.

- [ ] **Step 1:** Write the DDL files and both `.sqlproj` files.
- [ ] **Step 2:** Run `dotnet build src/DataLayer/Nachos.DataLayer.SqlServer.Database` and then the `.Sql2025` project. Expected: two `.dacpac` files, no errors.
  - **Gate:** if `Sql170DatabaseSchemaProvider` is rejected by the pinned `Microsoft.Build.Sql`, upgrade the SDK version. If no version supports it, use the highest box provider that accepts `json` and `vector`, and post a finding on the PR (spec §7.3).
- [ ] **Step 3:** Commit `feat(schema): M1 tables in dual-target sqlproj (Azure V12 + SQL Server 2025)`.

### Task 6: SchemaDeployer (Cortado)

**Files:**
- Create: `src/DataLayer/Nachos.DataLayer.SqlServer/Schema/{SchemaInfo,SchemaDeployer,DacpacCatalog,DeployReportClassifier,SqlServerOptions,SchemaGate}.cs`; `test/Nachos.DataLayer.SqlServer.Tests/{SqlServerFixture,SchemaDeployerTests,DeployReportClassifierTests}.cs`.
- Modify: `src/DataLayer/Nachos.DataLayer.SqlServer/Nachos.DataLayer.SqlServer.csproj` to embed both dacpacs as `EmbeddedResource` (`Nachos.Azure.dacpac`, `Nachos.Sql2025.dacpac`), using `ProjectReference` with `ReferenceOutputAssembly=false`.

**Interfaces:**
- Produces:
  - `SqlServerOptions { string ConnectionString; bool AutomaticSchemaDeploymentEnabled = false; }`, bound from `Nachos:SqlServer`.
  - `ISchemaManager` (defined in Abstractions by this task as an additive change):
    - `GetStatusAsync(ct) → SchemaStatus(string Platform, int? Deployed, int Current, SchemaState State)`, where `SchemaState ∈ {Empty, Unstamped, Current, Behind, Ahead}`. `Empty` means no user objects at all (`sys.objects` with `is_ms_shipped = 0` is empty). `Unstamped` means user objects exist but there is no `SchemaVersion` row.
    - `ReportAsync(ct) → SchemaReport(DeployClassification Classification, string ReportXml)`
    - `DeployAsync(bool allowDataLoss, ct) → SchemaReport`
  - `enum DeployClassification { AutoSafe, Unsafe, Unclassifiable }`.
  - `SchemaGate.EnsureAsync(ct)`: runs once per process before first store use. Behavior by state:
    - `Current`: no-op.
    - `Empty`: bootstraps from the dacpac only if `AutomaticSchemaDeploymentEnabled` is set.
    - `Behind`: deploys only if that setting is on **and** the classification is `AutoSafe`.
    - `Unstamped` and `Ahead`: **never** changed automatically. An `Ahead` database is never downgraded.
    - Every refusal throws `InvalidOperationException` whose remedy text names `nachos schema upgrade`.
  - `DeployReportClassifier` returns `AutoSafe` only when the report has **no alerts and every operation is on the explicit allowlist**: `Create` of a table, nullable column, column with a default, index, constraint, procedure, function, or view; `Alter`/`Create` of a procedure, function, or view. Anything else is `Unsafe`, including drops, column type or nullability changes, and table rebuilds. A well-formed report containing an unrecognized operation is `Unclassifiable`. Both refuse.
  - Platform selection: `SERVERPROPERTY('EngineEdition') = 5` → Azure dacpac; box editions with `ProductMajorVersion >= 17` → Sql2025 dacpac; anything else → `NotSupportedException`. Deploy options: `BlockOnPossibleDataLoss = !allowDataLoss`, `ScriptDatabaseOptions = true`.
  - `SqlServerFixture` (Testcontainers `mcr.microsoft.com/mssql/server:2025-latest`), with `CreateDatabaseAsync() → string connectionString` giving each test class a unique database.

- [ ] **Step 1:** Write the tests:
  - `DeployReportClassifierTests` (pure XML fixtures): `AllowlistedCreatesNoAlerts_IsAutoSafe`, `DataIssueAlert_IsUnsafe`, `DropOrRebuildWithoutAlerts_IsUnsafe`, `UnknownOperationWithoutAlerts_IsUnclassifiable`, `MalformedXml_IsUnclassifiable`.
  - `SchemaDeployerTests` (Docker):
    - `EmptyDatabase_DeploysAndStampsVersion`
    - `Deploy_SetsReadCommittedSnapshotOn` (`sys.databases.is_read_committed_snapshot_on = 1`)
    - `CurrentDatabase_IsNoOp`
    - `GateDisabled_Uninitialized_ThrowsWithRemedy` (the message contains `nachos schema upgrade`)
    - `UnsafeDiff_Refused`: deploy, add a column outside the model, redeploy → `Unsafe`, nothing applied
    - `NonEmptyUnstampedDatabase_NeverAutoDeployed`: create a user table only, enable automatic deployment → refused, nothing applied
    - `AheadDatabase_NeverDowngraded`: stamp version `Current + 1` → refused
    - `AllIndexKeys_WithinSqlLimits`: after deploy, sum the key column `max_length` per index from `sys.index_columns`: ≤ 900 clustered, ≤ 1,700 nonclustered
    - `PostDeployVersion_MatchesSchemaInfo`: the post-deploy script text contains `SchemaInfo.CurrentVersion`
- [ ] **Step 2:** Run `dotnet test test/Nachos.DataLayer.SqlServer.Tests --filter Schema`. Expected: FAIL.
- [ ] **Step 3:** Implement with `Microsoft.SqlServer.Dac` (`DacServices`, `DacPackage.Load(stream)`, `GenerateDeployReport`, `Deploy(..., upgradeExisting: true, options)`).
- [ ] **Step 4:** Run it again. Expected: PASS. Start Docker Desktop first, and post the results with the SHA.
- [ ] **Step 5:** Commit `feat(sql): SchemaDeployer with auto-safe gate and platform selection`.

### Task 7: SQL Server provider (Cortado)

**Files:**
- Create: `src/DataLayer/Nachos.DataLayer.SqlServer/{NachosDbContext,Entities/*,SqlMemoryStore,Stores/*,Filtering/SqlFilterCompiler,SqlServerBuilderExtensions}.cs`; `test/Nachos.DataLayer.SqlServer.Tests/{SqlStoreContractTests,SqlFilterConformanceTests,SqlFilterCompilerTests}.cs`.

**Interfaces:**
- Consumes: Tasks 2, 3, and 6.
- Produces:
  - `NachosBuilder.UseSqlServer(Action<SqlServerOptions> configure)`, which registers `IMemoryStore` → `SqlMemoryStore` (scoped) and `ISchemaManager`, and calls `SchemaGate.EnsureAsync` on first use.
  - `SqlFilterCompiler.Compile(FilterNode, ResourceKind, string tableAlias) → (string Sql, IReadOnlyList<SqlParameter> Parameters)`. Implementation notes:
    - `JsonContains` compiles to `JSON_VALUE` for scalars and `EXISTS (SELECT 1 FROM OPENJSON(col, path) …)` for arrays and objects.
    - `Contains` uses `COLLATE Latin1_General_100_BIN2_UTF8 LIKE`; `IContains` uses a `CI_AS` collation `LIKE`.
    - `%`, `_`, and `[` in user values are escaped.
- Concurrency rules:
  - Get-or-create inserts and catches errors 2627/2601, then re-reads.
  - Seq allocation: `UPDATE Sessions SET NextMessageSeq += @n OUTPUT deleted.NextMessageSeq WHERE …` inside the append transaction.
  - EF entities are internal. EF is used only for mapping and LINQ paging, never migrations: there is a test that asserts no `Migrations` folder exists and that `Database.Migrate` is never referenced.

- [ ] **Step 1:** Write `SqlStoreContractTests : StoreContractTests` and `SqlFilterConformanceTests : FilterConformanceTests`, both against the fixture. Also write `SqlFilterCompilerTests.EscapesLikeWildcards`, `.ProducesOnlyParameters` (no user values appear in the SQL text), and `NoEfMigrations_Exist`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.DataLayer.SqlServer.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run it again. Expected: PASS, including the concurrency test. Loop `--filter "GetOrCreate_IsIdempotentUnderConcurrency|Append"` 20 times and record the result.
- [ ] **Step 5:** Commit `feat(sql): SQL Server memory store and filter compiler`.

### Task 8: In-memory provider (Salsa)

**Files:**
- Create: `src/DataLayer/Nachos.DataLayer.InMemory/{InMemoryMemoryStore,Stores/*,InMemoryFilterEvaluator,InMemoryBuilderExtensions}.cs`; `test/Nachos.DataLayer.InMemory.Tests/{InMemoryStoreContractTests,InMemoryFilterConformanceTests}.cs`.

**Interfaces:**
- Produces:
  - `NachosBuilder.UseInMemory()`. Outside the `Development` environment it logs a warning containing `"in-memory provider is not durable"` (R9).
  - `InMemoryFilterEvaluator.Matches(FilterNode, object record) → bool`.
  - All comparisons are ordinal. Each workspace has its own `SemaphoreSlim`, so get-or-create and append are atomic.

- [ ] **Step 1:** Write the two derived test classes, plus `UseInMemory_OutsideDevelopment_LogsWarning`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.DataLayer.InMemory.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run it again. Expected: PASS.
- [ ] **Step 5:** Commit `feat(inmemory): in-memory store and filter evaluator`.

### Task 9: API: routes, JSON, errors, pagination, health, 501s (Cedar)

**Files:**
- Create: `src/Nachos.Api/Program.cs` (ends with `public partial class Program;`, so `WebApplicationFactory<Program>` works from other assemblies), `test/Nachos.Testing/NachosApiFactory.cs`, `Endpoints/{Workspace,Peer,Session,Message,NotImplemented}Endpoints.cs`, `Errors/NachosExceptionHandler.cs`, `Json/NachosJsonContext.cs`, `Paging/PagingParameters.cs`, `Health/StoreReadinessCheck.cs`; `test/Nachos.Api.Tests/{WorkspaceEndpointsTests,PeerEndpointsTests,SessionEndpointsTests,MessageEndpointsTests,PaginationTests,ErrorShapeTests,WireCoverageTests}.cs`.

**Interfaces:**
- Consumes: `INachosClient` (Task 4) and `UseInMemory` (Task 8).
- Produces:
  - Every M1 route in spec §9.3 (M1 rows), with paths, query parameters, and statuses exactly as in `honcho-v3-wire.json`.
  - JSON uses `JsonSerializerOptions` with `PropertyNamingPolicy = SnakeCaseLower`, null values written, and the source-generated `NachosJsonContext`.
  - `NotImplementedEndpoints` maps every manifest route that M1 doesn't implement to `501 {"detail":"Not implemented in this Nachos version"}`.
  - `NachosApiFactory : WebApplicationFactory<Program>` lives in `test/Nachos.Testing` (shared with `Nachos.Client.Tests` and conformance), and uses the in-memory provider with auth disabled, running in the `Development` environment. Task 10 adds an auth-enabled variant, `NachosApiFactory.WithAuth(SigningKeyOptions)`.

- [ ] **Step 1:** Write the tests:
  - `WireCoverageTests.EveryManifestRoute_IsMapped`: a route is mapped when it is implemented, or when it returns 501 and is listed in `NotImplementedEndpoints`.
  - `WorkspaceEndpointsTests.GetOrCreate_Twice_Returns200SameBody`; `.Update_NullConfiguration_Unchanged`; `.List_WithMetadataFilter`.
  - `PeerEndpointsTests.List_KindScope_ExcludesRegular`; `.Sessions_ForPeer`.
  - `SessionEndpointsTests.Create_WithPeers_ListsMembers`; `.SetPeers_ReplacesMembership`; `.DeletePeers_BodyArray`; `.PeerConfig_GetPut_204`.
  - `MessageEndpointsTests.Create_Batch_Returns201InOrder`; `.Create_101_Returns422`; `.Create_ContentOver25000_Returns422`; `.Create_TrailingSlashAlias`; `.Get_Unknown_Returns404Detail`; `.Update_MetadataOnly`.
  - `PaginationTests.PageBeyondEnd_ReturnsEmptyWithTotals`; `.SizeAbove100_Returns422`; `.EmptyCollection_PagesZero`; `.Reverse_FlipsOrder`.
  - `ErrorShapeTests.Validation_Is422DetailArray`; `.Domain_Is422DetailString`; `.Every4xx_HasRfc9457Fields`.
  - `HealthTests.Live_Ready_Alias_Return200`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Api.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement. The exception handler maps:
  - `NotFoundException` → 404;
  - `ConflictException` → 409;
  - `NachosValidationException` and `IdempotencyKeyReusedException` → 422 with a string `detail`;
  - `RequestValidationException` → 422 with an array `detail`;
  - `AuthException` → 401.
- [ ] **Step 4:** Run it again. Expected: PASS.
- [ ] **Step 5:** Commit `feat(api): v3 CRUD routes, errors, pagination, health, 501 coverage`.

### Task 10: Auth: NachosKey + Entra, keys and grants routes (Cedar)

**Files:**
- Create: `src/Nachos.Core/Keys/{IKeyIssuer,HmacKeyIssuer,NachosKeyClaims,SigningKeyOptions}.cs`; `src/Nachos.Api/Auth/{NachosPrincipal,NachosKeyAuthenticationHandler,EntraPrincipalMapper,NachosAuthorizationHandler,RouteRequirements,MemberReadRoutes}.cs`; `Endpoints/{Key,Grant}Endpoints.cs`; `test/Nachos.Api.Tests/Auth/{ScopeMatrixTests,KeyIssuerTests,MemberReadPolicyTests,EntraMappingTests,KeyEndpointTests}.cs`.

**Interfaces:**
- Produces:
  - `IKeyIssuer.Issue(NachosKeyClaims claims) → string`, where `NachosKeyClaims(bool Admin, string? Workspace, string? Peer, string? Session, DateTimeOffset? ExpiresAt)`. The token is an HS256 JWT with claims `t` (ISO-8601 UTC string), `exp?`, `ad?`, `w?`, `p?`, `s?`, and a `kid` header.
  - `IKeyIssuer.Validate(string token) → NachosKeyClaims`, which throws `AuthException`.
  - `SigningKeyOptions { IReadOnlyList<SigningKey> Keys }`, where `SigningKey(string Kid, string Secret)`. `Keys[0]` signs; all keys validate. A missing `kid` validates against `Keys[0]`.
  - `NachosPrincipal(bool IsAdmin, IReadOnlySet<string> Workspaces, string? Peer, string? Session)`.
  - Auth options bound from `Nachos:Auth`: `{ bool Enabled = true; SigningKeyOptions NachosKey; MicrosoftIdentityOptions? Entra }`. `Enabled = false` outside Development throws at startup.
  - Entra mapping: app role `Nachos.Admin` → admin. App role `Nachos.Workspace` → the workspaces from `IGrantStore.GetWorkspacesAsync(oid)`. Entra never maps to a peer or session.
  - Route requirements, one per route:

| Route(s) | Allowed |
|---|---|
| `POST /v3/workspaces` | admin; workspace key with `w == body.id` |
| `POST /v3/workspaces/list`, `POST /v3/keys`, `POST /v3/admin/grants` | admin |
| `PUT W`, `POST W/peers`, `W/peers/list`, `W/sessions`, `W/sessions/list` | admin; workspace |
| `PUT P`, `POST P/sessions` | admin; workspace; peer key `p == peer_id` |
| `PUT S`, `POST/PUT/DELETE S/peers`, `PUT S/peers/{p}/config`, `POST M`, `PUT M/{id}` | admin; workspace; session key `s == session_id` |
| `GET S/peers`, `POST M/list`, `GET M/{id}` | admin; workspace; session; **member-read** |
| `GET S/peers/{p}/config` | admin; workspace; session; member-read **with `p == peer_id`** |

  - `MemberReadRoutes.All` lists exactly the member-read routes above.
  - `POST /v3/keys` rejects `peer_id` without `workspace_id`, and rejects `peer_id` together with `session_id`, with 422. It returns `KeyResponse`.
  - `POST /v3/admin/grants` takes a body `{ "object_id", "workspace_id"?, "role": "Nachos.Admin" | "Nachos.Workspace" }` and returns 204.

- [ ] **Step 1:** Write the tests:
  - `ScopeMatrixTests`: a theory over every M1 route × {none, admin, ws-A, ws-B, peer-A/p1, peer-A/p2 non-member, session-A/s1} that asserts 2xx versus 401 according to the table above.
  - `KeyIssuerTests`: `RoundTrip`, `Expired_Rejected`, `RotatedOutKid_Rejected`, `UnknownKid_Rejected`, `Tampered_Rejected`.
  - `MemberReadPolicyTests.AllowlistContainsNoMutatingRoutes`: every route in `MemberReadRoutes.All` must be GET or one of the read-only POST `…/list` routes.
  - `EntraMappingTests.WorkspaceRole_UsesGrants`.
  - `KeyEndpointTests.PeerWithoutWorkspace_422`.
  - `StartupTests.AuthDisabledOutsideDevelopment_Throws`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Api.Tests --filter Auth`. Expected: FAIL.
- [ ] **Step 3:** Implement. Register two authentication schemes (`NachosKey` and `Entra`) behind a policy scheme that chooses by token issuer (`iss` present → Entra), plus a single `IAuthorizationHandler`.
- [ ] **Step 4:** Run it again. Expected: PASS.
- [ ] **Step 5:** Commit `feat(auth): NachosKey + Entra schemes, scope matrix, keys and grants routes`.

### Task 11: Idempotency-Key on message create (Cedar)

**Files:**
- Create: `src/Nachos.Api/Idempotency/{IdempotencyFilter,RequestHasher}.cs`; `test/Nachos.Api.Tests/IdempotencyTests.cs`.

**Interfaces:**
- Produces:
  - `RequestHasher.Hash(string method, string routeTemplate, IReadOnlyDictionary<string,string> routeValues, ReadOnlySpan<byte> canonicalBody) → string` (lowercase hex SHA-256). The canonical body is compact JSON with recursively sorted keys.
  - The filter applies to `POST M` and to the trailing-slash alias. It runs **after authorization** (spec §9.1). The idempotency record's TTL is 24 h.

- [ ] **Step 1:** Write the tests:
  - `SameKeySameBody_ReplaysStoredResponse_NoSecondInsert`
  - `SameKeyDifferentBody_422`
  - `SameKeyDifferentSession_422`: same body, different route value
  - `Replay_UnauthorizedCaller_Gets401NotStoredBody`
  - `Expired_KeyReusable_FreshOperationCommits`: after the TTL passes (using a fake `TimeProvider`), the same key with a different body returns 201 and a new batch, not 422
  - `KeyOver255OrNonAscii_Returns422`
  - `ConcurrentSameKey_OneInsert`: 8 parallel requests
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement. On `IdempotencyDuplicateException` from the store, re-read the record and replay it when the hash matches. Otherwise return 422.
- [ ] **Step 4:** Run them. Expected: PASS against in-memory. Cortado also runs them against SQL once Task 7 is merged (`NACHOS_TEST_PROVIDER=sql`).
- [ ] **Step 5:** Commit `feat(api): Idempotency-Key for message creation`.

### Task 12: .NET client (Salsa)

**Files:**
- Create: `src/Nachos.Client/{NachosHttpClient,NachosClientOptions,RetryClassifier,ServiceCollectionExtensions}.cs`; `test/Nachos.Client.Tests/{RoundTripTests,RetryBoundaryTests}.cs`.

**Interfaces:**
- Produces:
  - `NachosHttpClient : INachosClient` and `services.AddNachosClient(Action<NachosClientOptions>)`, where `NachosClientOptions { Uri BaseAddress; string? ApiKey; TokenCredential? Credential; string[] Scopes }`.
  - `RetryClassifier.IsRetryable(HttpMethod, string routeTemplate, bool hasIdempotencyKey) → bool`, following spec §16. Read-only POSTs (`…/list`) and idempotent writes (`PUT`, get-or-create POST, `DELETE`) are retryable. `H/test` and chat are never retryable. `POST M` is retryable only with a key.
  - `CreateMessagesAsync` always sends `Idempotency-Key: <guid>`.
  - Retries cover 429 / 5xx / transport errors and honor `Retry-After`: up to 3 attempts with exponential jitter.

- [ ] **Step 1:** Write the tests:
  - `RoundTripTests`: every `INachosClient` method against `NachosApiFactory`, with results equal to the in-process `NachosService` results for the same calls, after normalizing server-generated `Id` (messages) and `CreatedAt`. Shape and every deterministic field must match.
  - `RetryBoundaryTests.CommittedThenTransportFailure_RetriesWithKey_ExactlyOneBatch`: a `DelegatingHandler` lets the first response commit, then throws `HttpRequestException`.
  - `RetryBoundaryTests.WithoutKey_NoReplay`: a raw call with the key header stripped asserts a single attempt.
  - `RetryBoundaryTests.Honors_RetryAfter`.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Commit `feat(client): NachosHttpClient with operation-aware retries`.

### Task 13: Bootstrap CLI (Cortado)

**Files:**
- Create: `src/Nachos.Cli/{Program,Commands/SchemaCommands,Commands/KeyCommands,Commands/GrantCommands}.cs`; `test/Nachos.Cli.Tests/*`.

**Interfaces:**
- Produces commands, with exit codes 0 = success, 2 = refused (Unsafe/Unclassifiable), 1 = error:
  - `nachos schema status --connection <cs>` prints `platform`, `deployed`, `current`, `state` as JSON.
  - `nachos schema report --connection <cs> [--out <file>]` writes the report XML and prints the classification.
  - `nachos schema upgrade --connection <cs> [--allow-data-loss] [--report-only]`.
  - `nachos keys create (--admin | --workspace <w> [--peer <p> | --session <s>]) [--expires <iso>] --signing-secret <secret> [--kid <kid>]`. It runs offline through `IKeyIssuer`. The secret may come from `--signing-secret-env <VAR>`, so it doesn't appear in shell history.
  - `nachos grants add|remove --connection <cs> --object-id <oid> --role <role> [--workspace <w>]` and `grants list`.
- Connection strings accept `Authentication=Active Directory Default`, which is how the azd hook uses them.

- [ ] **Step 1:** Write the tests:
  - `SchemaCommandTests` (Docker): `Upgrade_EmptyDb_Exit0_ThenStatusCurrent`; `ReportOnly_DoesNotApply`.
  - `KeyCommandTests.Admin_ValidatesWithIssuer`; `.PeerWithoutWorkspace_Exit1`.
  - `GrantCommandTests.AddListRemove`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Cli.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement with `System.CommandLine`.
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Commit `feat(cli): bootstrap schema, keys, and grants commands`.

### Task 14: Aspire AppHost + FTS image recipe (Cortado)

**Files:**
- Create: `src/Nachos.AppHost/{AppHost.cs,Nachos.AppHost.csproj}`; `eng/docker/mssql-fts/Dockerfile`; `test/Nachos.AppHost.Tests/AppHostSmokeTests.cs`.

**Interfaces:**
- Produces:
  - The AppHost runs a SQL Server 2025 container (stock image in M1) with a data volume, adds a database named `nachos`, and adds `Nachos.Api` with that reference. In Development it sets `Nachos__SqlServer__AutomaticSchemaDeploymentEnabled=true` and a generated dev signing key.
  - `eng/docker/mssql-fts/Dockerfile` is `FROM mcr.microsoft.com/mssql/server:2025-latest`, then `USER root`, installs `mssql-server-fts` from the image's `microsoft-prod` apt source, then `USER mssql`. M3 adopts it.

- [ ] **Step 1:** Write `AppHostSmokeTests.ApiReady_AndWorkspaceRoundTrip`, using `DistributedApplicationTestingBuilder`. It waits for `/health/ready`, then POSTs a workspace and gets 200.
- [ ] **Step 2:** Run it. Expected: FAIL.
- [ ] **Step 3:** Implement the AppHost.
- [ ] **Step 4:** Run it. Expected: PASS.
- [ ] **Step 5:** Run `docker build -t nachos-mssql-fts eng/docker/mssql-fts`, start the container, and run `SELECT SERVERPROPERTY('IsFullTextInstalled')`. Expected: `1`. Record the output in the PR. If apt is unreachable, post the failure as a finding; the recipe gate then moves to M3 CI.
- [ ] **Step 6:** Commit `feat(dev): Aspire AppHost and validated FTS image recipe`.

### Task 15: Bicep + azd, offline (Salsa)

**Files:**
- Create: `azure.yaml`; `infra/main.bicep`, `infra/main.parameters.json`, `infra/modules/{identity,monitoring,registry,keyvault,sql,containerapps-env,api-app}.bicep`; `infra/hooks/{postprovision,postdeploy}.ps1` and `.sh`; `test/Nachos.Infra.Tests/InfraTests.cs`.

**Interfaces:**
- Produces:
  - Resources per spec §18.2 for M1 (API only, no worker yet): a user-assigned MI; Log Analytics + App Insights; ACR (AcrPull for the MI); Key Vault (RBAC; Secrets User for the MI); an Azure SQL server with **Entra-only auth**, the admin being the deploying principal (`principalId` parameter), plus a serverless GP database with auto-pause disabled; a Container Apps environment; and the `nachos-api` app (external ingress, min 1).
  - Parameters: `environmentName`, `location`, `principalId`, `principalLogin`, `openAiEndpoint` (empty in M1).
  - `postprovision`:
    1. Creates the MI user with `CREATE USER [<mi>] WITH SID = <clientId-as-binary>, TYPE = E` plus `db_datareader`/`db_datawriter`/`db_ddladmin`, via `sqlcmd` with Entra auth.
    2. Runs `dotnet run --project src/Nachos.Cli -- schema upgrade --connection "<cs;Authentication=Active Directory Default>"`.
    3. If `nachos-bootstrap-admin-key` is absent from Key Vault, generates a signing secret, stores it as `nachos-signing-key-0`, and stores a minted admin key.
  - `postdeploy`: `curl -fsS https://<api>/health/ready`.

- [ ] **Step 1:** Write `InfraTests`:
  - `Bicep_Builds`: shells out to `az bicep build --file infra/main.bicep --stdout` and asserts exit code 0. It is skipped with an explicit reason if `az` is missing locally; in CI it is required.
  - `AzureYaml_ServicesPointAtExistingProjects`.
  - `Sql_IsEntraOnly`: the compiled ARM JSON contains `azureADOnlyAuthentication: true`.
  - `Repo_HasNoUnattendedAzurePath`: scan `.github/workflows/**` and `eng/**` for `azd (up|provision|deploy|down)`, `az deployment`, `az group`, `sqlpackage …Publish`, `infra/hooks`, `docker push`, and `az acr`. Fail on any hit, except inside a workflow that is `workflow_dispatch`-only **and** declares `environment: azure-live`.
  - `Repo_HasNoUnattendedAzurePath_DetectsPlantedAzdUp`: a mutation check where a temp workflow containing `azd up` on `push` makes the scanner fail.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Infra.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement. **Do not run `azd up`/`provision`/`deploy` or `what-if`. These are owner-gated.**
- [ ] **Step 4:** Run the tests again, plus `az bicep lint --file infra/main.bicep`. Expected: PASS, with no lint errors.
- [ ] **Step 5:** Commit `feat(infra): Bicep + azd for API, SQL (Entra-only), Key Vault, ACR (offline-validated)`.

### Task 16: Upstream-SDK conformance (Salsa)

**Files:**
- Create: `eng/conformance/Start-NachosForConformance.ps1`; `test/conformance/python/{pyproject.toml,requirements.lock,conftest.py,test_crud.py,test_auth.py}`; `test/conformance/typescript/{package.json,package-lock.json,crud.test.mjs}`; `.github/workflows/conformance.yml`.

**Interfaces:**
- Consumes:
  - the running API (in-memory, or SQL via `-Provider Sql -ConnectionString`);
  - `nachos keys create` (Task 13) for an admin key and scoped keys;
  - the SDKs **only through their public documented API**: `honcho-ai` (PyPI) and `@honcho-ai/sdk` (npm), pinned exactly to their latest versions on the day of this task. Python uses `==` pins plus a hash-locked `requirements.lock` (`pip install --require-hashes`); TypeScript uses `package-lock.json`. The resolved SDK versions and the wire pin (3.3.0) are recorded in the M1 PR body, and any later SDK bump is its own reviewed change. Never read the SDK source.
- Produces: `.github/workflows/conformance.yml` (on PR and push; in-memory provider; no secrets; `contents: read`).

- [ ] **Step 1:** Write the scenarios in both languages:
  - workspace/peer/session get-or-create and metadata update;
  - list with filters and auto-pagination over 120 sessions;
  - session peers add/set/remove and peer config get/set;
  - batch message create (100), list, get, metadata update;
  - a peer-scoped key reading member messages succeeds, while the same key listing workspaces fails;
  - an M2+ call (for example peer chat) surfaces a 501 error, not a hang.
- [ ] **Step 2:** Run `pwsh eng/conformance/Start-NachosForConformance.ps1 -Run`. Expected: initially FAIL wherever an earlier task is incomplete, then PASS once Tasks 9–11 are merged.
- [ ] **Step 3:** Commit `test(conformance): upstream Honcho SDK CRUD conformance`.

### Task 17: CI workflow (Cedar)

**Files:**
- Create: `.github/workflows/ci.yml`; `eng/license-check/{Check-Licenses.ps1,allowlist.json}`; `test/Nachos.LicenseCheck.Tests/*` (fixture-driven).

**Interfaces:**
- Produces these jobs (`permissions: contents: read`; actions pinned by SHA; no Azure; `on: push, pull_request`):
  - **build-test:** `dotnet build Nachos.slnx -c Release -warnaserror` and `dotnet test` for all projects except Docker ones.
  - **sql-integration** (`ubuntu-latest`, Docker available): `dotnet test` for the SqlServer, Cli, and AppHost test projects.
  - **schema:** builds both dacpacs. Starts an `mssql/server:2025-latest` service container and runs `sqlpackage /Action:DeployReport` with the Sql2025 dacpac against an empty database. Uploads both dacpacs and the report as artifacts.
  - **licenses:** `Check-Licenses.ps1` enforces spec §3 rule 7 and fails closed.
    - **Inputs, every ecosystem in use:** NuGet (`dotnet list package --include-transitive --format json`, with license evidence from each `.nupkg`); npm (each `package-lock.json`, with license evidence from `node_modules/<pkg>` license files); Python (`test/conformance/python/requirements.lock`, with evidence from each downloaded wheel or sdist: `License-Expression` metadata and its license file).
    - **Evidence rule:** a package passes only when its license **text** identifies the license. Metadata alone is not enough. If the metadata and the text disagree, or the text is unavailable (for example `licenseUrl` only), the check fails unless a reviewed entry in `eng/license-overrides.json` records the package, version, license, and evidence URL. SPDX `OR` requires a recorded choice. For `AND`, every component must pass.
    - **Tier = artifact, never a dev flag.** Shipped tier: every package appearing in an emitted artifact, namely the `deps.json` of `dotnet publish` output for `Nachos.Api` and `Nachos.Cli`, plus the docs-site bundle module manifest emitted by Task 18 (`docs/site/dist/.nachos/bundle-modules.json`). Everything else is the tooling tier.
    - A package in `eng/license-exceptions.json` that appears in any emitted artifact fails.
  - **infra:** `az bicep build` and `az bicep lint` (no login).
- Extend: if `docs-validate` still walks `docs/` only, add `README.md` in Task 18.

- [ ] **Step 1:** Write `LicenseCheckTests`: `GplPackage_Fails`, `UnknownLicense_FailsClosed`, `EplInTooling_WithException_Passes`, `EplInShippedProject_Fails`, `OrExpression_RecordsSelectedLicense`, `DisallowedPythonDependency_Fails`, `MetadataTextDisagreement_Fails`, `LicenseTextUnavailable_FailsWithoutOverride`, `ExceptedToolingPackageInDepsJson_Fails`, `ExceptedToolingPackageInDocsBundle_Fails`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.LicenseCheck.Tests`. Expected: FAIL, then PASS after implementation.
- [ ] **Step 3:** Push and confirm every job is green on GitHub, citing the run IDs.
- [ ] **Step 4:** Commit `ci: build, SQL integration, schema report, license tiers, offline infra checks`.

### Task 18: README, logo, docs site, DocsGen, Pages workflow (Cedar)

**Files:**
- Create:
  - `README.md` (spec §22.1, M1 subset: only features that have shipped);
  - `docs/assets/nachos-logo.svg` and a 350-px `nachos-logo.png` exported from it;
  - `docs/site/{package.json,package-lock.json,astro.config.mjs,tsconfig.json,src/content.config.ts,src/styles/custom.css}`;
  - `docs/site/src/content/docs/{index.mdx, getting-started/{introduction,quick-start,deploy-azure,self-host,library}.mdx, concepts/{workspaces-and-peers,sessions-and-messages}.mdx, guides/{auth,schema-upgrades,honcho-sdks}.mdx, reference/compatibility.mdx}`;
  - `eng/DocsGen/{DocsGen.csproj,Program.cs}`;
  - `.github/workflows/docs-site.yml`.
- Modify: `.github/scripts/validate-docs.mjs` and its tests, so that root `README.md` is also link-checked. Cedar owns these files.

**Interfaces:**
- Consumes:
  - `Nachos.Api`'s build-time OpenAPI (`Microsoft.Extensions.ApiDescription.Server`, output `src/Nachos.Api/openapi/nachos.json`);
  - `Nachos.Cli --help` output;
  - `test/contracts/honcho-v3-wire.json`, for the compatibility matrix of implemented vs 501 routes.
- Produces:
  - `dotnet run --project eng/DocsGen` writes `docs/site/src/content/docs/reference/_generated/{rest-api.json,cli.md,options.md,compatibility.json}`.
  - The site configuration: Starlight with `site: 'https://brendankowitz.github.io'` and `base: '/nachos'`, Pagefind search, `rehype-mermaid`, `starlight-openapi`, and `starlight-links-validator`.
  - `docs-site.yml`:
    - on PR: DocsGen, then `npm ci`, then `astro build` (links validated);
    - on push to `main` and on dispatch: build plus `actions/deploy-pages`, with `pages: write` / `id-token: write` permissions and the `pages` concurrency group;
    - the deploy job is skipped unless Pages is enabled;
    - no Azure.
  - Docs-site npm dependencies are checked by Task 17's license tiers against the **emitted bundle**. A small Astro/Vite integration writes `docs/site/dist/.nachos/bundle-modules.json`, the sorted list of `{package, version}` for every module that Rollup emitted into any client chunk or copied asset. Task 17 consumes it to prove that no excepted package ships. `DocsGenTests.BundleManifest_ListsEmittedPackages` covers it.

- [ ] **Step 1:** Write `DocsGenTests.GeneratesAllReferenceFiles` and a validator regression fixture, `readme-link-missing` → fails.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement. The README follows the spec §22.1 section order. Every shell command in the README and in getting-started is exercised by CI (quick start via the AppHost smoke test, plus the Task 16 conformance flow).
- [ ] **Step 4:** Run `dotnet run --project eng/DocsGen && npm --prefix docs/site ci && npm --prefix docs/site run build`, then `npm --prefix .github/scripts run check`. Expected: the site builds with zero link errors, and the validator passes.
- [ ] **Step 5:** Commit `docs: README, logo, Starlight site, generated reference, Pages workflow`.

---

## M1 Done checklist

The M1 PR merges when all of these hold:

- [ ] All 18 tasks are committed, each with its tests.
- [ ] `dotnet test` (all projects) is green locally for every agent at the final SHA. Cortado and Salsa post the Docker suite results with the SHA. Cedar posts the full validation set.
- [ ] CI jobs `build-test`, `sql-integration`, `schema`, `licenses`, `infra`, `docs-validate`, `docs-site` (build), and `conformance` are green at the final SHA.
- [ ] `ConcurrentSameKey_OneInsert` and the store concurrency tests have been looped 20 times on SQL (Cortado or Salsa).
- [ ] Cortado, Cedar, and Salsa each post LGTM at the same SHA.
- [ ] The spec is updated for clarifications 1–5, and the README and docs pages match what has shipped.

## Owner actions requested (not performed by agents)

1. **Enable GitHub Pages** (Settings → Pages → Source: GitHub Actions), so `docs-site.yml` can deploy.
2. **Approve an M1 Azure smoke deployment.** The agents provide the exact commands in the PR:
   ```
   azd env new nachos-m1 --location <region> --subscription <id>
   azd up
   ```
   The run verifies `CREATE USER … WITH SID` (spec §18.2), auto-safe schema deploy, bootstrap key storage, and `/health/ready`. To clean up afterwards: `azd down --purge`, which also needs approval.
3. **(Optional, for future dispatch-only CI)** Create the `azure-live` GitHub Environment with yourself as the required reviewer, and configure the OIDC federated credential.
