# Nachos M2 — Memory Formation Implementation Plan

> **For agentic workers:** execute each task with the `implement-task-next` skill (task slug `issue-2-m2-<name>`). Steps use checkbox (`- [ ]`) syntax for tracking. Do not start a task until every task on its **Depends on** line is merged on the milestone branch (`nachos-m2`). Resolve the [open questions](#open-questions-for-the-owner) that a task names before starting that task.

**Goal:** Messages become memory. A message append enqueues derivation work in the same transaction. A worker claims that work under a fenced lease, asks an LLM for explicit facts, and stores them as deduplicated, corroborated, embedded conclusions per observer. The conclusions, peer-card, representation, peer-context and queue-status routes stop returning `501`, and every one of their reads goes through one `VisibilityPolicy`.

**Architecture:**
- `Nachos.Abstractions` (BCL only) gains the M2 domain records, the store interfaces (`IConclusionStore`, `IPeerCardStore`, `IWorkQueue`, `IEmbeddingStore`) and the pure observer rule.
- `Nachos.Core` gains the LLM layer (`Microsoft.Extensions.AI`), the Deriver, dedup, `RepresentationBuilder`, `PeerContextService`, `VisibilityPolicy`, and the queue consumer and reconciler loops.
- Both providers implement the new stores. The SQL provider adds tables to the existing dacpac.
- A new `Nachos.Worker` host runs the loops. `Nachos.Hosting.AddNachosWorker()` runs them in-process.
- `Nachos.Api` maps the M2 routes.

**Tech Stack:** M1's stack, plus:
- `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>`, `ChatClientBuilder`, the `GetResponseAsync<T>` structured-output extension);
- `Microsoft.Extensions.AI.OpenAI` with `Azure.AI.OpenAI` for Azure OpenAI;
- the SQL Server 2025 / Azure SQL `VECTOR(n)` type with `VECTOR_DISTANCE`, through `Microsoft.Data.SqlClient` `SqlVector<float>` (6.1+; the repo pins 6.1.7);
- `Microsoft.NET.Sdk.Worker` and Aspire `AddProject` for the worker.

Microsoft Agent Framework is **not** needed in M2 (see Divergence D6).

**Spec:** [`docs/superpowers/specs/2026-10-07-nachos-design.md`](../specs/2026-10-07-nachos-design.md): §4.2–4.3, §7, §8, §9.3 (M2 rows), §10, §11.1–11.4, §11.8, §17, §19, §20 (M2), §21 (R5, R7). Roadmap: [`2026-10-07-nachos-roadmap.md`](2026-10-07-nachos-roadmap.md). Tracking issue: #2.

## Global Constraints

These carry over from M1 and the roadmap:

- **Clean-room (spec §3):** never read Honcho `src/`, `sdks/` or `mcp/`, or Honcho's prompt text. The Deriver prompt is written from the behavioral requirements in spec §11.2 alone. Wire names and default values may match.
- **Licenses (spec §3 rule 7):** distributed dependencies are checked by review when they are added. There is no license tooling. The new packages (`Microsoft.Extensions.AI*`, `Azure.AI.OpenAI`) are MIT.
- **Schema (spec §7.3, roadmap rule 1):** all DDL lives in `src/DataLayer/Nachos.DataLayer.SqlServer.Database/`. Every M2 change is additive and must classify `AutoSafe`. EF Core never creates migrations. `AllowIncompatiblePlatform` is never set.
- **No stubs as features (roadmap rule 3):** a route stays `501` until its task lands. M3+ routes stay `501`.
- **Interfaces flow forward (roadmap rule 2):** existing `IMessageStore.AppendAsync` callers keep working. New parameters are added in a compatible way (see Task 3).
- **Azure consent (spec §18.3):** no agent runs `azd`, `az deployment`, live Azure OpenAI calls, or registry pushes without a per-run, untagged owner approval. Live LLM tests are `workflow_dispatch`-only in the `azure-live` environment.
- **Coding standards:** the owner's .NET standards (`~/.claude/CLAUDE.md`) apply. Seal classes, use one constructor and constructor injection, pass `CancellationToken` all the way down, return `IReadOnlyList<T>`, and add no speculative abstraction. Comments only for a non-obvious *why*.
- **SDK/CPM/Nullable/TreatWarningsAsErrors:** unchanged from M1.

## Ownership and sequencing

