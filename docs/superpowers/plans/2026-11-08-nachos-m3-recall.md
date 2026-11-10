# Nachos M3 — Recall Implementation Plan

> **For agentic workers:** execute each task with the `implement-task-next` skill (task slug `issue-2-m3-<name>`). Steps use checkbox (`- [ ]`) syntax for tracking. Do not start a task until every task on its **Depends on** line is merged on the milestone branch (`nachos-m3`). Open questions are in [Owner questions](#owner-questions); each has a proposed default that applies unless the owner answers otherwise.

**Goal:** Memory can be read back the way an agent needs it, and data can be deleted.
- Messages are chunked and embedded.
- Sessions get rolling short and long summaries.
- Hybrid search (vector + full-text, fused with RRF) works for workspace, session and peer-perspective scopes.
- `GET S/context` returns a session context that **never exceeds its token budget**.
- `DELETE S` and `DELETE W` are accepted (202) behind the §13.1 write barrier and run as resumable deletion jobs, visible through `W/jobs`.
- Every new read goes through `VisibilityPolicy`.

**Architecture:**
- `Nachos.Abstractions` gains:
  - `ISummaryStore`, `IMessageSearch`, `IDeletionStore`;
  - message chunks in `IEmbeddingStore`;
  - a standalone `IWorkQueue.EnqueueAsync` (the deletion acceptance is the first non-append producer);
  - one `MessageAppend` request record for the append.
- `Nachos.Core` gains:
  - `MessageChunker`;
  - `SummarizerService` (handler for `summary` items);
  - `HybridSearchService`;
  - `SessionContextService` (the hard budget);
  - `DeletionService` (handler for `deletion` items);
  - `JobService`.
- Both providers implement the new stores. The SQL provider adds two tables, a lexical index with a `LIKE` fallback, and the write barrier on every existing write path.
- `Nachos.Api` maps the M3 routes. `Nachos.Client` gains the matching methods.

**Tech Stack:** M2's stack, plus:
- SQL Server full-text search (`CONTAINSTABLE`) on the FTS-enabled SQL Server 2025 image already built and verified in `eng/docker/mssql-fts`;
- the `LIKE` fallback when `SERVERPROPERTY('IsFullTextInstalled') = 0`;
- `Microsoft.ML.Tokenizers` for chunking (already referenced).

No new NuGet packages are expected. Budget property tests use seeded xUnit theories, not a property-testing library.

**Spec:** [`docs/superpowers/specs/2026-10-07-nachos-design.md`](../specs/2026-10-07-nachos-design.md): §7.1–7.4, §8, §9.1, §9.3 (M3 rows), §10.1–10.2, §10.5, §11.1 (chunking), §11.3, §11.5, §11.6, §11.8, §13, §17, §20 (M3), §21 (R1, R2). Roadmap: [`2026-10-07-nachos-roadmap.md`](2026-10-07-nachos-roadmap.md). Tracking issue: #2.

## Global Constraints

These carry over from M1/M2 and the roadmap:

- **Clean-room (spec §3):** never read Honcho `src/`, `sdks/` or `mcp/`, or Honcho's prompt text. The summarizer prompts are written from spec §11.3 alone. Wire names and default values may match.
- **Licenses (spec §3 rule 7):** checked by review when a dependency is added. There is no license tooling.
- **Schema (spec §7.3, roadmap rule 1):** all DDL lives in `src/DataLayer/Nachos.DataLayer.SqlServer.Database/`. Every M3 change is additive and must classify `AutoSafe`. The schema version stays `1` (M2 Q1 still applies). EF Core never creates migrations. `AllowIncompatiblePlatform` is never set.
- **No stubs as features (roadmap rule 3):** a route stays `501` until its task lands. M4+ routes stay `501`.
- **Interfaces flow forward (roadmap rule 2):** the existing `IMessageStore.AppendAsync` overloads keep working and forward to the new one.
- **Azure consent (spec §18.3):** no agent runs `azd`, `az deployment`, live Azure OpenAI calls, or registry pushes without a per-run, untagged owner approval.
- **Coding standards:** the owner's .NET standards (`~/.claude/CLAUDE.md`) apply. Seal classes, use one constructor and constructor injection, pass `CancellationToken` all the way down, return `IReadOnlyList<T>`, and add no speculative abstraction. Comments only for a non-obvious *why*.
- **SDK/CPM/Nullable/TreatWarningsAsErrors:** unchanged.

## Ownership and sequencing

The tasks are agent-neutral. One rule still holds: **SQL/Testcontainers-heavy tasks (1, 3, 7's SQL half, 8's SQL half, 10, 11's schema half) need a Docker-capable host.**

```
1 Schema + FTS image ──> 3 SQL recall stores ──┬─> 5 Hybrid search ──┐
2 Abstractions + in-memory ─┬─> 3              │                     ├─> 9 API routes + client ─> 10 Benchmark, AppHost, docs
                            ├─> 4 Chunker + Summarizer ─> 6 Session context ┘
                            └─> 7 Write barrier ─> 8 Deletion jobs + W/jobs ─> 9
```

- Tasks 1 and 2 can start immediately and in parallel. Task 4 is developed against 2 (it runs on the in-memory provider), but it **merges only after Task 3**: until then the SQL provider rejects appends that carry chunks or summary work, so every SQL append would fail.
- Task 7 needs only 2 for its contract tests. Its SQL half touches every existing SQL write path, so it should not run at the same time as Task 3 on the same files. Coordinate in the PR's coordination table.
- Task 9 can land route by route as its services do.
- Task 11 (local embeddings) needs 3 and 4 and is independent of 5–10.

## Review Focus

These are the high-risk behaviors. Each is pinned by a named test in its owning task.

1. **The context budget is never exceeded.** This includes budgets smaller than the newest message, a representation that alone is over 50% of the budget, a summary larger than its 40% share, and `tokens = 1`. Pinned by `SessionContextBudgetTests.NeverExceedsBudget` (seeded theory, 500 random cases) and `.SingleMessageLargerThanBudget_ReturnsNoMessages` (Task 6).
2. **User text cannot break full-text syntax.** A search query containing `"`, `*`, `NEAR`, `AND`, `(`, `~`, noise words only, or non-Latin text returns a normal result and never a 500. Pinned by `FullTextConditionTests.HostileTerm_FallsBackToLike` (Task 3), `SqlMessageSearchTests.HostileQueries_Return200` (Task 3), and `LexicalQueryTests.HostileQuery_IsTermsOnly` and `.DropsTermsOverMaxLength` (Task 5).
3. **Search sees only what the caller may see.** Peer-perspective search returns only messages inside that peer's `JoinedAt`/`LeftAt` windows, and a session-scoped search never returns another session's message. Pinned by `SearchVisibilityNegativeTests` (Task 5).
4. **The write barrier holds under races.** Covers acceptance against a concurrent create, a late worker commit after acceptance, and a crash mid-purge. These are the six spec §13.1 tests, each with a deterministic pause hook. Pinned by `SqlDeletionBarrierTests` (Task 7/8).
5. **Messages that existed before M3 become searchable.** Messages appended by an M1/M2 build have no chunks. The reconciler backfills them, and lexical search finds them before they are embedded. Pinned by `ReconcilerTests.BackfillsChunksForUnchunkedMessages` and `HybridSearchTests.PendingEmbedding_StillFoundLexically` (Tasks 4, 5).

---

## Spec-vs-code divergences (resolved by this plan)

| # | Spec says | Code on `main` | Plan |
|---|---|---|---|
| D1 | `Messages` has a full-text index on `Content` (§7.2). FTS DDL lives in the dacpac (§7.3). | No FTS index. The stock SQL Server 2025 image has no FTS. A dacpac carrying `CREATE FULLTEXT INDEX` fails to deploy where FTS isn't installed, which breaks the spec's own `LIKE` fallback (R2). | **(Q1 default)** The full-text catalog and index are created **at runtime** by a readiness-gated maintenance step, mirroring the spec's ANN pattern (§7.4). It runs only when `IsFullTextInstalled = 1`. Until the index is populated, lexical search uses `LIKE`. The dacpac stays deployable everywhere. |
| D2 | `IMessageStore.AppendAsync` gains parameters per milestone. | Two overloads already (M1, M2). A third positional overload would be unreadable. | Add `AppendAsync(string ws, string session, MessageAppend append, CancellationToken)`, where `MessageAppend` is `{ Messages, Idempotency?, Derive?, Summary? }`. Both existing overloads forward to it. |
| D3 | A `summary` work item is enqueued "when the short or long threshold is crossed" (§10.2 step 5). | Seq numbers are allocated inside the store, so Core cannot know which batch crosses a threshold. | Core resolves the session's summary configuration and passes a `SummaryIntent(ShortEvery, LongEvery)`. Inside the append transaction, the store enqueues one `summary` item per kind whose multiple of `Every` falls in `[firstSeq, lastSeq]`. The payload is `{ "kind": "short"|"long", "through_seq": n }`. |
| D4 | `IWorkQueue` has no standalone enqueue until the first non-append producer (§7.1). | — | M3's producer is deletion acceptance. Acceptance must insert its item **in the same transaction** as the lifecycle change, so it lives in `IDeletionStore.BeginDelete*Async`, not in a separate enqueue. **No standalone `EnqueueAsync` is added in M3.** Spec §7.1 is amended to say so. |
| D5 | Session context budget: "the response must never exceed `tokens`" (§11.6). | — | The budget counts the **content tokens** (`ITokenCounter`) of: the representation text, each card entry, the summary content, and each message's content. The JSON envelope and field names are not counted. Documented in the context concept page. |
| D6 | `GET W/jobs` (Δ extension) has no wire shape. | — | `GET W/jobs?page=&size=` returns the standard page envelope of `Job { id, type, target: { kind, id }, status, attempts, error?, created_at, completed_at? }`. M3 lists only `deletion` jobs. `id` is the work item id as a string. |
| D7 | Session deletion tombstones "unsupported" derived conclusions and enqueues a `card_refresh` dream (§13). | M2 creates only explicit conclusions. Derived ones and dreams arrive in M5. | M3 hard-deletes the session's explicit conclusions. It tombstones any conclusion whose `ConclusionSources` all point at deleted conclusions (in M3 only API-created rows can have sources). It enqueues **no** dream: the `dream` handler is M5, and enqueuing an unclaimable item would only grow the queue. M5 adds the enqueue. |
| D8 | Tokenization uses `o200k_base` for budgets (§11.1). Issue #10 (exact managed counter) is a release gate. | `TiktokenTokenCounter` (MVP, `Microsoft.ML.Tokenizers`). | M3 uses the existing `ITokenCounter`. **#10 is not in M3**: it needs a specific owner approval of the Unicode-data policy exception first (Q4). The budget guarantee holds for whatever counter is registered, because the final check (§11.6 step 4) uses the same counter. |
| D9 | §11.5 hybrid search: the lexical side is a full-text ranking. | Not built before M3. | **Lexical search matches any term**, ranked by distinct terms matched, then newest, identically on SQL (full-text and `LIKE`) and in-memory; full-text `RANK` and occurrence counts are not used. Core drops a fixed English stopword set when another term remains. All-terms matching made question-style queries find nothing lexically, which broke lexical-only mode (no embedding generator). Ruled by the coordinator in Task 5 review; the owner may overrule. Task 10 measures the per-term `CONTAINS` cost. |

---

## Tasks

### Task 1: M3 schema and the FTS test image

**Files:**
- Create in `src/DataLayer/Nachos.DataLayer.SqlServer.Database/Tables/`: `MessageEmbeddings.sql`, `SessionSummaries.sql`.
- Modify `test/Nachos.DataLayer.SqlServer.Tests/SqlServerFixture.cs`: build and use the `eng/docker/mssql-fts` image (Testcontainers `ImageFromDockerfileBuilder`). Add a second fixture, `SqlServerNoFtsFixture`, on the stock image for the fallback tests.
- Modify `src/Nachos.AppHost/AppHost.cs`: use the FTS image (`AddSqlServer(...).WithDockerfile("../../eng/docker/mssql-fts")`). Keep the comment that pins the base tag.
- Modify the CI workflow's SQL job to build the FTS image once and cache it. `eng/ci/test-fts-image.sh` already verifies it.
- Tests: `test/Nachos.DataLayer.SqlServer.Tests/Schema/M3SchemaTests.cs`.

**Depends on:** none.

**Tables** (M1/M2 conventions: `bigint IDENTITY` clustered PK, explicit `NONCLUSTERED` uniques, composite `WorkspaceId` FKs, application-clock `datetimeoffset(7)`):
- `MessageEmbeddings(Id, WorkspaceId, MessageId, SessionId, ChunkIndex smallint, Content nvarchar(max), Embedding VECTOR(1536) NULL, SyncState tinyint (0 Pending, 1 Synced, 2 Failed), Attempts int DEFAULT 0, CreatedAt)`.
  - `UQ (MessageId, ChunkIndex)`; `IX (WorkspaceId, SessionId)`; `IX (SyncState)` (plain, not filtered: `ANSI_NULLS OFF`, see spec §7.2).
  - FK to `Messages (WorkspaceId, Id)` and `Sessions (WorkspaceId, Id)`. `SessionId` is denormalized so session- and window-filtered vector search needs no join.
- `SessionSummaries(Id, WorkspaceId, SessionId, Kind tinyint (1 Short, 2 Long), Content nvarchar(max), CoversThroughMessageId bigint, CoversThroughSeq bigint, TokenCount int, CreatedAt)`.
  - `UQ (SessionId, Kind)`: one current summary per kind, replaced in place.
  - FK to `Sessions` and to `Messages (WorkspaceId, Id)` for the coverage point.
- **No FTS DDL in the dacpac** (D1).

- [ ] **Step 1:** Write `M3SchemaTests`:
  - `M3Tables_DeployToEmptyDatabase` (both fixtures);
  - `M2Database_ReviewedUpgrade_AddsM3Tables` (deploy the M2 dacpac, then `schema upgrade --approve-reviewed`; the report classifies the change as additive);
  - `StampedCurrent_MissingM3Table_FailsReadinessWithFixCommand` (extends the M2 `SchemaProbe` table list);
  - `AllIndexKeys_WithinSqlLimits` (extended);
  - `DacpacDeploys_WithoutFullTextInstalled` (stock image);
  - `FtsFixture_ReportsFullTextInstalled` (`SERVERPROPERTY('IsFullTextInstalled') = 1`).
- [ ] **Step 2:** Run `dotnet test test/Nachos.DataLayer.SqlServer.Tests --filter M3Schema`. Expected: FAIL.
- [ ] **Step 3:** Write the DDL, the fixtures, and the AppHost and CI changes. Build both `.sqlproj` projects.
- [ ] **Step 4:** Run it again. Expected: PASS. Also run the full `SchemaDeployerTests` and the M2 schema tests.
- [ ] **Step 5:** Amend spec §7.2 (D1; the two tables as built) and §7.3 (FTS outside the dacpac). Commit `feat(schema): M3 message embeddings and session summaries, FTS test image`.

### Task 2: Abstractions and in-memory provider

**Files:**
- Create in `src/Nachos.Abstractions/`:
  - `Domain/{MessageAppend,SummaryIntent,SummaryKind,SummaryRecord,MessageChunk,MessageSearchRequest,MessageSearchScope,MessageHit,DeletionTarget,DeletionTargetKind,DeletionAcceptance,JobRecord}.cs`;
  - `Stores/{ISummaryStore,IMessageSearch,IDeletionStore}.cs`;
  - No new exception type: writes under `Deleting` throw the existing `ConflictException` with `"<resource> is being deleted"`, which the API already maps to `409 {"detail": …}`.
- Modify:
  - `Stores/IMessageStore.cs` (D2);
  - `Stores/IEmbeddingStore.cs` (chunks);
  - `Stores/IMemoryStore.cs` (`Summaries`, `Search`, `Deletion`);
  - `Domain/PendingEmbedding.cs` (a `Kind`: conclusion or message chunk).
- Create in `src/DataLayer/Nachos.DataLayer.InMemory/Stores/`: the in-memory implementations.
- Add the abstract suites `M3StoreContractTests`, `SearchContractTests`, `DeletionContractTests` and `DeletionPurgeContractTests` (on `M3ContractTestBase`) in `test/Nachos.Testing/`. They are separate from `StoreContractTests` because xunit 2.9.3 cannot skip at runtime; the SQL provider derives from them in Tasks 3, 7 and 8.

**Depends on:** none.

**Interfaces** (workspace-scoped by name, like M1/M2; vectors as `ReadOnlyMemory<float>`):
- `MessageAppend(IReadOnlyList<NewMessage> Messages, IdempotencyWrite? Idempotency, IReadOnlyList<DeriveIntent>? Derive, IReadOnlyList<IReadOnlyList<string>>? Chunks, SummaryIntent? Summary)`. `Chunks` has one entry per message, in order.
  - Inside the existing append transaction the provider:
    - inserts one `Pending` `MessageEmbeddings` row per chunk (from `MessageAppend.Chunks`; Core fills them, Task 4);
    - inserts the `summary` items per D3. Work-unit key: `WorkUnitKeys.Summary(ws, session)`, which uses the same 128-character hashing rule as M2.
- `ISummaryStore`:
  - `GetAsync(ws, session, SummaryKind, ct) → SummaryRecord?`;
  - `ListAsync(ws, session, ct) → IReadOnlyList<SummaryRecord>`;
  - `SaveAsync(WorkLease lease, ws, session, IReadOnlyList<NewSummary> summaries, ct)`, where `NewSummary(Kind, Content, CoversThroughMessageId, CoversThroughSeq, TokenCount)` has no time: the store stamps `CreatedAt` from its clock. It takes a list because one lease can hold both a short and a long item. It is fenced like `CommitDerivationAsync`: it checks the lease and its unit key, takes the §13.1 S locks, replaces each `(session, kind)` row **only if** the new `CoversThroughSeq` is greater, and completes the lease's items in the same transaction. It throws `LeaseLostException` when the lease is gone. For a `Deleting` or already-removed session it completes the items and writes nothing (`resource_deleted`).
- `IMessageSearch`:
  - `VectorAsync(MessageSearchRequest req, ReadOnlyMemory<float> query, int take, ct) → IReadOnlyList<MessageHit>`;
  - `LexicalAsync(MessageSearchRequest req, IReadOnlyList<string> terms, int take, ct) → IReadOnlyList<MessageHit>`.
  - `MessageSearchRequest(string Workspace, MessageSearchScope Scope, FilterNode? Filter)`. `MessageSearchScope` is `Workspace` | `Session(name)` | `PeerPerspective(peer)`.
  - `MessageHit(MessageRecord Message, int Rank)`, in rank order. The vector rank uses a message's best chunk distance.
  - **Peer perspective** is applied inside the store: a message is visible when the peer has a `SessionPeers` row for its session with `JoinedAt <= CreatedAt < COALESCE(LeftAt, max)`.
  - `terms` are already sanitized by Core (Task 5). The store never parses user text.
- `IEmbeddingStore`: `ListPendingAsync` and `CountPendingAsync` now return conclusion **and** chunk items (`PendingEmbedding.Kind`).
  - Add `ListUnchunkedMessagesAsync(int limit, ct)` and `AddChunksAsync(IReadOnlyList<MessageChunk>, ct)` for the reconciler backfill. Insertion is idempotent on `(MessageId, ChunkIndex)`.
- `IDeletionStore`:
  - `BeginDeleteSessionAsync(ws, session, ct) → DeletionAcceptance(long JobId, bool AlreadyDeleting)`;
  - `BeginDeleteWorkspaceAsync(ws, ct) → DeletionAcceptance`, which throws `ConflictException` ("workspace has active sessions") per §13.1. Both follow §13.1 exactly, including the idempotent repeat. Each inserts its `deletion` item (`WorkUnitKeys.Deletion(ws, kind, name)`) and sets `DeletionJobId` in the same transaction.
  - `CancelOtherWorkAsync(WorkLease lease, DeletionTarget target, ct)`: §13.1 job step 1.
  - `PurgeBatchAsync(WorkLease lease, DeletionTarget target, int batchSize, ct) → bool morePending`: child-first, then checkpoints `InternalMetadata.deletion_progress`.
  - `RemoveAsync(WorkLease lease, DeletionTarget target, ct)`: removes the row and completes the item.
  - Every member checks `DeletionJobId == lease item id` and the fencing token. It throws `LeaseLostException` otherwise.
  - `ListJobsAsync(ws, PageRequest, ct) → Page<JobRecord>`.
- **Write admission (in-memory):** every write under a `Deleting` workspace or session throws `ConflictException`, under the existing per-workspace lock. The SQL half is Task 7.

- [ ] **Step 1:** Write the contract tests (abstract, in `Nachos.Testing`):
  - `Append_InsertsPendingChunks_Atomically` (including serializer-throw rollback);
  - `Append_SummaryThresholdCrossed_EnqueuesOnePerKind` (theory: a batch spanning seq 18–23 with short=20/long=60 → one short item with `through_seq` 20; a batch spanning 59–61 → short and long);
  - `Append_SummaryDisabled_EnqueuesNothing`;
  - `SaveSummary_OlderCoverage_IsIgnored`;
  - `SaveSummary_LeaseLost_WritesNothing`;
  - `Search_SessionScope_ExcludesOtherSessions`;
  - `Search_PeerPerspective_RespectsMembershipWindows` (join at t1, leave at t2, rejoin elsewhere);
  - `Search_Filter_UsesMessageAllowlist`;
  - `Unchunked_ListsOnlyMessagesWithoutChunks`;
  - `DeleteSession_Repeat_ReturnsSameJob`;
  - `DeleteWorkspace_ActiveSessions_Conflicts`;
  - `WritesUnderDeleting_Throw` (one case per covered write in §13.1, and for `POST C`);
  - `Purge_IsResumableFromCheckpoint`;
  - `Purge_StaleFencingToken_Rejected`;
  - `Remove_FreesNameForReuse`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.DataLayer.InMemory.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement the types and the in-memory provider. Lexical search uses case-insensitive ordinal term containment (any term matches, D9), ranked by distinct terms matched, then recency. Vector search uses brute-force cosine over chunks.
- [ ] **Step 4:** Run it again. Expected: PASS. Run the architecture tests too (Abstractions stays BCL-only).
- [ ] **Step 5:** Amend spec §7.1 (D2, D4). Commit `feat(abstractions): M3 recall and deletion contracts, in-memory provider`.

### Task 3: SQL recall stores and the lexical index

**Files:**
- Create in `src/DataLayer/Nachos.DataLayer.SqlServer/`:
  - `Stores/{SqlSummaryStore,SqlMessageSearch}.cs`;
  - `Search/FullTextIndexMaintenance.cs` (an `IHostedService`; D1);
  - `Entities/*` for the new tables.
- Modify: `Stores/SqlMessageStore.cs` (chunks and summary items in the append transaction), `Stores/SqlEmbeddingStore.cs` (chunks, unchunked listing), `SqlMemoryStore.cs`, `Schema/SchemaProbe.cs` (table list).
- Tests: `test/Nachos.DataLayer.SqlServer.Tests/{SqlM3StoreContractTests,SqlMessageSearchTests,FullTextIndexMaintenanceTests}.cs`.

**Depends on:** 1, 2.

**Notes:**
- **Vector search:** `SELECT TOP (@take) … ORDER BY MIN(VECTOR_DISTANCE('cosine', e.Embedding, @q))`, grouped by message. The candidates are pre-filtered by `WorkspaceId`, then by `SessionId` or by the peer's membership windows (a join on `SessionPeers`), then by the message filter (existing `FilterWriter`). Only `Synced` chunks are searched. The search is exact (spec §7.4).
- **Lexical search:**
  - With an active, populated full-text index it uses `CONTAINSTABLE(Messages, Content, @expr)` for candidates, ordered by distinct terms matched (one `CONTAINS` per term), then newest (D9; not `RANK`).
  - `@expr` is built **only** from Core's sanitized terms, as `"t1*" OR "t2*" …`, with each term double-quote-escaped. It is always a parameter, never concatenated into SQL.
  - Otherwise it uses `Content LIKE @p ESCAPE '\'` per term, summed per row (any term matches), with `%`, `_`, `[` and `\` escaped, ordered by distinct terms matched and then newest.
  - The mode comes from `FullTextIndexMaintenance.IsReady`.
- **`FullTextIndexMaintenance`** runs after the schema gate passes. When `IsFullTextInstalled = 1` and the index is missing, it creates the catalog `NachosFts` and `CREATE FULLTEXT INDEX ON dbo.Messages(Content) KEY INDEX PK_Messages WITH CHANGE_TRACKING AUTO`. `IsReady` is set as described below.
  - The app identity needs `CREATE FULLTEXT CATALOG` (`db_ddladmin` already grants it, spec §18.2).
  - It runs on the worker host and on the in-process API host when SQL is used. Creation is idempotent and guarded by the `reconciler:fts` lease, so only one replica issues the DDL.
  - `CREATE FULLTEXT INDEX` cannot run inside a user transaction: issue it autocommit, never under an EF or ambient transaction.
  - Treat `IsFullTextInstalled` NULL as not installed. Readiness uses `OBJECTPROPERTYEX(OBJECT_ID('dbo.Messages'), 'TableFullTextPopulateStatus') = 0` (`PopulateStatus` is deprecated), polled slowly (every 30 s), and NULL means not ready.
- Lock order stays M2's: workspace → session → messages → embeddings → work items → summaries.

- [ ] **Step 1:** Write `SqlM3StoreContractTests : M3StoreContractTests` and `SqlSearchContractTests : SearchContractTests`, which inherit Task 2's cases. Task 2 put the M3 cases in separate abstract suites because xunit 2.9.3 cannot skip at runtime. Also write:
  - `SqlMessageSearchTests`:
    - `VectorRanking_MatchesInMemory` (same fixture vectors, same ranking);
    - `FullText_And_LikeFallback_ReturnSameSet` (FTS fixture vs. no-FTS fixture, same corpus);
    - `HostileQueries_Return200` (Review Focus 2: `"`, `*`, `NEAR`, `AND`, `(`, `~`, noise words only, CJK text);
    - `LikeWildcards_AreLiteral` (`100%`, `a_b`, `[x]`);
    - `PeerPerspectiveWindows_MatchInMemory`;
  - `FullTextIndexMaintenanceTests`:
    - `CreatesIndexOnce_UnderTwoReplicas`;
    - `NoFts_StaysOnLikeAndDoesNotThrow`;
    - `IsReady_OnlyAfterPopulation`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.DataLayer.SqlServer.Tests --filter "SqlM3|SqlMessageSearch|FullTextIndex"` (Docker). Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run it again. Expected: PASS. Run the M2 SQL suites too: the append path changed.
- [ ] **Step 5:** Commit `feat(sql): M3 summaries, message search and runtime full-text index`.

### Task 4: Chunker, summarizer, reconciler backfill

**Files:**
- Create:
  - `src/Nachos.Core/Memory/MessageChunker.cs`;
  - in `src/Nachos.Core/Summarizer/`: `SummarizerService.cs` (`IWorkHandler`, `TaskType = Summary`, `BatchPolicy = WorkBatchPolicy.Immediate`), `Prompts/summary-short.v1.md`, `Prompts/summary-long.v1.md`;
  - `src/Nachos.Core/Configuration/EmbeddingChunkOptions.cs`.
- Modify:
  - `src/Nachos.Core/NachosService.Messages.cs` (chunks and `SummaryIntent` into `MessageAppend`);
  - `src/Nachos.Core/Worker/ReconcilerService.cs` (embed pending chunks, backfill unchunked messages);
  - `src/Nachos.Hosting/` fake AI (answer the summarizer prompts) and handler registration.
- Tests: `test/Nachos.Core.Tests/{Memory/MessageChunkerTests,Summarizer/SummarizerTests,Worker/ReconcilerTests}.cs`.

**Depends on:** 2.

**Notes:**
- **`MessageChunker.Chunk(string content) → IReadOnlyList<string>`**:
  - chunks are at most `Embeddings:ChunkTokens` tokens (default 512), with a 20% overlap (spec §11.1), counted with the **active embedding model's** tokenizer. The tokenizer and its limit come from the embedding registration as `EmbeddingChunking(Tokenizer Tokenizer, int MaxTokens)`, not a hard-coded encoding. Azure OpenAI registers `cl100k_base` with 512; Task 11's local models register their own tokenizer and limit. The effective size is `min(ChunkTokens, MaxTokens)`;
  - content at or under the chunk size is one chunk;
  - blank content gives zero chunks.
- **Summarizer handler** (one leased unit = one session; the lease can hold a short item, a long item, or both):
  1. For each kind present, take the highest `through_seq` among its items.
  2. Load the prior summary of that kind and the messages after its `CoversThroughSeq` up to `through_seq`. Bound the input to `Summary:MaxInputTokens` (default 25,000) by keeping the newest messages.
  3. Make one plain-text `GetResponseAsync` call. Its `MaxOutputTokens` is the resolved `MaxTokensShort`/`MaxTokensLong` (1000/4000). Short and long run concurrently when both are present (spec §11.3).
  4. `SaveAsync` all results in one call, each with the last message's id and seq and the counted tokens. Sessions with `summary.enabled = false` at handling time complete without an LLM call. Core passes `SummaryIntent` only when summaries are enabled (null otherwise).
  5. Transient provider failures follow the M2 rule (release, no attempt consumed).
- **Prompts (clean-room, spec §11.3 only):** short = concise factual coverage; long = themes, context, apparent preferences and personality. Both are cumulative: prior summary plus new messages. Custom summary instructions go in the system prompt; message text is data.
- **Reconciler:** each tick, embed pending chunks (batched, like conclusions). Then backfill: `ListUnchunkedMessagesAsync(Reconciler:BackfillBatch = 500)`, chunk the messages, and `AddChunksAsync`. Metric: `nachos.messages.embedding_pending`.

- [ ] **Step 1:** Write the tests:
  - `MessageChunkerTests`: `ShortContent_OneChunk`, `LongContent_OverlapIsTwentyPercent`, `Blank_NoChunks`, `EveryChunkWithinTokenLimit` (seeded theory);
  - `SummarizerTests` (`ScriptedChatClient`, in-memory provider): `CumulativeFromPriorSummary`, `ShortAndLongBothDue_RunConcurrently`, `InputBoundedToNewest`, `DisabledAtHandleTime_CompletesWithoutLlmCall`, `LeaseLost_WritesNothing`, `TransientFailure_ReleasesWithoutAttempt`, `RecordsCoverageAndTokenCount`;
  - `ReconcilerTests`: `EmbedsPendingChunks`, `BackfillsChunksForUnchunkedMessages` (Review Focus 5), `BackfillIsIdempotent`;
  - `NachosServiceTests.CreateMessages_PassesChunksAndSummaryIntent`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Core.Tests --filter "Chunker|Summarizer|Reconciler"`. Expected: FAIL.
- [ ] **Step 3:** Implement. Get the prompts reviewed for clean-room compliance.
- [ ] **Step 4:** Run it again. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): message chunking, summarizer, chunk backfill`.

### Task 5: Hybrid search and visibility

**Files:**
- Create in `src/Nachos.Core/Search/`: `HybridSearchService.cs`, `LexicalQuery.cs`, `ReciprocalRankFusion.cs`.
- Modify `src/Nachos.Core/Memory/VisibilityPolicy.cs` (`RestrictMessages`), `INachosClient` and `NachosService` (`SearchWorkspaceAsync`, `SearchSessionAsync`, `SearchPeerAsync`), and add the wire contracts `MessageSearchOptions` and `WorkspaceMessageSearchOptions`.
- Tests: `test/Nachos.Core.Tests/Search/*`, `SearchVisibilityNegativeTests.cs`; extend `ArchitectureTests.MemoryReads_RequireVisibilityPolicy` to `IMessageSearch` and `ISummaryStore` reads.

**Depends on:** 2 (unit tests), 3 (SQL wiring).

**Notes:**
- **`LexicalQuery.Terms(string query) → IReadOnlyList<string>`**: Unicode word tokens (letters, digits, marks), lower-cased with `ToLowerInvariant`, deduplicated, and capped at 16 terms. A token longer than `IMessageSearch.MaxTermLength` (1,000) is dropped, because the store rejects it with `ArgumentException` (Task 3: SQL Server cannot bind a longer `LIKE` pattern). Everything else is dropped, so FTS operators can never reach the store. A query with no word token gives zero terms; lexical retrieval is then skipped and vector search still runs.
- **`HybridSearchService.SearchAsync(MessageSearchRequest, string query, int limit, VisibilityPolicy, ct)`**:
  - Vector and lexical retrieval run concurrently, each with `take = 4 × limit` (spec §11.5).
  - The query is embedded on the request path. Without an embedding generator, search is lexical only and logs a warning once; it is not a 500, unlike `C/query`, because search has a useful lexical half.
  - Results are fused with RRF, `score = Σ 1 / (60 + rank)`, deduplicated by message id, with ties broken by `CreatedAt` descending then `PublicId`. The top `limit` are returned.
- **Bounds:** `limit` 1–100, default 10 (spec §8). `query` is required. An empty or whitespace query returns `[]` (Q5). `W/search` with a `scope` returns `422 scope is not supported in this Nachos version` (fail closed until M6, like M2 Q3).
- **Visibility:** the scope (session or peer perspective) is a `VisibilityPolicy` input. `RestrictMessages` narrows the request's sessions to the allowlist, and an empty allowlist matches nothing (fail closed).

- [ ] **Step 1:** Write the tests:
  - `ReciprocalRankFusionTests`: `K60_KnownRanking`, `DuplicateAcrossLists_Summed`, `TieBreakIsDeterministic`;
  - `LexicalQueryTests`: `HostileQuery_IsTermsOnly` (Review Focus 2), `NoWordTokens_Empty`, `CapsAt16Terms`;
  - `HybridSearchTests`: `FusesBothLists`, `PendingEmbedding_StillFoundLexically` (Review Focus 5), `NoEmbeddingGenerator_LexicalOnly`, `LimitBounds_422`, `WorkspaceScope_422`;
  - `SearchVisibilityNegativeTests` (Review Focus 3): `SessionSearch_NeverReturnsOtherSession`, `PeerSearch_OutsideWindow_Excluded`, `EmptyAllowlist_FailsClosed`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Core.Tests --filter "Search|Rrf|Lexical"`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run it again. Expected: PASS, including the architecture test.
- [ ] **Step 5:** Amend spec §11.5 (the term sanitizer, the lexical-only fallback) and §11.8 (search is a consumer). Commit `feat(core): hybrid search with RRF and visibility`.

### Task 6: Session context with a hard budget, and summaries read

**Files:**
- Create in `src/Nachos.Core/Context/`: `SessionContextService.cs`, `SessionContextQuery.cs`, `ContextBudget.cs`.
- Modify `INachosClient` and `NachosService` (`GetSessionContextAsync`, `GetSessionSummariesAsync`), and add the wire contracts `SessionContext`, `SessionSummaries` and `Summary`, matching the manifest.
- Tests: `test/Nachos.Core.Tests/Context/{SessionContextServiceTests,SessionContextBudgetTests,SessionContextValidationTests}.cs`; summaries cases in `VisibilityNegativeTests`.

**Depends on:** 2, 4 (summaries exist to read). It reuses M2's `RepresentationBuilder` and `PeerContextService` unchanged.

**Notes:**
- **Query parameters** (spec §11.6, all from the manifest): `tokens`, `summary` (default true), `search_query`, `peer_target`, `peer_perspective` (requires `peer_target`), `scope` (requires `peer_target`; excludes `peer_perspective`; `422 scope is not supported in this Nachos version` until M6), repeated `sessions`, `limit_to_session`, `search_top_k`, `search_max_distance`, `include_most_frequent`, `max_conclusions`.
  - Invalid combinations are `422` with the query `loc`.
  - `tokens` is 1–100,000. Above 100,000 is `422`, matching Honcho's OpenAPI (`maximum: 100000`). The minimum of 1 is a Nachos rule; Honcho publishes none.
  - With no `tokens`, the budget is 100,000, i.e. "exhaustive within 100,000 tokens", as Honcho's OpenAPI describes (Q2).
  - Under a peer-scoped key, every session named in `sessions` must be one the peer is an active member of, else `401` (Honcho OpenAPI: "A peer-scoped key must be an active member of every session named").
- **Perspective:** representation and card are included only when `peer_target` is set. The observer is `peer_perspective ?? peer_target`; the observed peer is `peer_target`. `limit_to_session = true` makes this session the `VisibilityPolicy` allowlist, so there is no card. `sessions` adds to the allowlist.
- **Algorithm (`ContextBudget`, content tokens per D5):**
  1. When requested, build the representation (`max_conclusions`, `search_*`, `include_most_frequent`) and the card. If together they exceed 50% of `tokens`, drop the lowest-ranked representation items until they fit in 50%. If the card alone exceeds 50%, drop the card.
  2. From the remainder R, take the newest summary (long if it fits in 40% of R, else short if it fits, else none) when `summary = true`. Long is preferred because it covers more.
  3. Fill with messages after the summary's `CoversThroughSeq` (or all messages when there is no summary), newest first, each whole or not at all, until the next one would exceed the remainder. Then restore chronological order.
  4. Final check: count the assembled content. If it exceeds `tokens`, drop the oldest message and repeat. This is a guard; a correct steps 1–3 never trips it, and a test asserts it doesn't.
- **`GET S/summaries`** returns `{ id, short_summary?, long_summary? }`. Summaries are session-attributed, so they pass under an allowlist only when that allowlist includes this session.

- [ ] **Step 1:** Write the tests:
  - `SessionContextBudgetTests` (Review Focus 1): `NeverExceedsBudget` (seeded theory over 500 cases: random message sizes, summary sizes, representation sizes, `tokens` from 1 to 20,000), `SingleMessageLargerThanBudget_ReturnsNoMessages`, `RepresentationOver50Percent_Truncated`, `SummaryOver40Percent_Skipped`, `MessagesChronological`, `FinalGuardNeverTrips`;
  - `SessionContextServiceTests`: `NoPeerTarget_NoRepresentationOrCard`, `PeerPerspective_IsObserver`, `LimitToSession_OmitsCard`, `MessagesStartAfterSummaryCoverage`;
  - `SessionContextValidationTests`: `PerspectiveWithoutTarget_422`, `ScopeWithPerspective_422`, `Scope_422NotSupported`, `TokensZero_422`, `TokensOver100000_422`, `NoTokens_Uses100000`;
  - `VisibilityNegativeTests`: `Summaries_AllowlistWithoutSession_Hidden`, `Context_EmptyAllowlist_FailsClosed`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Core.Tests --filter Context`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run it again. Expected: PASS.
- [ ] **Step 5:** Amend spec §11.6 (D5, the long-before-short preference, the 1–100,000 bound) and §9.4 item 4. Commit `feat(core): token-budgeted session context and summaries`.

### Task 7: Deletion write barrier (SQL)

**Files:**
- Create `src/DataLayer/Nachos.DataLayer.SqlServer/Storage/WriteAdmission.cs` (the shared S-lock checks).
- Modify every SQL write path named in spec §13.1:
  - `SqlMessageStore` (append);
  - `SqlSessionStore` (update, reactivation, membership changes, get-or-create);
  - `SqlPeerStore` (get-or-create);
  - `SqlConclusionStore` (`InsertAsync`, `CommitDerivationAsync`);
  - `SqlSummaryStore.SaveAsync`.
- Create in `src/DataLayer/Nachos.DataLayer.SqlServer/Stores/`: `SqlDeletionStore.cs` (acceptance only in this task; the purge is Task 8).
- Add an internal test seam: `SqlServerOptions.BeforeLifecycleTransition` (in `Schema/SqlServerOptions.cs`) (`Func<CancellationToken, Task>?`, `internal`, reachable through `InternalsVisibleTo` from the SQL tests only).
- Tests: `test/Nachos.DataLayer.SqlServer.Tests/{SqlDeletionBarrierTests,SqlDeletionContractTests}.cs`.

**Depends on:** 2. Coordinate file ownership with Task 3 (both touch `SqlMessageStore`).

**Notes (spec §13.1, normative):**
- **Parent checks:** `SELECT LifecycleState FROM Workspaces WITH (REPEATABLEREAD) WHERE Id = @w` (S, held to commit), then the same on `Sessions` when the write is under a session. `Deleting` throws `ConflictException("<resource> is being deleted")`. The hint is never combined with `HOLDLOCK` or `READCOMMITTEDLOCK`.
- **Session-row writes** (update, reactivation) use `UPDATE … WHERE LifecycleState <> 2`; `@@ROWCOUNT = 0` on an existing row means the same `ConflictException`.
- **Acceptance:** exactly the steps in §13.1, with `BeforeLifecycleTransition` awaited between the predicate check and the transition.
- **Worker commits** (`CommitDerivationAsync`, `SaveAsync`) take the same S locks after their fencing check. Under `Deleting`, they discard the result and complete the items as `Succeeded` with `Error = 'resource_deleted'`.
- The lock order workspace → session → children is unchanged from M2. The existing `ParallelWorkers_NoDeadlock` test must stay green.

- [ ] **Step 1:** Write `SqlDeletionContractTests : DeletionContractTests` (acceptance and barrier only; the purge cases are Task 8's `DeletionPurgeContractTests`) and `SqlDeletionBarrierTests` (Review Focus 4; each test uses the pause hook):
  - `WorkspaceAcceptance_vs_SessionCreate_BothOrders`;
  - `SessionAcceptance_vs_Append`;
  - `SessionAcceptance_vs_Reactivation`;
  - `LateDerivationCommit_AfterAcceptance_Discarded`;
  - `LateSummarySave_AfterAcceptance_Discarded`;
  - `RepeatedDelete_SameJob`.

  Loop the race tests 20× and post the results with the SHA.
- [ ] **Step 2:** Run `dotnet test test/Nachos.DataLayer.SqlServer.Tests --filter "Deletion"` (Docker). Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run it again, then the whole SQL suite (every write path changed). Expected: PASS.
- [ ] **Step 5:** Commit `feat(sql): deletion write barrier and acceptance`.

### Task 8: Deletion jobs and `W/jobs`

**Files:**
- Create in `src/Nachos.Core/Deletion/`: `DeletionService.cs` (`IWorkHandler`, `TaskType = Deletion`, `BatchPolicy = Immediate`), `DeletionOptions.cs` (`Deletion:BatchSize` 500).
- Create `src/Nachos.Core/Jobs/JobService.cs`.
- Modify `SqlDeletionStore` (purge, cancel, remove, list jobs), `INachosClient` and `NachosService` (`DeleteSessionAsync`, `DeleteWorkspaceAsync`, `ListJobsAsync`). Add the `Job` wire contract (D6).
- Tests: `test/Nachos.Core.Tests/Deletion/DeletionServiceTests.cs`; extend `SqlDeletionBarrierTests`.

**Depends on:** 4 (summaries to purge), 7.

**Handler flow** (spec §13.1, "The deletion job"):
1. `CancelOtherWorkAsync`: delete pending non-deletion items for the target, mark pending child-session deletion items `Succeeded` (`superseded_by_workspace_deletion`), and bump the fencing token of every other active lease on the target's units. It excludes its own unit.
2. Loop `PurgeBatchAsync` until it returns `false`, renewing the lease between batches. Purge order is child-first:
   - session: `MessageEmbeddings` → `SessionSummaries` → the session's explicit conclusions → tombstone conclusions left unsupported (D7) → work items → `SessionPeers` → `Messages`;
   - workspace: each session's set, then `Conclusions` → `ConclusionSources` → `Collections` → `PeerCards` → `IdempotencyRecords` → `PrincipalGrants` for the workspace → `Peers`.
3. `RemoveAsync`. After that, the name is free.

Failures retry with M2 backoff on the same item. A dead-lettered deletion leaves the resource `Deleting` and shows in `W/jobs` with its error.

- [ ] **Step 1:** Write the tests:
  - `SqlDeletionPurgeContractTests : DeletionPurgeContractTests` (inherits Task 2's purge cases);
  - `DeletionServiceTests` (in-memory): `SessionDelete_PurgesEverythingAttributed`, `UnsupportedConclusion_Tombstoned`, `WorkspaceDelete_SupersedesChildJobs`, `OwnLeaseSurvivesCancel`, `DeadLettered_LeavesDeleting`;
  - SQL (`SqlDeletionBarrierTests`): `CrashAfterPartialPurge_ResumesAndCompletes` (kill the lease mid-loop and resume through a **new `SqlMemoryStore` instance** over the same database, so no in-process state survives; a conclusion citing one the first run removed must end tombstoned; writes stay rejected throughout), `OtherLeasesFenced_ByCancel`, `NameReusable_AfterRemove`;
  - `JobServiceTests`: `ListsDeletionJobsWithStatus`, `Paged`.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Amend spec §13 (D7; purge order as built) and §9.3 (D6). Commit `feat(core): resumable deletion jobs and job listing`.

### Task 9: API routes, auth matrix, client, conformance

**Files:**
- Create in `src/Nachos.Api/Endpoints/`: `SearchEndpoints.cs` (`W/search`, `S/search`, `P/search`), `SessionContextEndpoints.cs` (`S/context`, `S/summaries`), `JobEndpoints.cs`.
- Modify:
  - `WorkspaceEndpoints.cs` and `SessionEndpoints.cs` (`DELETE` → 202);
  - `NotImplementedEndpoints.cs`: remove exactly the M3 pairs: `DELETE W`, `DELETE S`, `POST W/search`, `POST S/search`, `POST P/search`, `GET S/context`, `GET S/summaries`, `GET W/jobs`;
  - `Auth/RouteRequirements.cs`;
  - `Json/NachosJsonContext.cs`.
- Move the M3 service methods onto `INachosClient` (Tasks 5 and 6 left them on `NachosService` only, because the Client tests count interface methods): `SearchWorkspaceAsync`, `SearchSessionAsync`, `SearchPeerAsync`, `GetSessionContextAsync`, `GetSessionSummariesAsync`. Update `RequestShapeTests`' method count.
- **Carried from Task 6 review:** the `S/context` route must enforce, with a test, that a peer-scoped key is an active member of this session **and of every session named in `sessions`**. Until M6 this is the only gate for peer-scoped keys, because `NachosService` passes an unrestricted access policy. Pin the status code (Nachos's remark says 401) against Honcho's public docs in Step 0. Bind an absent repeated `sessions` to null; an empty list is treated as not provided either way.
- Modify `src/Nachos.Client/NachosHttpClient.cs`: new methods. The search routes and `GET` routes retry, and so does `DELETE` (it is idempotent: a repeat returns the same job).
- Tests: `test/Nachos.Api.Tests/{SearchEndpointTests,SessionContextEndpointTests,DeletionEndpointTests,JobEndpointTests}.cs`, `WireCoverageTests`, `ScopeMatrixTests` rows, `test/Nachos.Client.Tests` round-trips. Add conformance scenarios for context, search and session deletion to the SDK smoke.

**Depends on:** 5, 6, 8.

**Auth (mirror Honcho, as in M2 Q7).**
- **Step 0** records, per route, the public-doc evidence and the resulting `RouteAccess` in the PR.
- **Documented** (Honcho platform reference): a peer-scoped key gets read-only access to sessions its peer is an active member of, covering context, summaries and search. So `S/context`, `S/summaries` and `S/search` join the member-read allowlist on evidence, not as a fallback. A session-scoped key never reaches peer routes. `scope` on `S/context` requires a workspace or admin key.
- Where the evidence is silent, fall back to:
  - `W/search`, `W/jobs`, `DELETE W`, `DELETE S` → workspace;
  - `P/search` → peer with `p == peer_id`;
  - `S/search`, `S/context`, `S/summaries` → session, plus member-read (documented, above; the allowlist test keeps it free of mutating routes).
- `S/context` with `peer_target` under a peer-scoped key additionally requires `peer_perspective ?? peer_target == p`, so a member cannot read another peer's representation through context.

- [ ] **Step 0:** Collect the evidence and write the `ScopeMatrixTests` rows from it.
- [ ] **Step 1:** Write the endpoint tests:
  - status codes exactly as in `honcho-v3-wire.json` (202 for both deletes, plus 409 for active sessions and for writes under `Deleting`);
  - the 422 bounds (`limit` 1–100, `tokens` ≥ 1, the §11.6 combinations, `scope`);
  - the 401 matrix rows;
  - the 501 list shrinking by exactly the M3 pairs;
  - the OpenAPI wire diff;
  - `Context_PeerKey_OtherPerspective_401`, `Context_PeerKey_NonMemberSessionsParam_401`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Api.Tests test/Nachos.Client.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run them, then the SDK smoke (`eng/ci/Invoke-SdkSmoke.ps1`). Expected: PASS.
- [ ] **Step 5:** Commit `feat(api): search, session context, summaries, deletion and jobs routes`.

### Task 10: Search benchmark (R1), AppHost smoke, docs

**Files:**
- Create `test/Nachos.Benchmarks/` (console app, not in the default test run): it seeds 1,000,000 messages in one workspace (unit-norm random 1536-d chunk vectors, 1–2 chunks each, lorem-style text) into the FTS fixture, then measures `W/search` and `S/search` p50/p95 over 200 queries at `limit = 10`.
- Create `research/m3-search-benchmark.md` with the host spec, the numbers, and the decision.
- Modify `test/Nachos.AppHost.Tests`: `AppHostSmokeTests.MessagesBecomeSearchableAndSummarized_WithFakeAI`.
- Docs:
  - README "What works today" and the 501 table;
  - concept pages "Search" and "Session context" (including the budget rule, D5);
  - guide "Delete sessions and workspaces" (202 semantics, `W/jobs`, dead-letter redrive by SQL);
  - generated options reference (`Embeddings:ChunkTokens`, `Summary`, `SessionContext`, `Deletion`, `Search`).

**Depends on:** 3, 9.

**Benchmark decision rule:**
- Also measure one reconciler tick's `ListUnchunkedMessagesAsync` at 1M messages (Task 3 review I4). If it is slow, add a reconciler `afterId` cursor. Measure session-scoped vector search seeks on `IX_MessageEmbeddings_Workspace_Session` and `CONTAINSTABLE` cross-workspace candidate volume (Task 3 review M7).
- If `W/search` p95 is at most 1 s on the dev host, keep exact search unchanged.
- Otherwise, add `Search:MaxVectorCandidates` (newest-first chunk cap before `VECTOR_DISTANCE`, default chosen from the measurements). Document the recall trade-off, and record that ANN (M7) is the real fix.

Either way the result is posted on the PR. It closes R1's M3 obligation.

- [ ] **Step 1:** Write the AppHost smoke test.
- [ ] **Step 2:** Run it. Expected: FAIL.
- [ ] **Step 3:** Implement the docs and AppHost changes. Run the benchmark and record the results.
- [ ] **Step 4:** Run the AppHost suite (Docker) and the docs build. Expected: PASS, with zero broken links.
- [ ] **Step 5:** Commit `docs(m3): search, context and deletion docs; R1 benchmark`.

### Task 11: Local embeddings option

Run Nachos with **no paid embedding service**: a small sentence-embedding model runs in-process on CPU through ONNX Runtime. This is an option alongside Azure OpenAI, not a replacement. Chat (the Deriver and Summarizer) still needs a chat model, or the fake AI; a local small chat model is a future follow-up (Q8), not M3.

**Files:**
- Create in `src/Nachos.Hosting/LocalEmbeddings/`:
  - `LocalEmbeddingGenerator.cs`: `IEmbeddingGenerator<string, Embedding<float>>`, sealed, `IDisposable` (it owns the `InferenceSession`);
  - `LocalEmbeddingModel.cs`: the catalog of supported models (below);
  - `LocalEmbeddingOptions.cs` (`Embeddings:Local:Model`, `Embeddings:Local:ModelDirectory`);
  - `EmbeddingVectors.cs`: pure `Pool`, `Normalize` and `PadTo(VectorSpace.Dimensions)` helpers.
- Modify the `NachosBuilder` extensions in `src/Nachos.Hosting/`: add `UseLocalEmbeddings(Action<LocalEmbeddingOptions>? configure = null)`. Configuration also selects it with `Nachos:Ai:Embeddings=Local`.
- Create `src/Nachos.Cli/ModelCommands.cs`: `nachos models fetch <model> [--dir <path>]` downloads the pinned files over HTTPS and verifies each SHA-256. It refuses to overwrite a file whose hash differs.
- Schema: create `Tables/EmbeddingSpace.sql`, a single-row table `(Id tinyint PK CHECK (Id = 1), Model nvarchar(128), Dimensions int, StampedAt)`. It is additive and AutoSafe.
- Create `src/Nachos.Core/Llm/EmbeddingSpaceCheck.cs`, an `IHostedService` that replaces M2's dimension-only probe; see "One model per database" below. Add `nachos embeddings reset --model <id>` to the CLI.
- Add `Microsoft.ML.OnnxRuntime` (MIT) to `Directory.Packages.props`, and record the licence check in the PR.
- Tests: `test/Nachos.Core.Tests/Llm/{EmbeddingVectorsTests,EmbeddingSpaceCheckTests}.cs`, `test/Nachos.Hosting.Tests/LocalEmbeddingGeneratorTests.cs` (real model; see Step 4), `test/Nachos.Cli.Tests/ModelCommandsTests.cs`.

**Depends on:** 3 (schema and SQL stores), 4 (the chunker takes its tokenizer from the active embedding model).

**Model catalog** (pinned; nothing is bundled in the repo or any package):

| Id | Licence | Native dims | Max tokens | Pooling | Default |
|---|---|---|---|---|---|
| `bge-small-en-v1.5` | MIT | 384 | 512 | CLS, then L2-normalize | yes |
| `all-minilm-l6-v2` | Apache-2.0 | 384 | 256 | mean over the attention mask, then L2-normalize | no |

Each entry pins the `model.onnx` and `vocab.txt` download URLs and their SHA-256, plus a `DedupMaxDistance` default (see the calibration below). Tokenization uses `Microsoft.ML.Tokenizers` `BertTokenizer` over the pinned `vocab.txt`, with the model's max length.

**Notes:**
- **Dimensions:** native vectors are zero-padded to 1536 after normalization. Zero padding leaves every dot product and norm unchanged, so cosine distances are exactly the native ones. The `VECTOR(1536)` columns and every query stay as they are. The cost is about four times the storage per vector.
- **One model per database (makes mixing impossible):** vectors from different models must never be compared, and the model name lives only in row JSON today. So the database records one embedding model in `EmbeddingSpace`.
  - At start, `EmbeddingSpaceCheck` compares the configured generator's model id with the stamp. On a mismatch, startup fails with `Embedding model '<configured>' does not match the database's '<stamped>'. Run 'nachos embeddings reset --model <configured>' to re-embed, or configure '<stamped>'.`
  - An unstamped database is stamped on first start when every stored vector's recorded model (row metadata) equals the configured one, or when there are no vectors. Otherwise startup fails with the same message.
  - `embeddings reset` clears every conclusion vector to `Pending` and deletes every message chunk (chunks were cut for the previous model's tokenizer and limit), in batches, then restamps. The reconciler re-embeds the conclusions and re-chunks the messages through `ListUnchunkedMessagesAsync`. Until it has, those rows are missing from vector results; lexical search still finds messages.
  - The in-memory provider holds the stamp for the process lifetime.
  - The fake AI's model id is `fake-hash-v1`.
- **Dedup threshold per model:** the 0.05 default was chosen on OpenAI's distance scale. When the deployment doesn't set `Deriver:DedupMaxDistance`, the catalog entry's value applies. Step 3 calibrates it on a fixture of paraphrase and non-paraphrase pairs (committed as `test/fixtures/dedup-pairs.json`, written fresh for this task) and records the chosen value and the false-merge rate in `research/local-embeddings-calibration.md`.
- **Chunking (Task 4 seam):** with local embeddings, `MessageChunker` uses the model's `BertTokenizer` and its max tokens less 2 (`[CLS]`/`[SEP]`). That is 510 for bge-small and 254 for MiniLM, with the same 20% overlap. Chunks must be cut from the **original** message text, not the `BertTokenizer`'s normalized (lower-cased, accent-stripped) form: encode with `considerNormalization: false` or map token offsets back to the original, and ignore the zero-width `[CLS]`/`[SEP]` offsets. (Task 4 review M6.)
- **Performance:** the session is created once and is thread-safe for `Run`. Inputs are batched (up to `Embeddings:Local:BatchSize`, default 32), and `CancellationToken` is checked between batches.

- [ ] **Step 1:** Write the tests:
  - `EmbeddingVectorsTests`: `ZeroPadding_PreservesCosineDistance` (seeded theory), `ClsPooling_TakesFirstToken`, `MeanPooling_IgnoresMaskedTokens`, `Normalize_UnitLength`;
  - `EmbeddingSpaceCheckTests`: `Mismatch_FailsWithResetCommand`, `Unstamped_NoVectors_Stamps`, `Unstamped_MatchingRecordedModel_Stamps`, `Unstamped_OtherRecordedModel_Fails`, `Reset_MarksAllPendingAndRestamps`;
  - `LocalEmbeddingGeneratorTests` (real model): `Output1536_UnitNorm_PaddingZero`, `Paraphrase_CloserThanUnrelated`, `LongInput_TruncatedNotThrown`, `Deterministic_AcrossCalls`;
  - `ModelCommandsTests`: `HashMismatch_FailsAndKeepsNothing`, `ExistingGoodFile_Skipped` (uses a local HTTP fixture, not the internet);
  - `MessageChunkerTests.LocalModel_ChunksWithinModelMaxTokens`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Core.Tests --filter "EmbeddingVectors|EmbeddingSpace"` and `test/Nachos.Cli.Tests --filter ModelCommands`. Expected: FAIL.
- [ ] **Step 3:** Implement. Run the dedup calibration and record it.
- [ ] **Step 4:** Run them again. Expected: PASS. Run the real-model tests with `NACHOS_LOCAL_MODEL_DIR` set; they skip, with a reason, when it is unset. Add a CI job step that runs `nachos models fetch bge-small-en-v1.5` (cached by hash) and then those tests. It is a public download, not an Azure action.
- [ ] **Step 5:** Docs: a guide "Run Nachos with local embeddings", covering fetching the model, `UseLocalEmbeddings()` or configuration, the one-model-per-database rule and `embeddings reset`, and the quality and storage trade-off. Also add an AppHost switch `Nachos__Ai__Embeddings=Local`. Commit `feat(hosting): local ONNX embeddings with a per-database embedding model`.

---

## M3 Done checklist

The M3 PR merges when all of these hold:

- [ ] Tasks 1–11 are committed with their tests.
- [ ] CI is green at the final SHA: build-test (both OSes), SQL/CLI/AppHost (Docker, FTS image), schema (both dacpacs; the reviewed M2→M3 upgrade adds the tables), docs, and SDK smoke.
- [ ] The §13.1 race tests have been looped 20× on SQL, with results posted against the SHA.
- [ ] Budget property tests are green (spec §20 M3 exit).
- [ ] Context and search conformance scenarios pass against the upstream SDKs (spec §20 M3 exit).
- [ ] Visibility negative tests are green for search, context and summaries, and the architecture test covers the new stores.
- [ ] Locally (Aspire, fake AI) **and** with the README's in-memory quick start: posted messages become searchable, a summary appears after 20 messages, and `S/context` returns within budget, with no manual steps.
- [ ] The R1 benchmark result and decision are recorded.
- [ ] The spec is amended for D1–D8 and for each answered owner question.
- [ ] The README and docs pages match what shipped.

## Owner actions requested (not performed by agents)

1. **Answer or accept the defaults for the questions below.**
2. **Still open from M2:** the M2 Azure smoke run (PR #33). M3 does not depend on it, but a real-model run is the only way to judge conclusion and summary quality before M4 builds on them.
3. **(Optional) A live summarizer spot-check** through the `azure-live` environment once Task 4 lands. The eval harness itself is M4.

## Owner questions

Each row has a proposed default. The default applies unless the owner answers otherwise.

| # | Question | Proposed default | Blocks |
|---|---|---|---|
| Q1 | The full-text index can't live in the dacpac without breaking deployment to servers that lack FTS (and the spec's own `LIKE` fallback). Create it at runtime instead (D1)? | **Yes:** runtime creation behind readiness, like the spec's ANN pattern. The dacpac stays FTS-free. | Tasks 1, 3 |
| Q2 | What should `S/context` use when `tokens` is absent? | **Changed after research (2026-11-08):** 100,000, with `422` above it. Honcho's public OpenAPI (3.6.0) says `maximum: 100000` and "If not provided, the context will be exhaustive (within 100000 tokens)". The earlier 8,000 proposal had no evidence behind it. | Task 6 |
| Q3 | Route access for search, context and summaries? | **Yes, mirror Honcho.** Research found the member-read access for context, summaries and search is documented in the platform reference. Workspace fallback remains only for `W/search`, `W/jobs` and both deletes. | Task 9 |
| Q4 | Issue #10 (exact managed tokenizer) is a release gate, but it needs a specific approval of the Unicode-data policy exception. Keep it out of M3? | **Yes, keep it separate.** M3's budget guarantee holds with the current counter (D8). | — |
| Q5 | An empty or whitespace search `query`: `200 []` or `422`? | **`200 []`**. The manifest requires the field but sets no minimum length. | Task 5 |
| Q7 | Honcho's live OpenAPI is 3.6.0; the pinned manifest is 3.3.0 (spec R4). `DELETE W` now documents `409`. Re-pin before Task 9's wire diff? | **Yes, as its own deliberate commit before Task 9**, regenerating `test/contracts/honcho-v3-wire.json` and reviewing the diff. Re-pinning is a deliberate owner-visible change (R4). | Task 9 |
| Q8 | A local **chat** model so Nachos runs entirely free (Phi, Qwen or Gemma class, in-process via ONNX Runtime GenAI or through a local OpenAI-compatible server such as Ollama or Foundry Local)? | **Not in M3** (owner, 2026-11-09); a follow-up issue opened with the M3 PR. Notes for it: (1) the Deriver depends on schema-valid structured output (`GetResponseAsync<ExtractionResult>`), so the runtime must constrain decoding to the JSON schema, and the eval harness (M4) must show acceptable fact precision before it is recommended; (2) licences: Phi-4-mini is MIT and most Qwen2.5/Qwen3 sizes are Apache-2.0, both permissive; Gemma ships under Google's Gemma Terms of Use, which is not on the permissive allowlist (spec §3 rule 7), so Gemma could only be user-supplied, never fetched or recommended by Nachos; (3) the summarizer is plain text and is the easier first target. | — |
| Q6 | Message deletion (`DELETE M/{id}`) isn't in Honcho v3's route list, so M3 doesn't add it. Agreed? | **Agreed.** No new route. | — |