The tasks are agent-neutral. Optionally, put an owner's name next to each task in the PR's coordination table. One rule still holds from M1: **SQL/Testcontainers-heavy tasks (1, 3, and 6's SQL half) need a Docker-capable host.**

```
1 Schema ─┬─> 3 SQL stores + enqueue ──┐
2 Abstractions + in-memory ─┴─> 3      ├─> 6 Worker loops ─> 9 Hosting/Aspire/infra
4 LLM layer ─> 5 Deriver ──────────────┘
2 ─> 7 Visibility + read services ─> 8 API routes
```

- Tasks 1, 2 and 4 can start immediately and in parallel.
- Task 5 needs only 2 and 4. It uses the in-memory provider.
- Task 8 can land route by route as its services do.

## Review Focus

These are the high-risk behaviors. Each is pinned by a named test.

1. **Enqueue is atomic with the append.** A message batch that commits always has its work items, and a rolled-back batch (serializer throw, idempotency duplicate) has none. Pinned by `StoreContractTests.Append_EnqueuesRepresentationWork_Atomically` (both providers).
2. **A unit has one owner at a time.** Under RCSI ON, with N concurrent claimers and forced lease expiry, a work unit is never held by two owners, and a stale fencing token can never write. Pinned by `SqlWorkQueueTests.ConcurrentClaimers_NeverShareAUnit` (looped 20×) and `.StaleFencingToken_WriteRejected`.
3. **`times_derived` does not inflate on retries.** Re-running the same batch after a crash that hit after the write but before `Complete` leaves `TimesDerived` unchanged. Pinned by `DeriverTests.RetriedBatch_DoesNotInflateTimesDerived`.
4. **Explicit facts never dedup across sessions.** Pinned by `ConclusionDeduplicatorTests.ExplicitFact_OtherSession_IsNotDuplicate`.
5. **No M2 read path bypasses `VisibilityPolicy`.** Pinned by `VisibilityNegativeTests`, one per consumer (representation, card, context, conclusion list/query/get), plus an architecture test: in `Nachos.Core`, only `VisibilityPolicy`-taking methods may call `IConclusionStore`/`IPeerCardStore` read members.
6. **Observer resolution is identical at enqueue and at derive time.** Pinned by `ObserverRuleTests`, a table of peer and session-peer `observe_me`/`observe_others` combinations and membership windows that both providers and the Deriver use.

---

## Spec-vs-code divergences (resolved by this plan)

The plan builds on the code on `main`, not on the spec's idealized interfaces. Each divergence below gets a spec amendment in the task that resolves it.

| # | Spec says | Code on `main` | Plan |
|---|---|---|---|
| D1 | `IWorkQueue.EnqueueAsync` "joins the ambient transaction" (§7.1); `MessageIngestionService` runs enqueue in one transaction (§10.2). | No unit of work and no ambient transaction. Each store method opens its own connection and transaction (`SqlMessageStore.AppendAsync`). | Enqueue happens **inside `IMessageStore.AppendAsync`**. Core passes a per-message derive decision; the provider resolves observers with the shared pure rule (Abstractions) inside the same transaction and inserts the `WorkItems`. No ambient-transaction machinery. |
| D2 | Queue claim, seq allocation and status live in stored procedures (`AcquireWorkUnit`, `AllocateMessageSeq`, `GetQueueStatus`, `GetDueWorkCount`). | No stored procedures. Seq allocation is inline parameterized SQL. | Keep inline parameterized SQL in `SqlWorkQueue`, matching the code. `GetDueWorkCount` is added only when KEDA (R7) is verified (Task 9, owner-gated). |
| D3 | The schema version stays `1` until the first release (§7.3). | `SchemaGate` only deploys when the database is behind `SchemaInfo.CurrentVersion`, so an M1 database stamped `1` would **never receive the M2 tables**. | **Owner (Q1): keep version `1` until the first release.** Pre-release databases are recreated, or taken through `schema report` + `schema upgrade --approve-reviewed`. |
| D4 | Message configuration's `reasoning` and the resolver drive derivation (§8). | `NachosService.CreateMessagesResponseAsync` validates configuration but never calls `IConfigurationResolver`. `NewMessage` has no configuration. | Task 3 resolves `reasoning.enabled` per message (message > session > workspace > global) and passes the result to `AppendAsync`. |
| D5 | `PeerCards.Entries json`, `WorkItems.Payload json` (§7.2 table). | All JSON is `nvarchar(max)` with an `ISJSON` CHECK (§7.2 prose, and every M1 table). | Use `nvarchar(max)` plus `ISJSON(…, ARRAY/OBJECT)`, like M1. |
| D6 | Deriver is an Agent Framework `RunAsync<ExtractionResult>` (§4.2, §11.1). | No AI packages. | The Deriver is one tool-less structured call, so it uses `IChatClient.GetResponseAsync<ExtractionResult>` (M.E.AI) directly. Agent Framework (`ChatClientAgent`, `AIFunction` tool loops) comes in with M4 dialectic, where it adds value. Agent Framework's `RunAsync<T>` sits on the same M.E.AI abstraction, so nothing is lost ([Agent Framework structured outputs](https://learn.microsoft.com/en-us/agent-framework/agents/structured-outputs)). |
| D7 | Store/queue/index interfaces live in Abstractions (§7.1). | `Nachos.Architecture.Tests.Abstractions_ReferencesOnlyBcl` forbids non-BCL references. | Interfaces carry vectors as `ReadOnlyMemory<float>`. M.E.AI types appear only in Core and Hosting. |
| D8 | In-memory queue is a `Channel` (§7.5). | — | A lock-guarded in-memory table with the same lease/fencing semantics. A `Channel` adds nothing when consumers poll the same contract. |
| D9 | `PeerRepresentationGet` inputs are `max_conclusions`, `search_top_k`, `search_max_distance` and session allowlists (§11.4). | The wire manifest also has `filters`, `scope`, `session_id` and `target`. | All are accepted. `filters` uses the conclusion filter allowlist. `scope` is Q3. |
| D10 | `ConclusionCreate.content` has no spec bound. | Manifest: `content` 1–65,535; batch 1–100. | Enforce the manifest bounds. |
| D11 | Bicep provisions an Azure OpenAI account and a worker app (§18.2). | `main.bicep` has only an `openAiEndpoint` parameter, and no worker app. | Task 9 adds an offline-validated `worker-app.bicep` and model deployment parameters. AOAI account provisioning is Q6. |
| D12 | `DeriverOptions`: eligibility 512 / batch 1024 / max age 30 min / max input 25,000 (§8). | `DeriverOptions` has only `MaxCustomInstructionsTokens`. | Task 5 adds the parity defaults. |

---

## Tasks

### Task 1: M2 schema (additive; schema version stays 1)

**Files:**
- Create in `src/DataLayer/Nachos.DataLayer.SqlServer.Database/Tables/`: `Collections.sql`, `Conclusions.sql`, `ConclusionSources.sql`, `ConclusionCorroborations.sql`, `PeerCards.sql`, `MessageEmbeddings.sql`, `WorkItems.sql`, `WorkUnitLeases.sql`.
- No version change: `SchemaInfo.CurrentVersion` stays `1` (Q1). Existing pre-release databases are recreated or take the reviewed upgrade path.
- Tests: `test/Nachos.DataLayer.SqlServer.Tests/Schema/M2SchemaTests.cs`.

**Depends on:** none (Q1 answered).

**Tables** (conventions as in M1: `bigint IDENTITY` clustered PK, explicit `NONCLUSTERED` uniques, composite `WorkspaceId` FKs, `BIN2_UTF8` on key text, application-clock `datetimeoffset(7)`, JSON as `nvarchar(max)` + `ISJSON` CHECK in stored form):
- `Collections(Id, WorkspaceId, ObserverPeerId, ObservedPeerId, NewExplicitCount int DEFAULT 0, LastActivityAt NULL, LastDreamAt NULL, CreatedAt)`, `UQ (WorkspaceId, ObserverPeerId, ObservedPeerId)`, FKs to `Peers (WorkspaceId, Id)`. The three dream columns are the **M5 seam**: the Deriver maintains them in M2 and nothing reads them yet.
- `Conclusions(Id, WorkspaceId, PublicId nvarchar(32), CollectionId, SessionId NULL, Level tinyint, Content nvarchar(max), Embedding VECTOR(1536) NULL, EmbeddingState tinyint, TimesDerived int DEFAULT 1, PatternType NULL, Confidence NULL, InternalMetadata, CreatedAt, DeletedAt NULL)`.
  - `UQ PublicId`; `IX (CollectionId, DeletedAt, CreatedAt)`; `IX (EmbeddingState) WHERE EmbeddingState = 0`.
  - The level CHECK uses stored form.
  - `InternalMetadata` holds `source_message_ids` and the prompt id/version (§11.11).
- `ConclusionSources(DerivedId, Position, SourceId)`, PK `(DerivedId, Position)`. There is deliberately no FK on `SourceId` (spec §7.2). Empty in M2 except for API-created rows; it is the M5 seam.
- `ConclusionCorroborations(ConclusionId, SourceBatchKey char(64), CreatedAt)`, `UQ (ConclusionId, SourceBatchKey)`.
- `PeerCards(Id, WorkspaceId, ObserverPeerId, ObservedPeerId, Entries nvarchar(max) CHECK ISJSON ARRAY, UpdatedAt, UpdatedBy tinyint)`, `UQ (WorkspaceId, ObserverPeerId, ObservedPeerId)`.
- `MessageEmbeddings(Id, WorkspaceId, MessageId, ChunkIndex, Content, Embedding VECTOR(1536) NULL, SyncState tinyint, Attempts int, AvailableAt)`, `UQ (MessageId, ChunkIndex)`, `IX (SyncState, AvailableAt) WHERE SyncState = 0`.
- `WorkItems(Id, WorkspaceId NULL, TaskType tinyint, WorkUnitKey nvarchar(900) BIN2_UTF8, Payload nvarchar(max) CHECK ISJSON OBJECT, Status tinyint, Attempts int, AvailableAt, TokenCount int, MessageId NULL, SessionId NULL, Error nvarchar(4000) NULL, CreatedAt, CompletedAt NULL)`.
  - `IX (Status, WorkUnitKey, Id) INCLUDE (AvailableAt, TokenCount, CreatedAt)`; `IX (WorkspaceId, SessionId, Status)` for queue status.
  - `TaskType` values: `1 representation`, `2 summary` (M3), `3 dream` (M5), `4 webhook` (M6), `5 deletion` (M3), `6 reconciler`, `7 scope_backfill`, `8 scope_removal` (M6). The values are reserved now so later milestones need no CHECK change.
- `WorkUnitLeases(WorkUnitKey nvarchar(900) BIN2_UTF8 PK CLUSTERED, Owner nvarchar(128), ExpiresAt, FencingToken bigint)`.
  - This is the one non-`bigint` clustered key. It is deliberate: the key is the lease identity, and 900 bytes fits the 900-byte clustered limit (450 UTF-16 code units; work-unit keys are built from names of at most 512 characters, so the key builder hashes over-long components, see Task 2).

The vector dimension is fixed at **1536** (Q2). Startup validates the configured embedding dimension against the column (Task 4).

- [ ] **Step 1:** Write `M2SchemaTests`:
  - `M2Tables_DeployToEmptyDatabase`, and `M1Database_ReviewedUpgrade_AddsM2Tables` (deploy the M1 dacpac, then `schema upgrade --approve-reviewed` with this one; the report classifies the change as additive);
  - `AllIndexKeys_WithinSqlLimits` (extended to the new tables);
  - `VectorColumns_Dimension1536_OnBothDacpacs`;
  - `WorkItemsTaskType_RejectsUnknownValue`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.DataLayer.SqlServer.Tests --filter M2Schema`. Expected: FAIL.
- [ ] **Step 3:** Write the DDL. Build both `.sqlproj` projects. **Spike first:** confirm that `Microsoft.Build.Sql` 2.2.0 / DacFx 170.4.83 build `VECTOR(1536)` for both `SqlAzureV12` and `Sql170`. Microsoft's release notes say VECTOR is supported for SQL project build on Azure SQL and SQL Server 2025 ([SqlPackage release notes](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/release-notes-sqlpackage)). If a pinned version fails, bump DacFx (owner Microsoft-library policy, spec §3) and record it in the PR.
- [ ] **Step 4:** Run it again. Expected: PASS. Also run the full `SchemaDeployerTests`.
- [ ] **Step 5:** Amend spec §7.2 (D5, the 1536 decision). Commit `feat(schema): M2 memory and queue tables`.

### Task 2: Abstractions and in-memory provider

**Files:**
- Create in `src/Nachos.Abstractions/`:
  - `Domain/{ConclusionRecord,ConclusionLevel,NewConclusion,PeerCardRecord,WorkItemRecord,WorkTaskType,WorkUnit,WorkLease,QueueStatusRecord,EmbeddingChunk,DeriveIntent}.cs`;
  - `Stores/{IConclusionStore,IPeerCardStore,IWorkQueue,IEmbeddingStore}.cs`;
  - `Observation/ObserverRule.cs`;
  - `Work/WorkUnitKeys.cs`.
- Modify: `Stores/IMemoryStore.cs` (four new members), `Filtering/ResourceFields.cs` (`ResourceKind.Conclusion`).
- Create in `src/DataLayer/Nachos.DataLayer.InMemory/`: the in-memory implementations under `Stores/`.
- Extend `test/Nachos.Testing/StoreContractTests.cs` and add `WorkQueueContractTests.cs`.

**Depends on:** none.

**Interfaces** (all workspace-scoped by name, like M1; vectors as `ReadOnlyMemory<float>`):
- `ObserverRule.Resolve(PeerObservation sender, IReadOnlyList<MemberObservation> members, DateTimeOffset at) → IReadOnlyList<string>` (observer peer names, ordinal-sorted). The rule:
  - the sender observes itself unless peer-level or session-level `observe_me` is `false`;
  - another member observes the sender when that member is active at `at` (`JoinedAt <= at < LeftAt`), its session-level `observe_others` is `true`, and the sender's `observe_me` is not `false`.

  Defaults: `observe_me` true and `observe_others` false (Honcho public docs). Pure, BCL-only, shared by both providers and the Deriver.
- `WorkUnitKeys.Representation(workspace, session, observed)` builds the spec §10.1 key. A component longer than 128 characters is replaced by `~` plus its SHA-256 prefix, so the key always fits `nvarchar(900)`.
- `IWorkQueue`:
  - `TryAcquireAsync(owner, IReadOnlySet<WorkTaskType> types, TimeSpan lease, CancellationToken) → WorkLease?`, where `WorkLease(WorkUnitKey, Owner, FencingToken, IReadOnlyList<WorkItemRecord> Items)`;
  - `RenewAsync(lease)`;
  - `CompleteAsync(lease)`;
  - `FailAsync(lease, error, retryAt?)`, which moves items to `DeadLettered` after `MaxAttempts`;
  - `GetStatusAsync(workspace, observer?, sender?, session?)`;
  - `EnqueueAsync(IReadOnlyList<NewWorkItem>)`, used by non-append producers in M2 (the reconciler only).

  Eligibility for `representation`: the unit's summed `TokenCount` is at least `EligibilityTokens`, or its oldest item is older than `MaxAge`. Items are returned up to `BatchTokenTarget` (always at least one).
- `IConclusionStore`, every write taking a `WorkLease? fence`:
  - `GetCollectionAsync`;
  - `FindExactAsync`;
  - `FindNearestAsync(collection, vector, maxDistance, sessionFilter?)`;
  - `InsertAsync(fence, IReadOnlyList<NewConclusion>)`;
  - `CorroborateAsync(fence, conclusionId, batchKey) → bool` (false when the key was already recorded);
  - `ReplaceContentAsync(fence, …)`;
  - `RecordDerivationAsync(fence, collection, newExplicitCount, at)`, the dream-counter seam;
  - `ListAsync`, `QueryAsync`, `GetAsync` and `TombstoneAsync` (204 semantics: a tombstoned conclusion is invisible to every read);
  - `PurgeTombstonesAsync(olderThan)`.

  A fenced write throws `LeaseLostException` when the lease is no longer held with that token.
- `IPeerCardStore`: `GetAsync(workspace, observer, observed)`, `SetAsync(…, entries, updatedBy)`.
- `IEmbeddingStore`: `ClaimPendingAsync(kind, batch, CancellationToken)`, `SaveAsync(...)`, `MarkFailedAsync(...)`. It covers message chunks and conclusions, so the reconciler has one loop.
- `IMessageStore.AppendAsync` gains an overload with `IReadOnlyList<DeriveIntent>? derive` (one per message: `Derive = true/false`). The existing signature forwards `null`, which means "no work", so M1 callers and tests are unchanged.
  - Inside the transaction, the provider resolves observers with `ObserverRule` and inserts one `representation` item per message whose sender has at least one observer, with payload `{ "observers": [...] }`.
  - It also inserts `Pending` `MessageEmbeddings` chunks for every message (chunking itself is Task 4's `ITextChunker`, passed in as precomputed `EmbeddingChunk`s on `NewMessage`).

- [ ] **Step 1:** Write the contract tests (abstract, in `Nachos.Testing`):
  - `Append_EnqueuesRepresentationWork_Atomically` (incl. serializer-throw rollback);
  - `Append_NoObservers_EnqueuesNothing`;
  - `Append_DeriveFalse_EnqueuesNothing`;
  - `Acquire_RespectsEligibilityAndBatchTarget`;
  - `Acquire_IsSerialPerUnit`;
  - `ExpiredLease_IsReclaimedWithNewFencingToken`;
  - `StaleFencingToken_WriteRejected`;
  - `Fail_BacksOffThenDeadLetters`;
  - `Corroborate_IsIdempotentPerBatchKey`;
  - `Tombstoned_IsInvisibleToEveryRead`;
  - `QueueStatus_CountsUnitsBySessionAndObserver`.

  Also write `ObserverRuleTests` (Review Focus 6) and `WorkUnitKeysTests` (length bound).
- [ ] **Step 2:** Run `dotnet test test/Nachos.DataLayer.InMemory.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement the types and the in-memory provider: one lock per workspace for appends and the queue, and brute-force cosine for `FindNearestAsync`/`QueryAsync`.
- [ ] **Step 4:** Run it again. Expected: PASS. Run the architecture tests too (Abstractions stays BCL-only).
- [ ] **Step 5:** Commit `feat(abstractions): M2 memory and queue contracts, observer rule, in-memory provider`.

### Task 3: SQL provider for M2 stores and transactional enqueue

**Files:**
- Create in `src/DataLayer/Nachos.DataLayer.SqlServer/`:
  - `Stores/{SqlConclusionStore,SqlPeerCardStore,SqlWorkQueue,SqlEmbeddingStore}.cs`;
  - `Entities/*` for the new tables;
  - `Filtering/` conclusion field mapping.
- Modify: `Stores/SqlMessageStore.cs` (enqueue and pending chunks in the append transaction), `SqlMemoryStore.cs`.
- Modify `src/Nachos.Core/NachosService.Messages.cs` (D4: resolve `reasoning.enabled` per message and build `DeriveIntent`s and chunks).
- Tests: `test/Nachos.DataLayer.SqlServer.Tests/{SqlM2StoreContractTests,SqlWorkQueueTests}.cs`.

**Depends on:** 1, 2, and 4 (for `ITextChunker` only; it can be stubbed until 4 lands).

**Claim (spec §10.3, normative):**
- `SET TRANSACTION ISOLATION LEVEL READ COMMITTED`.
- Candidate units are read from `WorkItems` with `WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)`, with no `ROWLOCK`.
- The lease is inserted (catch 2627/2601 → try the next candidate).
- An expired lease is taken by a single `UPDATE WorkUnitLeases SET Owner=@o, ExpiresAt=@e, FencingToken = FencingToken + 1 WHERE WorkUnitKey=@k AND ExpiresAt < @now`, with `@@ROWCOUNT = 1`.
- Every fenced write runs `EXISTS (SELECT 1 FROM WorkUnitLeases WITH (UPDLOCK) WHERE WorkUnitKey=@k AND Owner=@o AND FencingToken=@t)` in its own transaction, before the write.
- Multi-row locks are taken in `Id` order. The lock order extends M1's (`Upserts`): workspace → session → messages → work items → collections → conclusions.

Vector queries use `VECTOR_DISTANCE('cosine', Embedding, @q)` with a `SqlVector<float>` parameter over a candidate set pre-filtered by collection (and by session for explicit dedup). The search is exact; there is no ANN in M2 ([vector data type](https://learn.microsoft.com/en-us/sql/t-sql/data-types/vector-data-type), [EF Core vector search](https://learn.microsoft.com/en-us/ef/core/providers/sql-server/vector-search)).

- [ ] **Step 1:** Write `SqlM2StoreContractTests : StoreContractTests` and `SqlWorkQueueTests`:
  - `ConcurrentClaimers_NeverShareAUnit` (16 claimers, forced short leases, looped 20×, RCSI asserted ON);
  - `ExpiryReclaimRace_SingleWinner`;
  - `ParallelWorkers_NoDeadlock` (ordering test, spec §17.2);
  - `VectorNearest_MatchesInMemory` (same fixture vectors, same ranking);
  - `Append_WithDerive_ChunksAndItemsCommitTogether`.

  Also add `NachosServiceTests.CreateMessages_ReasoningDisabled_EnqueuesNothing` and `.MessageReasoningOverridesSession` for the Core half.
- [ ] **Step 2:** Run `dotnet test test/Nachos.DataLayer.SqlServer.Tests` (Docker) and `test/Nachos.Core.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run them again. Expected: PASS. Loop the concurrency tests 20× and record the result in the PR with the SHA.
- [ ] **Step 5:** Amend spec §7.1/§10.2 (D1, D2). Commit `feat(sql): M2 stores, fenced work queue, transactional enqueue`.

### Task 4: LLM layer

**Files:**
- Create in `src/Nachos.Core/Llm/`:
  - `ModelProfile.cs`, `LlmOptions.cs` (`Llm:Profiles:{name}`; per-agent profile name);
  - `NachosFallbackChatClient.cs` (a `DelegatingChatClient` that tries the profile chain on retryable errors: 408/429/5xx/timeouts);
  - `TokenAccounting.cs` (a `DelegatingChatClient` that records `nachos.llm.tokens{agent,profile,workspace}` from `ChatResponse.Usage`; metrics only, no table);
  - `EmbeddingOptions.cs`;
  - `EmbeddingDimensionCheck.cs` (an `IHostedService`: one probe embedding at start; a mismatch with `1536` fails startup with an actionable message);
  - `TextChunker.cs` (token-based o200k chunks with 20% overlap; spec §11.1);
  - `PromptTemplate.cs` (embedded Markdown templates with id and version; §11.11).
- Create in `src/Nachos.Hosting/`: `NachosBuilder` extensions `UseChatClient(...)`, `UseEmbeddingGenerator(...)`, `UseAzureOpenAI(...)` and `UseFakeAI()`.
- Create in `test/Nachos.Testing/`: `ScriptedChatClient.cs` and `DeterministicEmbeddingGenerator.cs`.

**Depends on:** none.

**Notes:**
- The pipeline is built with `ChatClientBuilder`: `UseOpenTelemetry()`, then token accounting, then fallback, then the provider client. Structured output uses `GetResponseAsync<T>`, which sets a JSON-schema `ResponseFormat` and returns `ChatResponse<T>.Result` ([M.E.AI chat completion](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)).
- The distributed cache middleware is M7. Do not add it.
- `UseFakeAI()` ships in `Nachos.Hosting` for the offline Aspire mode (spec §18.1). It registers:
  - the deterministic hash embedding generator (unit-norm and stable across processes);
  - a chat client that answers the Deriver's `ExtractionResult` schema by returning each target-peer message as one fact.

  That is enough to watch end-to-end derivation locally without an LLM. It is documented as not a memory-quality mode.

- [ ] **Step 1:** Write the tests:
  - `FallbackChatClientTests`: `RetryableError_UsesNextProfile`, `NonRetryable_Throws`, `ChainExhausted_ThrowsLastError`;
  - `TokenAccountingTests.RecordsUsagePerAgentAndWorkspace`;
  - `TextChunkerTests`: `OverlapIs20Percent`, `ShortTextIsOneChunk`, `NeverSplitsSurrogatePairs`;
  - `EmbeddingDimensionCheckTests.Mismatch_FailsStartupWithActionableMessage`;
  - `PromptTemplateTests.MissingPlaceholder_Throws`.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Core.Tests --filter Llm`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run it again. Expected: PASS.
- [ ] **Step 5:** Amend spec §4.2/§11.1 (D6). Commit `feat(core): LLM layer with profiles, fallback, token accounting, embeddings, chunking`.

### Task 5: Deriver, deduplication, corroboration

**Files:**
- Create in `src/Nachos.Core/Deriver/`:
  - `DeriverService.cs`, the `representation` handler;
  - `ExtractionResult.cs`;
  - `ConclusionDeduplicator.cs`;
  - `Prompts/deriver.v1.md` (clean-room; written from spec §11.2 only).
- Modify: `Configuration/DeriverOptions.cs` (D12: `EligibilityTokens 512`, `BatchTokenTarget 1024`, `MaxAge 30m`, `MaxInputTokens 25000`, `DedupMaxDistance 0.05`, `MaxAttempts 5`, `LeaseDuration 5m`).

**Depends on:** 2, 4.

**Handler flow** (one leased unit = one session and one observed sender):
1. Load the batch messages plus a little preceding context, bounded by `MaxInputTokens`. Messages are speaker-tagged and timestamped.
2. Resolve configuration (custom instructions and `reasoning.enabled`). Work items enqueued before reasoning was disabled are completed without an LLM call.
3. Make one `GetResponseAsync<ExtractionResult>` call. Validate the result: every `source_message_ids` entry must be in the batch and authored by the target, and content must be non-empty. Invalid facts are dropped and counted, not fatal. Zero facts is a valid result.
4. Embed the facts in one batch.
5. For each observer from `ObserverRule` (re-resolved at each message's `CreatedAt`):
   - exact dedup on normalized content + level + session (explicit only);
   - then semantic dedup `≤ DedupMaxDistance`, restricted to the same session for explicit facts;
   - on a duplicate, `CorroborateAsync(batchKey)`, where `batchKey` is the SHA-256 of `"{unitKey}|{firstItemId}|{lastItemId}"`; retries reuse the same items, so the key is stable;
   - when the new fact is richer (longer after normalization and contains every token of the old one), use `ReplaceContentAsync` instead;
   - otherwise insert the fact, with `source_message_ids` and the prompt id/version in `InternalMetadata`.
6. `RecordDerivationAsync`, the M5 seam: `NewExplicitCount += inserted`, `LastActivityAt = now`.
7. `CompleteAsync`. A `LeaseLostException` anywhere stops the handler without completing.

- [ ] **Step 1:** Write `DeriverTests` with `ScriptedChatClient` and the in-memory provider:
  - `ExtractsFactsToEveryObserverCollection`;
  - `OtherSpeakersAreContextOnly` (prompt-shape assertion: target tag present, other messages marked context);
  - `ZeroFacts_CompletesUnit`;
  - `InvalidSourceIds_FactDropped`;
  - `RetriedBatch_DoesNotInflateTimesDerived`;
  - `ReasoningDisabledAfterEnqueue_CompletesWithoutLlmCall`;
  - `LeaseLost_DoesNotComplete`;
  - `RecordsDreamCounters`.

  Write `ConclusionDeduplicatorTests`:
  - `ExactDuplicate_Corroborates`;
  - `SemanticDuplicateWithinThreshold_Corroborates`;
  - `ExplicitFact_OtherSession_IsNotDuplicate`;
  - `RicherFact_Replaces`.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement. Write the prompt from the spec's behavioral requirements only, and get it reviewed for clean-room compliance.
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): deriver with dedup and retry-safe corroboration`.

### Task 6: Worker loops (QueueConsumerService, ReconcilerService)

**Files:**
- Create in `src/Nachos.Core/Worker/`: `QueueConsumerService.cs`, `ReconcilerService.cs`, `WorkerOptions.cs`.
- Create `src/Nachos.Hosting/NachosWorkerServiceCollectionExtensions.cs` (`AddNachosWorker()`).
- Tests: `test/Nachos.Core.Tests/Worker/*`; SQL cases in `test/Nachos.DataLayer.SqlServer.Tests/WorkerIntegrationTests.cs`.

**Depends on:** 3, 5.

**Notes:**
- **`QueueConsumerService`** is a `BackgroundService` running `Worker:MaxConcurrency` loops (default 4).
  - Each loop runs `TryAcquireAsync`, then the handler registered for the task type, then renewal at lease/3, then `CompleteAsync` or `FailAsync` with exponential backoff plus jitter.
  - It acquires only task types that have a registered handler, so M2 never claims `dream`, `summary` and the other reserved types (the M5 seam needs no code).
  - It polls with backoff and jitter when idle.
  - An unhandled handler exception is logged once here, at the boundary.
- **`ReconcilerService`** is guarded by a lease on the work-unit key `reconciler:main`, so only one replica runs it. Each tick:
  - embeds `Pending` message chunks and conclusions in batches (`IEmbeddingStore`);
  - purges tombstoned conclusions older than `Reconciler:TombstoneRetention` (default 24 h);
  - prunes `Succeeded` work items older than `Reconciler:CompletedRetention` (7 d);
  - deletes expired idempotency records (the M1 index already exists).
- **Immediate best-effort embedding.** After a message append commits, the API fires a bounded best-effort embed of those chunks, with no retry, so search freshness doesn't wait for the reconciler tick. The reconciler is the guarantee. Implement it only if it stays trivial; otherwise defer it to M3 (it only matters for search).
- Metrics (§19): `nachos.queue.eligible_units`, `nachos.queue.oldest_age`, `nachos.queue.dead_lettered`, `nachos.deriver.facts`, `nachos.dedup.ratio`, `nachos.embeddings.pending`.

- [ ] **Step 1:** Write the tests:
  - `QueueConsumerTests`: `ProcessesAndCompletes`, `HandlerThrows_FailsWithBackoff`, `DeadLettersAfterMaxAttempts`, `RenewsLongRunningLease`, `IgnoresUnregisteredTaskTypes`, `StopsCleanlyOnCancellation`;
  - `ReconcilerTests`: `EmbedsPending`, `EmbeddingFailure_RetriesThenMarksFailed`, `PurgesOldTombstonesOnly`, `SingleReplicaUnderLease`;
  - SQL: `EndToEnd_AppendToConclusions_TwoWorkers`.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Commit `feat(worker): queue consumer and reconciler`.

### Task 7: VisibilityPolicy and read services

**Files:**
- Create in `src/Nachos.Core/Memory/`: `VisibilityPolicy.cs`, `RepresentationBuilder.cs`, `PeerContextService.cs`, `ConclusionService.cs`.
- Modify `INachosClient` and `NachosService`, adding these methods:
  - `CreateConclusionsAsync`;
  - `ListConclusionsAsync`;
  - `QueryConclusionsAsync`;
  - `GetConclusionAsync`;
  - `DeleteConclusionAsync`;
  - `GetPeerCardAsync`;
  - `SetPeerCardAsync`;
  - `GetRepresentationAsync`;
  - `GetPeerContextAsync`;
  - `GetQueueStatusAsync`.

  Add the wire contracts `Conclusion`, `ConclusionCreate`, `PeerCardResponse`, `PeerContext`, `RepresentationResponse` and `QueueStatus`, matching the manifest.
- Tests: `test/Nachos.Core.Tests/Memory/*`, `VisibilityNegativeTests.cs`, and the architecture test for Review Focus 5.

**Depends on:** 2. Q3 and Q4 must be answered.

**Notes:**
- **`VisibilityPolicy`** is created once per call from `(observer, observed, sessionAllowlist?, scope?)`. In M2 it does three things:
  - it restricts conclusions to explicit ones attributable to allowlisted sessions when an allowlist is set (spec §11.4);
  - it **omits the peer card** whenever an allowlist or scope is active (§11.8);
  - it fails closed on an empty allowlist.

  Every M2 read method takes a policy. There is no overload without one.
- **`RepresentationBuilder`** merges three sources: top-k semantic matches (only when `search_query` is set; the query is embedded on the request path), most-frequent (`include_most_frequent`, by `TimesDerived`) and most-recent. It deduplicates by conclusion id, caps at `max_conclusions`, orders deterministically (source rank, then `CreatedAt`, then `Id`), and formats Markdown sections by level. There is no LLM call.
- **API-created conclusions** are inserted with `EmbeddingState = Pending` and embedded best-effort inline. The reconciler is the guarantee. They are level `explicit` with an empty `source_ids` list (Q5). They are not deduplicated.
- **Peer card:** `observer = peer_id`, `observed = target ?? peer_id`. `PUT` replaces the entries (Q4).
- **Queue status** shapes the wire `QueueStatus`. `sessions` is a map keyed by session name when the work is session-scoped.

- [ ] **Step 1:** Write the tests:
  - `RepresentationBuilderTests`: `MergesSourcesWithoutDuplicates`, `DeterministicOrder`, `MaxConclusionsCaps`, `NoQuery_SkipsSemantic`;
  - `PeerContextServiceTests.CardAndRepresentationForTarget`;
  - `ConclusionServiceTests`: `CreateBatch_1to100`, `Create_ContentOver65535_422`, `Query_TopKAndDistance`, `Delete_TombstonesAndHides`;
  - `VisibilityNegativeTests` (one per consumer: allowlisted session excludes others, card omitted under allowlist, empty allowlist fails closed);
  - `ArchitectureTests.MemoryReads_RequireVisibilityPolicy`.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): visibility policy, representation, peer context, conclusions, queue status`.

### Task 8: API routes, auth matrix, client

**Files:**
- Create in `src/Nachos.Api/Endpoints/`: `ConclusionEndpoints.cs`, `PeerMemoryEndpoints.cs` (card, representation, context), `QueueEndpoints.cs`.
- Modify:
  - `NotImplementedEndpoints.cs`: remove exactly the M2 pairs, which are the five conclusion routes, `GET`/`PUT P/card`, `GET P/context`, `POST P/representation` and `GET W/queue/status`;
  - `Auth/RouteRequirements.cs`;
  - `Json/NachosJsonContext.cs`.
- Modify `src/Nachos.Client/NachosHttpClient.cs` (new methods; read routes retry, `POST C` does not).
- Tests: `test/Nachos.Api.Tests/{ConclusionEndpointTests,PeerMemoryEndpointTests,QueueEndpointTests}.cs`, `WireCoverageTests` updated, `ScopeMatrixTests` rows, and `test/Nachos.Client.Tests` round-trips. Add conformance smoke scenarios for conclusions create/list/query/delete and card get/set.

**Depends on:** 7.

**Auth (owner, Q7): mirror Honcho.** Each M2 route's scope requirement matches Honcho's documented scoped-key behaviour, established from Honcho's public docs and black-box SDK behaviour only (clean-room, spec §3), never its source.
- Step 0 records, per route, the evidence (doc link or SDK observation) and the resulting `RouteAccess` in the PR.
- Where the public evidence is silent, fall back to: conclusions and `W/queue/status` → `RouteAccess.Workspace`; `P/card`, `P/representation`, `P/context` → `RouteAccess.Peer` with `p == peer_id`. List each fallback in the PR.

- [ ] **Step 0:** Collect the scoped-key evidence above and write the `ScopeMatrixTests` rows from it.

- [ ] **Step 1:** Write the endpoint tests for each route. Cover status codes exactly as in `honcho-v3-wire.json`, the 422 bounds (batch 1–100, content ≤ 65,535, `top_k` 1–100, `distance` 0–1, `max_conclusions` 1–100), the 401 matrix rows, the 501 list shrinking by exactly the M2 pairs, and the OpenAPI wire diff.
- [ ] **Step 2:** Run `dotnet test test/Nachos.Api.Tests test/Nachos.Client.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run them, then the SDK smoke (`Invoke-SdkSmoke.ps1`). Expected: PASS.
- [ ] **Step 5:** Commit `feat(api): conclusions, peer card, representation, context, queue status routes`.

### Task 9: Worker host, Aspire, infra (offline), docs

**Files:**
- Create `src/Nachos.Worker/{Nachos.Worker.csproj,Program.cs}` (`Microsoft.NET.Sdk.Worker`: ServiceDefaults, `AddNachos(...).AddNachosWorker()`, SQL provider, AI from configuration).
- Modify:
  - `src/Nachos.AppHost/AppHost.cs`: add `AddProject<Projects.Nachos_Worker>("worker").WithReference(nachosDb).WaitFor(api)`; set `Nachos__Ai__Mode=Fake` unless an Azure OpenAI connection is configured (spec §18.1);
  - `test/Nachos.AppHost.Tests`: `AppHostSmokeTests.MessageBecomesConclusion_WithFakeAI`.
- Infra:
  - create `infra/modules/worker-app.bicep` (no ingress, `minReplicas = 1`, CPU scale rule; the KEDA `mssql` rule stays commented behind the R7 gate);
  - modify `infra/main.bicep` (chat/embedding deployment name parameters, worker module);
  - modify `azure.yaml` (worker service);
  - add `test/Nachos.Infra.Tests` cases (Bicep builds and lints; worker has no ingress; `workerMinReplicas = 0` is rejected).
- Docs:
  - README "What works today" and the 501 table;
  - docs-site concept page "Memory formation" and guide "Run the worker";
  - generated options reference (`Llm`, `Embeddings`, `Deriver`, `Worker`, `Reconciler`).

**Depends on:** 6, 8.

- [ ] **Step 1:** Write the AppHost smoke and infra tests.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the AppHost suite (Docker), the infra tests and the docs build. Expected: PASS.
- [ ] **Step 5:** Commit `feat(worker): worker host, Aspire wiring, offline worker infra, M2 docs`.

---

## M2 Done checklist

The M2 PR merges when all of these hold:

- [ ] Tasks 1–9 are committed with their tests.
- [ ] CI is green at the final SHA: build-test (both OSes), SQL/CLI/AppHost (Docker), schema (both dacpacs; the reviewed M1→M2 upgrade adds the tables), infra, docs, and SDK smoke.
- [ ] The queue concurrency tests have been looped 20× on SQL, with results posted against the SHA.
- [ ] Locally (Aspire, `UseFakeAI`): a posted message produces conclusions, visible through `C/list` and `P/representation`, with no manual steps.
- [ ] No M2 read path bypasses `VisibilityPolicy` (architecture test green).
- [ ] The spec is amended for D1–D7 and D12, and for each answered open question.
- [ ] The README and docs pages match what shipped.
- [ ] The Azure exit criterion is met by an owner-approved run, or listed as pending owner approval in the PR (this blocks the release tag, not the merge).

## Owner actions requested (not performed by agents)

1. ~~Answer the open questions below~~ (answered 2026-10-28).
2. **Approve one M2 Azure smoke run** once Task 9 lands. The agents will post the exact commands (`azd env new nachos-m2 …`, `azd up`) plus the model deployment names. The run verifies the worker, Azure OpenAI chat and embeddings, and one end-to-end derivation. Cleanup (`azd down --purge`) needs its own approval.
3. **R7 KEDA verification (optional for M2 merge):** approve a run that applies the `mssql` scale rule with the worker's managed identity on Container Apps. Until that run succeeds, the worker stays at `minReplicas = 1` with CPU scaling. Microsoft's docs confirm the `identity` property on Container Apps scale rules in general, but no source confirms the `mssql` scaler specifically ([Container Apps managed identity](https://learn.microsoft.com/azure/container-apps/managed-identity), [KEDA mssql scaler](https://keda.sh/docs/scalers/mssql/)).
4. **(Optional) A live Deriver quality spot-check** through the `azure-live` environment. The eval harness itself is M4.

## Owner decisions (2026-10-28)

Q1, Q2, Q6 and Q7 were answered by the owner. Q3, Q4, Q5 and Q8 take the proposed default.

| # | Question | Decision | Blocks |
|---|---|---|---|
| Q1 | Spec §7.3 keeps the schema version at `1` until the first release, but the deployer only applies changes when the stamp is behind, so M1 databases would never get the M2 tables (D3). Bump to `2` now? | **Owner: no, stay at `1`.** Recreate pre-release databases (or use the reviewed upgrade). | Task 1 |
| Q2 | The embedding dimension is fixed in DDL (`VECTOR(1536)`, `text-embedding-3-small`). Is a deploy-time dimension needed? | **Fixed at 1536.** Another model must use the `dimensions` request parameter (spec §7.4). Startup fails on a mismatch. **Owner: confirmed.** | Task 1 |
| Q3 | `PeerRepresentationGet.scope` exists on the wire, but scopes are M6. What should M2 do when `scope` is set? | Return **422** `scope is not supported in this Nachos version`, so it fails closed. | Task 7 |
| Q4 | Should `PUT P/card` enforce the 40-entry cap and the category prefixes (`IDENTITY`/`ATTRIBUTE`/`RELATIONSHIP`/`INSTRUCTION`), which the spec defines only for dream-written cards (§11.10)? | **No cap or prefix check on `PUT`** (store what the caller sends; the wire has no bound). The cap applies to agent-written cards (M5). | Task 7 |
| Q5 | For explicit conclusions (deriver- or API-created), should the wire `source_ids` list the source **message** public ids, or stay empty? The spec stores message provenance separately from `ConclusionSources` (conclusion → conclusion premises). | **Empty for explicit**, with message ids kept in `InternalMetadata`. Revisit with black-box SDK evidence. | Tasks 5, 7 |
| Q6 | Should Bicep provision the Azure OpenAI account and model deployments, or keep taking an existing endpoint (`openAiEndpoint`) as M1 does? | **Keep taking an existing endpoint**, adding deployment-name parameters and the `Cognitive Services OpenAI User` role for the managed identity. Provisioning AOAI raises quota and region issues (R6). **Owner: confirmed.** | Task 9 |
| Q7 | Should peer-scoped keys reach `P/representation`, `P/context` and `P/card` for their own peer, and should conclusions be workspace-only? | **Owner: mirror Honcho's scoped-key behaviour** from public docs and black-box SDK evidence (Task 8 Step 0); own-peer/workspace split is the fallback where evidence is silent. | Task 8 |
| Q8 | Should the Deriver also maintain the peer card when `peer_card.create` is true? Spec §11.2 lists no card step, while card updates are a dream behavior (§11.10). | **No card writes in M2.** Cards are set via `PUT` only until M5. | Task 5 |
