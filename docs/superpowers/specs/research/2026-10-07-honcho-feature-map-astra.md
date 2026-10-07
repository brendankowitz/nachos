> **Provenance:** Independent research produced by a GPT-6 Astra research agent on 2026-10-07 to cross-check the Nachos design spec (`../2026-10-07-nachos-design.md`). Citations point to upstream Honcho source for behavioral reference only — see the clean-room rules in the spec. Its Postgres-centric recommendations were superseded by the spec's SQL Server decision.

# Honcho → Nachos: Independent Feature and Architecture Mapping

**Research date:** 7 October 2026  
**Primary upstream:** [`plastic-labs/honcho`](https://github.com/plastic-labs/honcho)  
**Pinned `main` revision:** `e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16`  
**Latest published release found:** `v3.2.2`, published 1 October 2026, tag commit `06ed1929cf017c333a87c41d130bb6a0605d91c0`  
**Historical comparison:** `v2.5.1`, commit `a42252867fff80695abedca347eee397fefc7d44`


Unless explicitly marked otherwise, source citations refer to the pinned `main` revision above. The .NET/Azure column and deployment recommendations are proposals, not descriptions of existing Honcho behavior.

---

## 1. Executive summary

**Nachos should target the current Honcho v3 HTTP contract, not reproduce the v2 architecture indiscriminately.** Current `main` mounts `/v3` routes and ships server version `3.2.2`; the Python and TypeScript SDKs explicitly address `/v3`. SDK package versions are independent of server/API versions—current TypeScript source identifies itself as SDK `2.5.1` while using API `v3`.  
Sources: `plastic-labs/honcho:src/main.py:211-224`; `plastic-labs/honcho:pyproject.toml:1-9`; `plastic-labs/honcho:sdks/python/src/honcho/http/routes.py:1-12`; `plastic-labs/honcho:sdks/typescript/src/api-version.ts:1-11`.

The most important architectural distinction is that **v3 ingestion is primarily explicit-fact extraction**, using one structured LLM call per batch and sharing its output across observer collections. Deduction, induction, contradiction handling, and automatic peer-card maintenance principally belong to the **dreaming subsystem**. The v2.5.1 deriver instead incorporated existing representations, history, and peer cards into more substantial ingestion-time reasoning.  
Sources: `plastic-labs/honcho:src/deriver/deriver.py:139-246`; `plastic-labs/honcho:src/deriver/prompts.py:65-98`; `plastic-labs/honcho:src/dreamer/orchestrator.py:120-129`; **v2.5.1** `plastic-labs/honcho:src/deriver/deriver.py:169-218,302-305,422-438`.

A credible Azure-first implementation is:

- **.NET 10 / ASP.NET Core** for the REST server.
- **EF Core + Npgsql + pgvector** on Azure Database for PostgreSQL Flexible Server.
- A separate **.NET Worker Service** using a PostgreSQL-backed work queue and transactional outbox.
- **Microsoft.Extensions.AI** abstractions, with explicit adapters for provider-specific reasoning, structured-output, and streaming behavior.
- **Azure OpenAI / Azure AI Foundry** as the primary managed inference choices, without removing support for OpenAI-compatible and Anthropic providers.
- **OpenTelemetry → Azure Monitor/Application Insights**.
- **Aspire** for local orchestration and **azd/Bicep → Azure Container Apps** for deployment.

Keeping PostgreSQL as both relational and vector storage is the lowest-parity-risk starting point. Azure AI Search and Cosmos DB vector search are useful later alternatives, but are not transparent substitutions for Honcho’s relational filtering, distance thresholds, reasoning provenance, and deletion semantics.

---

## 2. Overview of Honcho architecture

### 2.1 Deployment and execution topology

Honcho consists of:

1. **FastAPI API process**
   - Authentication, validation, CRUD, search, context retrieval.
   - Synchronous/on-demand dialectic reasoning.
   - Background enqueue and immediate-embedding scheduling.
2. **Deriver worker process**
   - Consumes durable PostgreSQL queue rows.
   - Dispatches representation extraction, summaries, dreams, webhooks, deletion, reconciliation, and scope maintenance.
3. **PostgreSQL**
   - System of record for resources, conclusions, queue state, associations, and provenance.
   - Default vector-search backend through pgvector.
4. **Optional external vector backend**
   - Turbopuffer, LanceDB, Qdrant, or Chroma.
5. **Optional Redis**
   - Caching and associated coordination; not the primary work queue.
6. **LLM/embedding providers**
   - OpenAI-compatible, Anthropic, and Gemini transports.
7. **Separate MCP server**
   - TypeScript implementation with HTTP and stdio entry points.

Sources: `plastic-labs/honcho:docker-compose.yml.example:12-112`; `plastic-labs/honcho:src/models.py:573-641`; `plastic-labs/honcho:src/config.py:1304-1328,1436-1494`; `plastic-labs/honcho:src/llm/backend.py:48-89`; `plastic-labs/honcho:mcp/src/stdio.ts:14-27`; `plastic-labs/honcho:mcp/src/http.ts:226-245`.

### 2.2 Core data relationships

```text
Workspace
 ├── Peers
 ├── Sessions
 │    ├── SessionPeer memberships/configuration
 │    ├── Messages
 │    │    └── MessageEmbedding chunks
 │    └── Short/long summaries in internal metadata
 ├── Collections: unique (workspace, observer, observed)
 │    └── Documents / public Conclusions
 │         └── DocumentSource provenance edges
 ├── Named scopes: implemented using special observer peers
 ├── Webhook endpoints
 └── Queue items

Peer internal metadata
 └── Peer cards keyed by observer/observed relationship
```

A `Collection` is not a generic public document bucket: it represents **one observer’s knowledge of one observed peer**. A peer’s self-view is the pair `(peer, peer)`. A directional representation uses `(observer, target)`.  
Sources: `plastic-labs/honcho:src/models.py:342-382`; `plastic-labs/honcho:src/crud/peer_card.py:22-52`; `plastic-labs/honcho:src/schemas/api.py:263-265,498-510`.

### 2.3 Important v2 → v3 differences

| Area | v2.5.1 | Current v3 |
|---|---|---|
| API root | `/v2` | `/v3` |
| Public memory records | `/observations` | `/conclusions`; some input compatibility aliases remain |
| Queue status | `/{workspace}/deriver/status` | `/{workspace}/queue/status` |
| Manual dream endpoint | `/{workspace}/trigger_dream` | `/{workspace}/schedule_dream` |
| Ingestion reasoning | Existing working representation/history/card fed into reasoning | Minimal explicit-fact extraction, one result fanned out to observers |
| Peer-card updates | Dedicated update call from deriver | Dream deduction/card-refresh specialists |
| Additional current surface | Not present in inspected v2 router set | Named scopes, workspace chat, evidence and structured chat responses |

Sources: **v2.5.1** `plastic-labs/honcho:src/main.py:173-179`; `plastic-labs/honcho:src/routers/observations.py:15-29`; `plastic-labs/honcho:src/routers/workspaces.py:130-209`; `plastic-labs/honcho:src/deriver/deriver.py:169-218,435-487`. Current: `plastic-labs/honcho:src/routers/workspaces.py:190-234,290-309`; `plastic-labs/honcho:src/routers/scopes.py:30-65`; `plastic-labs/honcho:src/schemas/api.py:715-723,772-862`.

**Recommendation:** Treat v2 support as an explicit compatibility project. Merely mounting the same implementation under `/v2` will not provide behavioral or schema parity.

---

## 3. Feature inventory and .NET/Azure mapping

### Classification

- **Core:** Required for a useful Honcho-like server and its fundamental contracts.
- **Important:** Needed for substantial Honcho parity or production readiness.
- **Optional:** Valuable extension that can be deferred.
- **Out of scope:** Historical or hosted-platform behavior that should not automatically become part of the port.

### 3.1 Data model and public resource capabilities

| Feature | Verified Honcho capability / entities | Essentiality | Proposed Nachos mapping | Source |
|---|---|---:|---|---|
| Workspace tenancy | Top-level container with name, metadata, internal metadata, configuration, sessions and peers. | Core | Tenant/workspace entities in PostgreSQL; enforce workspace scope in every repository operation and authorization policy. | `plastic-labs/honcho:src/models.py:104-133` |
| Workspace/App terminology | Current schema uses Workspaces and Peers; migration history contains the older app/user model. | Out of scope for old API | Use Workspace terminology in the v3 contract. Add App/User aliases only if deliberately supporting historical SDKs. | `plastic-labs/honcho:migrations/versions/d429de0e5338_adopt_peer_paradigm.py:35-75` |
| Peers | Persistent entities representing humans, agents, services or other subjects; workspace-unique names, metadata and configuration. | Core | EF Core `Peer`; keep domain entity identity distinct from authenticated principal identity. | `plastic-labs/honcho:src/models.py:137-170`; `plastic-labs/honcho:src/deriver/prompts.py:75-85` |
| Sessions | Named interaction containers, active/inactive state, metadata/configuration, many-to-many peer membership. | Core | EF Core session and association entities; explicit lifecycle state. | `plastic-labs/honcho:src/models.py:174-209` |
| Session-peer configuration | Per-membership configuration plus `joined_at` and nullable `left_at`; directional observing is membership-sensitive. | Core | Explicit join entity, not an implicit EF many-to-many relation; retain time-window semantics. | `plastic-labs/honcho:src/models.py:40-100`; `plastic-labs/honcho:src/deriver/enqueue.py:356-372` |
| Messages | Ordered text records, sender peer, timestamps, public ID, token count, metadata; batch creation and metadata-only update. | Core | Append-oriented message entity; per-session sequence allocation in a transaction; public IDs compatible with Honcho. | `plastic-labs/honcho:src/models.py:213-276`; `plastic-labs/honcho:src/schemas/api.py:328-383`; `plastic-labs/honcho:src/crud/message.py:426-461` |
| IDs and name aliases | Workspace/peer/session `name` serializes as API `id`; message public ID differs from internal numeric identity. | Core | Dedicated wire DTOs. Do not expose EF primary keys by convention. | `plastic-labs/honcho:src/schemas/api.py:153-163,223-234,363-377,446-458` |
| Metadata | Public JSON metadata is distinct from internal metadata. Configuration is a separate structure. | Core | JSONB-backed `JsonDocument`/typed wrappers; do not allow public writes to internal metadata. | `plastic-labs/honcho:src/models.py:111-119,141-155,179-193` |
| Collections | Unique observer/observed/workspace relationship holding conclusions. No generic collection CRUD router is mounted. | Core internally | Internal collection aggregate keyed by workspace and directional pair. | `plastic-labs/honcho:src/models.py:342-382`; `plastic-labs/honcho:src/main.py:213-221` |
| Documents / conclusions | Public conclusions backed by documents with content, level, derivation count, vectors, session attribution and provenance. Supports direct insertion, listing, semantic query, fetch and deletion. | Core | `Conclusion` domain entity backed by PostgreSQL; retain wire names `observer_id`, `observed_id`, `source_ids`, `times_derived`. | `plastic-labs/honcho:src/models.py:390-444`; `plastic-labs/honcho:src/schemas/api.py:605-723`; `plastic-labs/honcho:src/routers/conclusions.py:24-184` |
| Reasoning provenance | `DocumentSource` stores ordered derived→source edges; source IDs deliberately do not have a foreign key to the source document. | Important | Explicit provenance-edge entity and bounded graph traversal; choose orphan-source retention deliberately. | `plastic-labs/honcho:src/models.py:546-569` |
| Named scopes | Named sets of sessions implemented as silent `scope.<name>` observer peers. Supports membership backfill/removal and status. | Important | Prefer first-class scope domain types while preserving observable single-scope versus union semantics. | `plastic-labs/honcho:src/crud/scope.py:1-15,65-79`; `plastic-labs/honcho:src/routers/scopes.py:36-208` |
| Session cloning | Copy session metadata/configuration/messages and memberships, optionally through a cutoff message; new identifiers/timestamps. | Optional | Transactional clone service; do not assume conclusions/summaries are copied unless intentionally matching implementation. | `plastic-labs/honcho:src/crud/session.py:773-789` |
| File-to-message upload | Multipart upload of PDF, text or JSON; extract text and split into messages, rather than store arbitrary binary attachments. | Important | ASP.NET multipart endpoint; PDF text extractor and deterministic text chunking. Azure Document Intelligence OCR is a later enhancement, not direct parity. | `plastic-labs/honcho:src/utils/files.py:23-104,113-140`; `plastic-labs/honcho:src/routers/messages.py:182-228` |
| Deletion | Session/workspace deletion is queued; conclusion deletion immediately hides a record using an internal tombstone. Workspace deletion requires no active sessions. | Core | Durable deletion jobs, tombstones, deletion status extension, explicit provenance/card cleanup policy. | `plastic-labs/honcho:src/routers/sessions.py:402-440`; `plastic-labs/honcho:src/crud/workspace.py:285-309`; `plastic-labs/honcho:src/crud/document.py:992-1027` |

### 3.2 Memory, reasoning and retrieval

| Feature | Verified Honcho behavior | Essentiality | Proposed Nachos mapping | Source |
|---|---|---:|---|---|
| Observe flags | `observe_me` controls whether a sender is modeled; session override wins over peer setting; defaults to true. Other peers observe only when `observe_others` is enabled. | Core | Typed nullable configuration with an explicit resolver; preserve missing/null/default distinctions. | `plastic-labs/honcho:src/schemas/configuration.py:248-261`; `plastic-labs/honcho:src/deriver/enqueue.py:257-292,343-383` |
| Hierarchical configuration | Message → session → workspace → global defaults. Reasoning, summaries, dreams, cards and dialectic custom instructions. | Core | `IOptions` for deployment defaults plus domain configuration resolver; message configuration is not a general override of every subsystem. | `plastic-labs/honcho:src/utils/config_helpers.py:112-180`; `plastic-labs/honcho:src/schemas/configuration.py:129-184` |
| Explicit-fact deriver | Batch analysis produces atomic facts about the target’s own messages; other speakers provide context, not independently attributed facts about the target. | Core | Bounded `BackgroundService` job using structured output and strongly validated extraction DTOs. | `plastic-labs/honcho:src/deriver/prompts.py:65-98`; `plastic-labs/honcho:src/deriver/deriver.py:159-217` |
| Multi-observer fan-out | One extracted representation is persisted into all applicable observer collections. | Core | Separate extraction result from persistence fan-out; avoid one LLM call per observer. | `plastic-labs/honcho:src/deriver/deriver.py:230-265` |
| Deduplication | Exact and semantic dedup; explicit conclusions never deduplicate across sessions; derived levels may. Derivation frequency is retained. | Core | SQL uniqueness/normalized-text strategy plus deterministic semantic duplicate policy; track retries independently from genuine corroboration if improving semantics. | `plastic-labs/honcho:src/crud/document.py:492-513,1369-1422` |
| Working representation | Curated selection blending semantic matches, frequently derived facts and recent facts; returned as text without a new answer-generation call. | Core | Retrieval service with deterministic selection, de-duplication, ordering and Markdown formatting. | `plastic-labs/honcho:src/crud/representation.py:317-411` |
| Peer card | Relationship-specific concise identity profile, stored in peer internal metadata; directly readable/writable. Automatic generation uses constrained identity categories. | Important | Typed card value object persisted separately or as JSONB, with compatible list-of-strings DTO. | `plastic-labs/honcho:src/crud/peer_card.py:22-52`; `plastic-labs/honcho:src/dreamer/specialists.py:76-132` |
| Peer context | Combined peer representation and peer card, optionally about another peer from the caller peer’s perspective. | Core | Composite retrieval endpoint, not another LLM call. | `plastic-labs/honcho:src/routers/peers.py:648-699` |
| Session context | Recent messages, optional short/long summary, optional peer representation/card, token controls and recall restrictions. | Core | Token-budget assembly service; preserve flags but improve strict budget enforcement explicitly. | `plastic-labs/honcho:src/routers/sessions.py:705-793,987-1033` |
| Summaries | Independent short/long schedules; cumulative summaries incorporate prior summary plus new conversation. Stored in session internal metadata. | Core | Background summarizer with versioned coverage marker and concurrency-safe writes. | `plastic-labs/honcho:src/utils/summarizer.py:102-173,288-367,694-743` |
| Hybrid message search | Semantic vector search plus PostgreSQL English full-text search/ILIKE fallback, combined using reciprocal rank fusion. Workspace/session/peer search routes. | Core | Npgsql pgvector + PostgreSQL FTS + identical RRF algorithm for initial parity. | `plastic-labs/honcho:src/utils/search.py:36-75,251-314,317-338,438-451` |
| Peer-perspective search | Searches sessions the peer attended, constrained by membership time windows. | Important | Membership-aware SQL filtering before/after vector candidate retrieval. | `plastic-labs/honcho:src/utils/search.py:345-372` |
| Dialectic peer chat | Agentic, tool-assisted natural-language answers about a peer or directional view. Supports reasoning levels and optional streaming. | Core | `IChatClient` plus a controlled tool loop, not a generic chatbot endpoint. | `plastic-labs/honcho:src/dialectic/core.py:145-183,548-606`; `plastic-labs/honcho:src/schemas/api.py:772-825` |
| Workspace chat | Queries multiple peers without an anchor peer; prefetches workspace orientation and then requires actual recall tools. | Important | Workspace orchestration over the same retrieval services, with tenant-scoped tool execution. | `plastic-labs/honcho:src/dialectic/workspace.py:91-142,173-218` |
| Structured answers | Optional JSON Schema; returned `content` remains a JSON **string**. Only a conservative schema subset is supported; some constraints are model hints rather than server-enforced. | Important | JSON-schema capability validation, provider adapters, explicit output validation policy; preserve wire string shape. | `plastic-labs/honcho:src/schemas/api.py:813-820`; `plastic-labs/honcho:src/dialectic/core.py:613-617` |
| Evidence | Optional conclusions, message references, ordered tool calls and trace ID supporting an answer. Evidence is what was read, not proof of every generated assertion. | Important | Request-scoped evidence collector instrumenting prefetch and tools; keep content/provenance visibility aligned. | `plastic-labs/honcho:src/schemas/api.py:865-963`; `plastic-labs/honcho:src/dialectic/core.py:264-320,621-622` |
| Streaming | SSE content deltas followed by a terminal `done` event; evidence, if requested, arrives on terminal event. | Important | `IAsyncEnumerable`/streamed response writing with exact SSE framing and cancellation propagation. | `plastic-labs/honcho:src/utils/sse.py:14-36` |
| Dream consolidation | Deduction followed by induction; knowledge updates, contradictions, higher-order patterns, source links and card maintenance. | Important | Separate durable job type with bounded agent loops and validated mutation tools. | `plastic-labs/honcho:src/dreamer/orchestrator.py:120-129`; `plastic-labs/honcho:src/dreamer/specialists.py:620-678,750-811` |
| Card-refresh dream | Card-only refresh or rebuild; cannot create/delete conclusions. | Important | Smaller maintenance job distinct from expensive consolidation. | `plastic-labs/honcho:src/dreamer/orchestrator.py:375-401`; `plastic-labs/honcho:src/dreamer/specialists.py:841-865` |
| Surprisal prioritization | Optional geometric novelty/surprisal sampling with multiple tree/index algorithms; off by default. | Optional | Defer. Start with recent/underexplored facts; add a separately evaluated novelty strategy later. | `plastic-labs/honcho:src/config.py:1331-1354`; `plastic-labs/honcho:src/dreamer/orchestrator.py:183-225` |

### 3.3 Infrastructure, operations and integrations

| Feature | Verified Honcho behavior | Essentiality | Proposed Nachos mapping | Source |
|---|---|---:|---|---|
| Durable work queue | PostgreSQL `QueueItem`, work-unit keys and unique active ownership records. | Core | PostgreSQL job table, leases/ownership fencing and transactional outbox; separate worker process. | `plastic-labs/honcho:src/models.py:573-641`; `plastic-labs/honcho:src/deriver/queue_manager.py:421-445` |
| Batching/backpressure | Token-based eligibility, max-age flush, bounded workers, poll backoff/jitter, stale-owner recovery. | Core | `BackgroundService`, bounded channels/semaphores, durable scheduling; do not use in-memory channels as the only queue. | `plastic-labs/honcho:src/config.py:879-980`; `plastic-labs/honcho:src/deriver/queue_manager.py:322-328,637-655` |
| Queue status | Counts representation/summary/dream work; internal deletion/reconciliation/webhook tasks excluded; completed totals are cleanup-window counts. | Important | Compatible status DTO plus a separate operational job-status API if desired. | `plastic-labs/honcho:src/routers/workspaces.py:195-213`; `plastic-labs/honcho:src/schemas/api.py:1022-1043` |
| Reconciliation | Vector synchronization, pending embeddings, cleanup and staged backfills. | Important | Idempotent repair jobs and migration/backfill registry. | `plastic-labs/honcho:src/reconciler/scheduler.py:1-7,59-78`; `plastic-labs/honcho:migrations/versions/a7c3e9f1b2d4_add_document_sources_table.py:1-12` |
| JWT authorization | Optional HS256 bearer auth; admin/workspace/peer/session claims and explicit member-read policy. | Core | Compatible JWT scheme plus optional Entra scheme. Translate Entra identity to domain authorization, not merely “authenticated means authorized.” | `plastic-labs/honcho:src/security.py:31-80,197-276` |
| Key minting | Admin endpoint generates scoped JWTs with optional expiry. | Important | Compatibility endpoint with signing secret in Key Vault; preferably configurable expiry policy. | `plastic-labs/honcho:src/routers/keys.py:17-66` |
| LLM abstraction | Normalized completion/streaming interface, tool calls, usage, provider history adapters, retry and final-attempt fallback. | Core | `Microsoft.Extensions.AI` plus a Nachos-owned execution policy and provider extension metadata. | `plastic-labs/honcho:src/llm/backend.py:10-89`; `plastic-labs/honcho:src/llm/api.py:180-205,279-300,461-484` |
| Embeddings | OpenAI-compatible and Gemini embedding transports; batching, token chunking, dimension/count validation. | Core | `IEmbeddingGenerator<string, Embedding<float>>`; configurable model/deployment identity and schema validation. | `plastic-labs/honcho:src/embedding_client.py:201-306,675-707` |
| pgvector | Default backend; cosine-distance HNSW indexes on documents and message chunks. | Core | EF Core/Npgsql with pgvector on PostgreSQL Flexible Server. Enable the `vector` extension explicitly. | `plastic-labs/honcho:src/models.py:314-338,510-518`; `plastic-labs/honcho:src/config.py:1436-1445` |
| External vector stores | Turbopuffer, LanceDB, Qdrant, Chroma; migration/dual-storage and synchronization states. | Optional | Port a vector-store interface; add Azure AI Search only after ranking/filtering conformance tests. Cosmos is a broader storage redesign. | `plastic-labs/honcho:src/vector_store/__init__.py:53-195,198-260`; `plastic-labs/honcho:src/config.py:1436-1494` |
| Webhooks | Endpoint registration/list/delete/test; `queue.empty` and `test.event`; HMAC-SHA256 signature header. | Important | Outbox-backed delivery worker, `HttpClientFactory`, retries and dead-letter state; retain payload/header contract. | `plastic-labs/honcho:src/webhooks/events.py:15-43`; `plastic-labs/honcho:src/webhooks/webhook_delivery.py:33-58,94-104` |
| Redis caching | Optional cache with TTL and Redis/cluster settings. | Optional | `HybridCache`/`IDistributedCache`; Azure Managed Redis if justified. Start without it. | `plastic-labs/honcho:src/config.py:1304-1328`; `plastic-labs/honcho:src/main.py:142-147` |
| Telemetry | Sentry, Prometheus, CloudEvents, Langfuse projection, logs and optional reasoning trace capture. | Important | OTel traces/metrics/logs to Azure Monitor; optional Prometheus and Langfuse exporters; payload capture opt-in. | `plastic-labs/honcho:src/telemetry/__init__.py:1-12,32-48`; `plastic-labs/honcho:src/telemetry/langfuse_exporter.py:1-30` |
| Health/scaling metrics | `/health`, `/metrics`, hidden `/deriver/metrics` with outstanding-work estimate and backlog dimensions. | Important | Liveness/readiness endpoints and queue-age/work-duration metrics suitable for Container Apps scaling. | `plastic-labs/honcho:src/main.py:223-230`; `plastic-labs/honcho:src/routers/deriver_metrics.py:21-43` |
| Rate limits | SDKs understand 429/retry-after. No explicit inbound quota middleware was found in inspected server startup/configuration. Numerous size/concurrency limits exist. | Important as enhancement | ASP.NET rate limiting or APIM, partitioned by workspace; separate request quotas from LLM token/concurrency budgets. Do not assume hosted Honcho quotas are open-source server behavior. | `plastic-labs/honcho:sdks/python/src/honcho/http/client.py:23-26,147-164`; `plastic-labs/honcho:src/main.py:202-224`; `plastic-labs/honcho:src/config.py:1549-1558` |
| Migrations | Alembic migrations, staged data backfills and operational indexes. | Core operationally | EF migrations plus explicit SQL/backfill jobs. Fresh-install schema and import tooling need not replay every historical Honcho migration. | `plastic-labs/honcho:migrations/versions/a7c3e9f1b2d4_add_document_sources_table.py:1-12,36-80` |
| Python SDK | Sync client plus async access, typed resource wrappers, lazy resource access, automatic pagination, context formatting, file upload, streaming. | Important compatibility target | Reuse existing SDK against Nachos where contracts match; build a native C# SDK separately. | `plastic-labs/honcho:sdks/python/src/honcho/client.py:168-185,190-257`; `plastic-labs/honcho:sdks/python/src/honcho/pagination.py:52-68`; `plastic-labs/honcho:sdks/python/src/honcho/session_context.py:73-190` |
| TypeScript SDK | Async client/resources, base URL override, paging, streaming helpers and OpenAI/Anthropic context formatting. | Important compatibility target | Preserve transport contract; reuse for client/MCP integration testing. | `plastic-labs/honcho:sdks/typescript/src/client.ts:160-190`; `plastic-labs/honcho:sdks/typescript/src/http/streaming.ts:79-144`; `plastic-labs/honcho:sdks/typescript/src/session_context.ts:179-299` |
| MCP server | Extensive tools for workspace, peer, session, message, scope and conclusion operations; API URL configurable. | Optional for first server release | Initially run upstream MCP beside Nachos; later implement with the C# MCP SDK if desired. | `plastic-labs/honcho:mcp/src/config.ts:98-109,130-150`; `plastic-labs/honcho:mcp/src/server.ts:1-29` |
| CLI | Inspect/admin groups and local stack start/stop/status/setup/doctor. | Optional | Native .NET CLI or retain upstream CLI for server-facing operations; replace its local-stack launcher with Aspire/azd workflows. | `plastic-labs/honcho:honcho-cli/src/honcho_cli/main.py:67-75,85-100` |
| Hosted dashboard, org provisioning, billing | README describes hosted organization/dedicated-instance onboarding, but this is not the inspected core REST implementation. | Out of scope | Separate Nachos control plane only if product requirements justify it. | `plastic-labs/honcho:README.md:75-77`; `plastic-labs/honcho:src/main.py:213-221` |

---

## 4. Exact REST route inventory

### 4.1 Prefix notation

The following abbreviations expand to exact current paths:

```text
W = /v3/workspaces/{workspace_id}
P = W/peers/{peer_id}
S = W/sessions/{session_id}
M = S/messages
C = W/conclusions
Q = W/scopes/{scope_id}
H = W/webhooks
```

The API registers only the v3 application routers in current `main`.  
Source: `plastic-labs/honcho:src/main.py:213-221`.

### 4.2 Workspaces

| Method | Route | Purpose / notable response |
|---|---|---|
| POST | `/v3/workspaces` | Get or create workspace using body `id` |
| POST | `/v3/workspaces/list` | Paginated listing and filters |
| PUT | `W` | Update metadata/configuration |
| DELETE | `W` | Queue workspace deletion; **202** |
| POST | `W/search` | Hybrid message search |
| GET | `W/queue/status` | Reasoning queue status |
| POST | `W/schedule_dream` | Schedule `omni` or `card_refresh`; **204** |
| POST | `W/chat` | Workspace-wide dialectic answer or SSE |

Sources: `plastic-labs/honcho:src/routers/workspaces.py:36-112,115-195,228-234,290-309`.

### 4.3 Peers

| Method | Route | Purpose |
|---|---|---|
| POST | `W/peers/list` | Paginated peers; optional kind filter |
| POST | `W/peers` | Get or create peer |
| PUT | `P` | Update metadata/configuration |
| POST | `P/sessions` | Paginated sessions for peer |
| POST | `P/chat` | Peer/directional dialectic chat |
| POST | `P/representation` | Retrieve curated representation |
| GET | `P/card` | Retrieve card; optional `target` |
| PUT | `P/card` | Set card; optional `target` |
| GET | `P/context` | Representation and card |
| POST | `P/search` | Search messages from peer perspective |

Sources: `plastic-labs/honcho:src/routers/peers.py:81-120,167-239,423-436,576-618,648-658,793-813`.

**There is no current peer DELETE route and no conventional `GET P` resource route.** The get-or-create POST and listing are central to SDK behavior.

### 4.4 Sessions and membership

| Method | Route | Purpose / response |
|---|---|---|
| POST | `W/sessions/list` | Paginated sessions |
| POST | `W/sessions` | Get or create session |
| PUT | `S` | Update metadata/configuration |
| DELETE | `S` | Mark inactive and enqueue deletion; **202** |
| POST | `S/clone` | Clone, optionally through `message_id`; **201** |
| POST | `S/peers` | Add peers |
| PUT | `S/peers` | Replace/set peer membership |
| DELETE | `S/peers` | Remove peers using request body |
| GET | `S/peers` | Paginated membership |
| GET | `S/peers/{peer_id}/config` | Read membership configuration |
| PUT | `S/peers/{peer_id}/config` | Set membership configuration; **204** |
| GET | `S/context` | Token-budgeted context |
| GET | `S/summaries` | Short and long summaries |
| POST | `S/search` | Search session messages |

Sources: `plastic-labs/honcho:src/routers/sessions.py:267-309,376-463,479-490,517-530,563-577,605-651,672-716,1061-1078,1108-1121`.

### 4.5 Messages

| Method | Route | Purpose / response |
|---|---|---|
| POST | `M` | Add batch of messages; **201** |
| POST | `M/` | Explicit trailing-slash alias; **201** |
| POST | `M/upload` | Multipart file converted to messages; **201** |
| POST | `M/list` | Paginated message listing |
| GET | `M/{message_id}` | Fetch one message |
| PUT | `M/{message_id}` | Update message metadata |

Sources: `plastic-labs/honcho:src/routers/messages.py:95-115,182-196,305-321,342-394`.

There is no individual message DELETE route or public message-content edit operation in this router. The update schema accepts metadata only.  
Source: `plastic-labs/honcho:src/schemas/api.py:359-360`.

### 4.6 Conclusions

| Method | Route | Purpose / response |
|---|---|---|
| POST | `C` | Insert conclusions; **201** |
| POST | `C/list` | Paginated filtered listing |
| POST | `C/query` | Semantic conclusion search |
| GET | `C/{conclusion_id}` | Fetch conclusion |
| DELETE | `C/{conclusion_id}` | Delete/hide conclusion; **204** |

Sources: `plastic-labs/honcho:src/routers/conclusions.py:24-33,56-70,90-108,137-166`.

There are no current public `/collections` or `/documents` routers. Public memory records are exposed as conclusions.

### 4.7 Named scopes

| Method | Route | Purpose / response |
|---|---|---|
| POST | `W/scopes` | Get or create scope |
| POST | `W/scopes/list` | Paginated scopes |
| GET | `Q` | Fetch scope |
| POST | `Q/sessions` | Add existing sessions; **204** |
| DELETE | `Q/sessions/{session_id}` | Remove session; **204** |
| POST | `Q/sessions/list` | Paginated scope sessions |
| GET | `Q/status` | Backfill state |

Sources: `plastic-labs/honcho:src/routers/scopes.py:36-48,68-110,132-170,187-208`.

### 4.8 Keys, webhooks and operational endpoints

| Method | Route | Purpose |
|---|---|---|
| POST | `/v3/keys` | Mint scoped JWT; admin protected |
| POST | `H` | Get or create webhook endpoint |
| GET | `H` | Paginated webhook endpoints |
| DELETE | `H/{endpoint_id}` | Delete endpoint; **204** |
| GET | `H/test` | Emit a test webhook event |
| GET | `/health` | Basic health |
| GET | `/metrics` | Prometheus metrics |
| GET | `/deriver/metrics` | Internal scaling/backlog snapshot; hidden from OpenAPI |

Sources: `plastic-labs/honcho:src/routers/keys.py:17-35`; `plastic-labs/honcho:src/routers/webhooks.py:27-100`; `plastic-labs/honcho:src/main.py:223-230`; `plastic-labs/honcho:src/routers/deriver_metrics.py:21-43`.

FastAPI also supplies framework documentation/OpenAPI behavior; those are not domain-resource APIs.

### 4.9 Pagination, filters and body contracts

**Pagination**

- `page`: default `1`, minimum `1`.
- `size`: default `50`, minimum `1`, maximum `100`.
- Envelope: `items`, `total`, `page`, `size`, `pages`.
- Many POST list endpoints accept `reverse=false`.
- SDK iteration automatically fetches subsequent pages.

Sources: `plastic-labs/honcho:docs/v3/openapi.json:73-168`; `plastic-labs/honcho:sdks/python/src/honcho/pagination.py:24-68,88-131`.

**General filtering**

- Filters are JSON bodies, not exclusively query-string expressions.
- Field equality and operators include `gte`, `lte`, `gt`, `lt`, `ne`, `in`, `contains`, `icontains`.
- Nested metadata and logical composition are implemented.
- Allowed fields vary by resource.
- `source_ids` filtering has special provenance-edge handling.
- Message listing/search and conclusion listing/search do not share one universal filter whitelist.

Sources: `plastic-labs/honcho:src/utils/filter.py:32-79,427-550,587-678,796-842`.

**Limits and notable DTOs**

- Message batch: **1–100** messages.
- Default message content maximum: **25,000 characters**.
- Conclusion batch: **1–100**.
- Search result `limit` or `top_k`: generally **1–100**, default **10**.
- Chat query: **1–10,000 characters**.
- Default upload maximum: **5 MiB**.
- Session creation may include peers with membership configurations and named scopes.

Sources: `plastic-labs/honcho:src/schemas/api.py:328-383,406-434,659-723,731-741,803-811`; `plastic-labs/honcho:src/config.py:1549-1557`.

**Session context query parameters**

```text
tokens
summary
search_query
peer_target
peer_perspective
scope
sessions             repeated query parameter
limit_to_session
search_top_k
search_max_distance
include_most_frequent
max_conclusions
```

Important distinctions:

- Wire parameter is `summary`, not `include_summary`.
- `peer_perspective` requires `peer_target`.
- `scope` requires `peer_target`, excludes `peer_perspective`, and requires workspace/admin authority.
- `sessions` and `limit_to_session` narrow representation recall to explicit conclusions and omit unscopable peer cards.
- Long repeated `sessions` query strings may hit proxy/request-line limits before the logical 1,000-session cap.

Source: `plastic-labs/honcho:src/routers/sessions.py:716-785`.

**Chat/representation filtering is narrower than generic CRUD filtering:** the chat `filters` option supports session allowlisting, not arbitrary metadata expressions.  
Source: `plastic-labs/honcho:src/schemas/api.py:772-797`.

---

## 5. Background pipeline details

## 5.1 Ingestion → queue → explicit conclusions

### Current flow

1. Validate message batch and sender identities.
2. Get/create the session and participating peers.
3. Obtain a PostgreSQL transaction advisory lock for `(workspace, session)`.
4. Assign increasing `seq_in_session`.
5. Store messages and pending embedding-chunk rows.
6. Commit the message transaction.
7. Schedule background enqueue and bounded immediate embedding work.
8. Enqueue summary work where thresholds are reached.
9. Enqueue one representation work item for the observed sender with a list of observers.
10. Worker batches eligible representation messages.
11. One structured LLM call extracts explicit atomic facts.
12. Embed and deduplicate those facts, then save them into every applicable collection.

Sources: `plastic-labs/honcho:src/crud/message.py:418-461,465-499`; `plastic-labs/honcho:src/routers/messages.py:142-174`; `plastic-labs/honcho:src/deriver/enqueue.py:330-383`; `plastic-labs/honcho:src/deriver/deriver.py:159-246`.

### Prompt behavior

The v3 prompt emphasizes:

- Facts about the selected target peer.
- Speaker-tagged messages with timestamps.
- Other speakers used only to interpret the target’s statements.
- Explicit assent can incorporate details from a preceding question/proposal.
- Absolute dates where possible.
- One self-contained atomic fact per conclusion.
- No conversational trivia or rephrased duplicates.
- No requirement to produce a fixed number of observations.

Source: `plastic-labs/honcho:src/deriver/prompts.py:19-34,65-98`.

**Porting implication:** Prompt behavior is part of practical memory quality, but should be evaluated rather than assumed equivalent because a C# implementation sends similar text.

### Deduplication details

- Exact match keys include normalized content and level.
- For explicit facts, session identity is also part of the key.
- Semantic duplicate candidate threshold is cosine distance **0.05**.
- A token-set richness heuristic chooses whether a new near-duplicate replaces an existing fact.
- `times_derived` is used in retrieval as a frequency/corroboration signal.

Sources: `plastic-labs/honcho:src/crud/document.py:235-250,492-513,1413-1422`; `plastic-labs/honcho:src/crud/representation.py:382-409`.

**Nachos recommendation:** Preserve these rules for the compatibility baseline, but make duplicate/retry attribution explicit. A retry is not necessarily independent evidence.

---

## 5.2 Queue, work units and concurrency

Representative work-unit keys:

```text
representation:{workspace}:{session}:{observed}
summary:{workspace}:{session}:{observer}:{observed}
dream:{dream_type}:{workspace}:{observer}:{observed}
webhook:{workspace}
deletion:{workspace}:{deletion_type}:{resource_id}
reconciler:{reconciler_type}
scope_backfill:{workspace}:{scope_peer}:{session}
scope_removal:{workspace}:{scope_peer}:{session}
```

Source: `plastic-labs/honcho:src/utils/work_unit.py:44-84`.

Important implementation properties:

- Representation work excludes observer from the key because extraction is shared.
- Work-unit ownership is acquired by inserting into a table with a unique work-unit key and `ON CONFLICT DO NOTHING`.
- This is **not simply a generic `SELECT ... FOR UPDATE SKIP LOCKED` queue implementation**.
- Work within an owned unit is processed serially; distinct units can run concurrently.
- Worker ownership is checked during processing.
- Transient failures may release the unit for retry.
- Delivery/processing is explicitly **at least once**, not exactly once.
- The source acknowledges that fresh LLM re-derivation on retry can increase `times_derived` and telemetry counts.

Sources: `plastic-labs/honcho:src/deriver/queue_manager.py:421-445,561-585,637-703`.

Current default tuning includes:

- One worker.
- Representation eligibility around **512 accumulated tokens**.
- Batch target around **1,024 input tokens**.
- Maximum waiting age **1,800 seconds**.
- Explicit flush mode off.
- Stale-owner timeout **5 minutes**.
- Polling backoff and startup jitter.

Source: `plastic-labs/honcho:src/config.py:879-980`.

### Azure processing choice

| Choice | Recommendation |
|---|---|
| PostgreSQL queue + Worker Service | **Best MVP fit.** Preserves data locality, status queries and work-unit batching with few Azure resources. Use a transactional outbox to improve enqueue durability. |
| Azure Service Bus | Best later managed broker option. Use `SessionId` derived from the ordering/work-unit key, duplicate handling, lock renewal and a DB outbox. Keep queue-status data in PostgreSQL. |
| Azure Storage Queues | Cheap but requires custom ordering, claims, visibility-renewal and status aggregation. Less attractive for strict parity. |
| Durable Functions | Useful for durable timers or long consolidation orchestration. Do not execute nondeterministic LLM calls directly in replaying orchestrator code; keep them in activities. |
| Container Apps Jobs | Useful for migrations or scheduled maintenance; not necessarily the best host for always-active fine-grained queue processing. |

Service Bus sessions provide ordered processing and exclusive session ownership; they require Standard or Premium tiers. This aligns with work-unit serialization but does not reproduce Honcho’s token batching or database state automatically.  
Source: [Azure Service Bus message sessions](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sessions).

### Reliability improvement worth making immediately

Message storage commits before the API schedules background enqueue, and enqueue catches/logs failures. There is therefore a crash/failure window between durable message creation and durable reasoning-work insertion.  
Sources: `plastic-labs/honcho:src/crud/message.py:497-499`; `plastic-labs/honcho:src/routers/messages.py:142-161`; `plastic-labs/honcho:src/deriver/enqueue.py:55-80`.

**Recommendation:** Nachos should persist an outbox entry in the message transaction. This improves reliability without changing the external API.

---

## 5.3 Dialectic: answering questions about memory

### Peer-scoped flow

1. Authorize workspace/peer/session access.
2. Resolve observer, observed peer, applicable configuration and cards.
3. Initialize a reasoning-level-specific toolset.
4. Prefetch semantically relevant explicit and higher-order conclusions separately.
5. Optionally include session history.
6. Run bounded tool-assisted reasoning.
7. Produce plain text or structured JSON-string content.
8. Collect optional evidence.
9. Stream final synthesis if requested.

Sources: `plastic-labs/honcho:src/dialectic/chat.py:78-130`; `plastic-labs/honcho:src/dialectic/core.py:145-183,185-227,229-320,548-622`.

Tools include:

- `search_memory`
- `get_reasoning_chain`
- `get_observation_context`
- `search_messages`
- `grep_messages`
- `get_messages_by_date_range`
- `search_messages_temporal`

Source: `plastic-labs/honcho:src/dialectic/prompts.py:11-49`.

### Reasoning levels are configuration profiles

At the pinned revision:

| Level | Default maximum tool iterations | Other notable default |
|---|---:|---|
| `minimal` | 1 | 250 output tokens; reduced tools and prefetch |
| `low` | 5 | Automatic tool choice |
| `medium` | 2 | Provider/model configuration can differ |
| `high` | 4 | Provider/model configuration can differ |
| `max` | 10 | Largest default loop ceiling |

All current default profiles use the same configured default model, `gpt-5.4-mini`; operators can override each profile. **Do not infer a monotonically increasing tool-call count from the level name.**  
Source: `plastic-labs/honcho:src/config.py:1054-1090`.

Prefetch retrieves up to 10 conclusions from each category for minimal and 25 from each for other levels, separating explicit from deductive/inductive/contradiction records to reduce retrieval dilution.  
Source: `plastic-labs/honcho:src/dialectic/core.py:229-294`.

### Workspace chat is not merely peer chat with `peer_id=null`

It prefetches workspace scale, active peers and cards for orientation—not one flat semantic top-k over all conclusions. It then requires a recall tool, routes across relevant pairs, and adds three tool iterations for nonminimal profiles by default.  
Sources: `plastic-labs/honcho:src/dialectic/workspace.py:91-142,173-218`; `plastic-labs/honcho:src/config.py:1109-1113`.

### Streaming contract

Example wire framing:

```text
data: {"delta":{"content":"partial answer"},"done":false}

data: {"done":true,"evidence":{...}}
```

The terminal event need not contain `delta`. This is **not** the OpenAI Chat Completions stream schema. Intermediate tool execution is not the same as streaming model reasoning; the LLM abstraction supports tool loops followed by streaming the final answer.  
Sources: `plastic-labs/honcho:src/utils/sse.py:14-31`; `plastic-labs/honcho:src/llm/api.py:180-205`.

---

## 5.4 Summaries and session-context assembly

### Summarization

- Short summary every **20 messages** by default.
- Long summary every **60 messages** by default.
- Default output caps: **1,000** and **4,000** tokens.
- Thresholds are evaluated independently.
- If both are due, they are generated concurrently.
- Both prompts incorporate the prior summary and new messages.
- Short emphasizes concise factual coverage.
- Long adds detailed themes, context and apparent emotional/personality information.
- Stored summaries include coverage message identity, timestamp and token count.

Sources: `plastic-labs/honcho:src/config.py:1195-1227`; `plastic-labs/honcho:src/utils/summarizer.py:102-173,288-367`; `plastic-labs/honcho:src/schemas/api.py:461-475`.

### Context packing

The intended packing scheme is:

1. Determine the token budget.
2. Account for peer representation/card when included.
3. Allocate up to 40% of remaining budget to a fitting summary.
4. Use remaining tokens for messages after the summary’s coverage marker.
5. If no summary fits, return no summary and use message context instead.

Sources: `plastic-labs/honcho:src/routers/sessions.py:1013-1033`; `plastic-labs/honcho:src/utils/summarizer.py:884-958`.

**Important caveat:** The source subtracts representation/card tokens rather than strictly truncating those components first. Tests explicitly cover the representation exhausting the budget. Therefore the “entire context fits” description should not be interpreted as a fully proven hard cap for all combinations.  
Source: `plastic-labs/honcho:tests/routes/test_session_context_summary.py:64-95`.

**Nachos recommendation:** Provide deterministic budget partitions and a true final token-budget check. Document this as an intentional quality improvement rather than preserving every upstream edge case.

---

## 5.5 Dreaming and consolidation

### Automatic scheduling

Dream scheduling uses:

- New explicit-conclusion threshold.
- Idle delay.
- Minimum interval since prior dream.
- Pending/in-flight deduplication.
- Cancellation of pending idle dreams on renewed message activity.
- A due-work reconciliation path.

Defaults include **50 explicit documents**, **60 minutes idle**, and **8 hours between dreams**. Dream-generated conclusions do not count toward the explicit-document trigger, avoiding a self-amplifying loop.  
Sources: `plastic-labs/honcho:src/config.py:1357-1368`; `plastic-labs/honcho:src/dreamer/dream_scheduler.py:248-286,305-365`; `plastic-labs/honcho:src/deriver/enqueue.py:35-52`.

### `omni` dream

**Deduction specialist first**

- Survey recent facts, search memory/messages.
- Identify knowledge updates.
- Produce logically implied facts.
- Flag contradictions.
- Remove outdated observations.
- Refresh stable peer-card identity information.
- Require valid source references for derived facts.

**Induction specialist second**

- Find patterns across multiple explicit and deductive observations.
- Produce preferences, behaviors, tendencies, personality patterns or correlations.
- Require at least two source observations.
- Record confidence and pattern type.

Sources: `plastic-labs/honcho:src/dreamer/specialists.py:620-678,750-811`.

### Peer-card taxonomy

Current automatic card entries use four categories:

- `IDENTITY`
- `ATTRIBUTE`
- `RELATIONSHIP`
- `INSTRUCTION`

The card is intended for durable identity facts and explicitly stated standing instructions, not inferred personality traits or transient events. The prompt limits the card to 40 entries.  
Source: `plastic-labs/honcho:src/dreamer/specialists.py:76-132`.

### `card_refresh` dream

This specialist can search conclusions and update the card, but has no conclusion-create/delete tools. `rebuild=true` hides the previous card so removed information is not carried forward merely because it was already present.  
Source: `plastic-labs/honcho:src/dreamer/specialists.py:841-865,896-939`.

### Failure semantics

The dream processor catches errors and intentionally does not rethrow them, allowing the queue item to be marked processed. Webhook delivery similarly logs failed deliveries without a durable per-endpoint retry loop in the inspected function.  
Sources: `plastic-labs/honcho:src/dreamer/orchestrator.py:658-665`; `plastic-labs/honcho:src/webhooks/webhook_delivery.py:58-78`.

**Nachos recommendation:** Introduce explicit failed/partial/retryable states. Do not accidentally equate “queue item processed” with “consolidation succeeded.”

---

## 5.6 Scope maintenance and visibility

A single named scope represents knowledge accumulated by a special observer from a defined set of sessions.

- New messages fan out through ordinary observer processing.
- Adding an existing session copies explicit conclusions into scope collections, avoiding LLM re-extraction.
- Removing a session soft-deletes its copied facts and dependent derived conclusions.
- Removal schedules card rebuild and reconsolidation.
- Scope membership has backfill status.
- A list of scopes is interpreted as a session union, not necessarily a merged higher-order representation.
- Explicit session allowlists intentionally exclude cross-session derived conclusions where attribution cannot be proven.

Sources: `plastic-labs/honcho:src/crud/scope.py:1-15`; `plastic-labs/honcho:src/deriver/scope_backfill.py:1-24`; `plastic-labs/honcho:src/schemas/api.py:786-797`; `plastic-labs/honcho:src/routers/sessions.py:743-763`.

The tests specifically verify single-scope collection selection, empty-scope fail-closed behavior and workspace-chat allowlisting.  
Source: `plastic-labs/honcho:tests/routes/test_scope_reads.py:305-343,390-403,521-537`.

---

## 6. Recommended .NET and Azure architecture

### 6.1 Runtime and project structure

**Use .NET 10 rather than starting a new project on .NET 9.** At the research date, Microsoft lists .NET 10 as LTS through November 2028, while .NET 9 is in maintenance and reaches end of support in November 2026.  
Source: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

Suggested logical projects:

```text
Nachos.Api
Nachos.Worker
Nachos.Domain
Nachos.Application
Nachos.Persistence.Postgres
Nachos.AI
Nachos.Contracts.HonchoV3
Nachos.Client
Nachos.AppHost
Nachos.ServiceDefaults
```

These are proposed boundaries, not a requirement for a large initial solution.

### 6.2 API implementation

Use ASP.NET Core route groups or controllers with:

- Explicit snake_case JSON contract configuration.
- Dedicated DTOs matching Honcho aliases.
- Compatible validation responses.
- JWT authorization policies.
- Multipart uploads.
- Direct SSE writing.
- Request cancellation propagated into provider calls.
- OpenAPI generation and contract snapshot tests.

**Avoid using EF entities as API responses.** Honcho’s public IDs, field aliases and internal metadata boundaries are sufficiently different that convention-based serialization is risky.

### 6.3 Persistence

Recommended starting point:

- PostgreSQL Flexible Server.
- Npgsql/EF Core.
- JSONB for metadata and configuration.
- pgvector cosine HNSW indexes.
- Transactional message sequence allocation.
- Explicit provenance edges.
- Durable work/outbox tables.
- Fresh-install migrations plus separately versioned Honcho import tooling.

Azure requires allowlisting the extension as `vector`, then installing it in the database.  
Source: [Enable pgvector on Azure PostgreSQL](https://learn.microsoft.com/en-us/azure/postgresql/flexible-server/how-to-use-pgvector).

### 6.4 AI abstraction

Use `IChatClient` and `IEmbeddingGenerator`, but keep a Nachos-specific layer for:

- Reasoning profiles.
- Provider-specific reasoning/thinking parameters.
- Tool-loop iteration ceilings.
- Prompt caching.
- Structured output support detection.
- Stream-final-only behavior.
- Token accounting.
- Fallback selection and retry ownership.
- Evidence and trace capture.
- Provider request/response metadata necessary for replaying tool history.

Microsoft.Extensions.AI provides shared chat/embedding interfaces, tool invocation, caching and telemetry infrastructure; it does not by itself define Honcho’s behavioral semantics.  
Sources: [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai); [Azure OpenAI with `IChatClient`](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-chat-app).

### 6.5 Authentication and secrets

Use two deliberately separated authentication modes:

1. **Honcho-compatible bearer JWT**
   - HS256 compatibility claims: `ad`, `w`, `p`, `s`, `t`, optional `exp`.
   - Supports unchanged SDK usage with Nachos-issued keys.
2. **Entra ID**
   - Suitable for Azure services, managed identities and enterprise clients.
   - Map token identity/roles to authorized workspaces and operations.
   - Do not infer Honcho peer identity directly from Entra subject without a policy.

Store signing secrets and non-Azure provider credentials in Key Vault. Prefer Managed Identity for Azure resources where supported.

Honcho’s narrowest-scope authorization rule is essential: a token containing workspace and peer scope must not regain full workspace access just because the workspace claim matches.  
Source: `plastic-labs/honcho:src/security.py:214-276`.

### 6.6 Observability

Recommended signals:

- API latency/error counts.
- LLM and embedding latency, usage and estimated cost.
- Queue age, eligible work units and ownership count.
- Extraction batches and conclusions produced.
- Deduplication/replacement rates.
- Summary freshness and coverage.
- Dream success/partial failure.
- Scope backfill progress.
- Webhook retries/dead letters.
- Deletion backlog.
- Embedding dimension/configuration mismatch.

Use OpenTelemetry to Azure Monitor/Application Insights; retain optional Prometheus exposition. Keep prompt/message payload logging off by default, with redaction and explicit retention controls.  
Source: [Azure Monitor OpenTelemetry](https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-enable).

### 6.7 Aspire, azd and Bicep

Local development:

- API.
- Worker.
- pgvector PostgreSQL container.
- Optional Redis.
- Mock AI provider.
- Aspire dashboard.

Azure deployment:

- API Container App with ingress.
- Worker Container App without public ingress.
- PostgreSQL Flexible Server.
- Container Registry.
- Key Vault.
- Azure OpenAI/Foundry configuration.
- Application Insights/Log Analytics.
- Optional Service Bus, Redis or Blob Storage.
- Separate migration execution.

Aspire supports Container Apps provisioning, and azd can generate reviewable Bicep. Pin the Aspire/azd toolchain because deployment integration details evolve.  
Sources: [Aspire Container Apps configuration](https://aspire.dev/integrations/cloud/azure/configure-container-apps/); `dotnet/docs-aspire:docs/deployment/azd/aca-deployment-azd-in-depth.md:11-19,45-60,204-214`.

**Do not deploy the local PostgreSQL container as the production database merely because a development AppHost contains it.** Explicitly provision or bind a managed PostgreSQL resource.

---

## 7. Can existing Honcho SDKs talk to Nachos unchanged?

### Short answer

**Yes, potentially—by changing the configured base URL and credentials—but only if Nachos reproduces the appropriate HTTP contract.** No Python implementation is intrinsically required server-side.

Both SDKs accept a custom base URL. Their routes include `/v3`, so configure the **server origin/root**, not an origin already ending in `/v3`.  
Sources: `plastic-labs/honcho:sdks/python/src/honcho/client.py:190-257`; `plastic-labs/honcho:sdks/python/src/honcho/http/routes.py:1-12`; `plastic-labs/honcho:sdks/typescript/src/client.ts:160-190`.

### Compatibility requirements

| Contract area | Required work |
|---|---|
| Paths/methods | Preserve POST-based listing/get-or-create, PUT updates, DELETE bodies, exact underscore names and route prefixes. |
| JSON | Preserve snake_case fields, ID aliases, nullable/omitted fields, array versus object responses, and JSON-string structured chat content. |
| Pagination | Return `items,total,page,size,pages`; support page/size bounds and ordering. |
| Filters | Implement resource-specific filter semantics, nested metadata handling and session-allowlist restrictions. |
| Resource creation | Match get-or-create behavior, including SDK lazy initialization and any update-on-existing semantics. |
| Errors | Match `{"detail": ...}`-style server errors and relevant HTTP statuses. Default ASP.NET ProblemDetails/400 validation is not automatically equivalent to FastAPI behavior. |
| Auth | Accept Nachos-issued compatible keys. Existing hosted Honcho keys do not become valid merely because the URL changes. |
| Streaming | Emit Honcho SSE frames, including terminal evidence behavior. |
| Uploads | Match multipart field names and JSON-encoded form values. |
| Retries | SDKs retry 429 and selected 5xx/network failures; support `Retry-After`. Consider duplicate writes under retries. |
| Consistency | Messages may exist before embeddings/conclusions/summaries. Preserve or clearly improve eventual-consistency semantics. |
| Scopes | Match single-scope versus scope-list behavior, reserved namespace and authorization boundaries. |
| Versions | Current SDK package major version does not identify API major version. Test actual route constants. |

Relevant source anchors: `plastic-labs/honcho:sdks/python/src/honcho/http/client.py:119-191`; `plastic-labs/honcho:sdks/python/src/honcho/pagination.py:24-68`; `plastic-labs/honcho:src/utils/sse.py:14-31`; `plastic-labs/honcho:src/schemas/api.py:958-987`.

### Conformance strategy

Run the existing Python and TypeScript SDK integration suites against Nachos and add language-independent golden HTTP tests for:

1. Get/create/list/update resource behavior.
2. JSON aliases and date serialization.
3. Metadata filters and numeric/string coercion.
4. All JWT scope combinations.
5. Membership-based read permission.
6. Context packing and summary coverage.
7. Streaming under arbitrary byte boundaries.
8. Structured responses and evidence.
9. File upload validation/chunking.
10. Deletion and eventual visibility.
11. Scope membership additions/removals.
12. Retried writes and worker restarts.

The upstream tests already encode valuable boundary decisions: member-read authorization is an explicit route allowlist, not inferred from HTTP verbs.  
Source: `plastic-labs/honcho:tests/routes/test_auth_route_policy.py:21-44,71-108`.

**Important distinction:** Passing CRUD/SDK transport tests proves wire compatibility, not memory-quality parity.

---

## 8. MCP and SDK ergonomics worth preserving

### SDK use pattern

The current examples follow this shape:

1. Create a client/workspace.
2. Obtain peer wrappers.
3. Obtain a session.
4. Construct messages using peer helpers.
5. Add messages.
6. Retrieve token-limited context.
7. Format context for a model provider.

Sources: `plastic-labs/honcho:sdks/python/examples/get_context.py:4-31`; `plastic-labs/honcho:sdks/typescript/examples/get_context.ts:12-44`.

For a native C# SDK, preserve these ideas without imitating dynamic-language syntax:

- Typed resource handles.
- `Task`-based calls.
- `IAsyncEnumerable<T>` pagination.
- `IAsyncEnumerable<string>` streaming.
- Cancellation tokens everywhere.
- Strong configuration and filter builders.
- Explicit `GetOrCreateAsync` semantics where useful.
- Context conversion helpers compatible with `ChatMessage`.
- Evidence and provenance types.
- Queue-drain/status helpers with timeouts.

### MCP tool coverage

The inspected MCP server exposes tools including:

- Workspace inspection/list/create/search/chat/metadata.
- Peer create/list/chat/card/context/representation.
- Session create/list/delete/clone/membership/inspection.
- Message add/list/fetch.
- Conclusion list/query/get/derived/create/delete.
- Scope create/list/membership/status/session listing.
- Dream scheduling and queue status.

Sources: `plastic-labs/honcho:mcp/src/tools/workspace.ts:36-178,325-447`; `plastic-labs/honcho:mcp/src/tools/peers.ts:7-89,178-316`; `plastic-labs/honcho:mcp/src/tools/sessions.ts:14-166,225-334,390-467`; `plastic-labs/honcho:mcp/src/tools/conclusions.ts:16-18,99-101,149-151,190-192,238-240,295-297`; `plastic-labs/honcho:mcp/src/tools/scopes.ts:34-36,74-76,117-119,154-156,186-188,227-229`; `plastic-labs/honcho:mcp/src/tools/system.ts:7-9,52-54`.

**Practical recommendation:** Reusing the upstream MCP server against Nachos is a strong early compatibility test and avoids porting two servers simultaneously.

---

## 9. Recommended MVP scope versus later phases

### Phase 0 — Contract and evaluation baseline

Before implementation:

- Freeze the target contract at a specific v3 commit/release.
- Record intentional differences from upstream.
- Resolve licensing strategy.
- Build HTTP contract fixtures.
- Choose a small memory-quality evaluation set.
- Define tenant boundaries and deletion guarantees.
- Select initial LLM and embedding deployments.
- Establish token-budget and cost limits.

### Phase 1 — Useful memory-server MVP

**Include**

- Workspaces, peers, sessions, memberships and messages.
- Public/internal metadata separation.
- Observe flags and configuration hierarchy.
- PostgreSQL/pgvector persistence.
- Transactional message ingestion and durable outbox.
- Explicit-fact deriver.
- Exact/semantic conclusion dedup.
- Conclusion CRUD/query.
- Hybrid message search.
- Working representations.
- Peer-context and session-context endpoints.
- Short/long summaries.
- Basic peer chat with a bounded retrieval toolset.
- Compatible authentication and tenant scoping.
- Queue status.
- Session/workspace/conclusion deletion.
- Azure Container Apps deployment.
- OTel, health checks and queue-age monitoring.

**Include at least a lightweight card refresh** if peer cards are presented as supported. Otherwise expose the direct card API and clearly label automatic card generation as deferred.

**Do not claim full Honcho parity:** deeper cross-session reasoning is materially reduced without dreams.

### Phase 2 — Current-v3 functional parity

Add:

- Full deduction/induction dreams.
- Contradiction and knowledge-update handling.
- Automatic dream scheduling and card rebuild.
- Named scopes, backfill/removal and visibility tests.
- Workspace chat.
- All reasoning profiles.
- Structured responses and evidence.
- Exact SSE streaming.
- File uploads.
- Webhooks with improved durable delivery.
- Session cloning.
- Full Python/TypeScript conformance suite.
- Upstream MCP compatibility.

### Phase 3 — Production hardening and optional scale features

Add as evidence justifies:

- Service Bus-backed scheduling.
- Per-tenant budgets, fairness and quotas.
- Stronger erasure/provenance cascade semantics.
- Multi-region disaster recovery.
- Import/export and migration tools.
- Azure AI Search adapter.
- Redis caching.
- Enterprise Entra integration.
- Native .NET SDK/CLI/MCP.
- Evaluation dashboard.
- Surprisal-based prioritization.
- Historical v2 compatibility layer.

### Explicitly defer or exclude

- Hosted Honcho billing and organization provisioning.
- Historical App/User APIs unless a client requires them.
- Recreating every upstream vector backend.
- Replaying every legacy Alembic migration for a clean installation.
- Proprietary/dashboard-specific control-plane behavior not represented in the inspected server.

---

## 10. Risks and open questions

### 10.1 Licensing is a first-order decision

Honcho is AGPL-3.0. A close code or prompt translation requires a deliberate licensing analysis; a language change is not a licensing boundary. Decide whether Nachos is an AGPL-compatible derivative, uses separately licensed components, or follows another legally reviewed approach.  
Source: `plastic-labs/honcho:README.md:719-721`.

### 10.2 “Latest main” is ahead of the release

This report pins `main`, not merely the `v3.2.2` tag. The version string can remain `3.2.2` while behavior changes after release. Freeze a commit for compatibility tests and independently choose whether production targets a released tag.

### 10.3 Prompt and model behavior cannot be ported mechanically

Current defaults use `gpt-5.4-mini` across major reasoning subsystems and `text-embedding-3-small` for embeddings. Azure deployment names, availability and quotas may differ. Provider model substitutions require quality evaluation, not just schema validation.  
Source: `plastic-labs/honcho:src/config.py:814-823,925-934,1054-1090,1206-1213,1374-1394`.

### 10.4 Tokenization affects both behavior and correctness

Honcho uses `o200k_base` for general estimation, while embeddings use model-specific tiktoken encoding with `cl100k_base` fallback. Embedding chunks overlap by 20%. Tokenization influences context budgets, batching, chunk IDs and semantic duplicate decisions.  
Sources: `plastic-labs/honcho:src/utils/tokens.py:11-23`; `plastic-labs/honcho:src/embedding_client.py:267-270,675-707`; `plastic-labs/honcho:src/crud/document.py:1413-1422`.

Choose a .NET tokenizer implementation only after corpus-level comparison, including Unicode and multilingual text.

### 10.5 Vector substitutions change semantics

Azure AI Search/Cosmos ranking, filters, distance representation, candidate truncation and consistency are not inherently equivalent to pgvector + PostgreSQL FTS + RRF. Even equal vector dimensions do not make embeddings from different models comparable.

Honcho explicitly validates configured dimensions against physical storage at startup. Preserve that protection.  
Source: `plastic-labs/honcho:src/startup/embedding_validator.py:1-11`; `plastic-labs/honcho:src/main.py:136-140`.

### 10.6 Scope visibility requires defense beyond SQL filters

A peer card is a cross-session aggregate. Current session context and workspace chat omit unscopable cards under allowlists. The source also explicitly notes a remaining peer-chat card-scoping inconsistency; the peer chat preparation code loads cards without conditioning on its session allowlist.

This is a parity-risk observation from source, not a completed security assessment. Nachos should use one centralized visibility policy for all prompt components.  
Sources: `plastic-labs/honcho:src/routers/sessions.py:987-1007`; `plastic-labs/honcho:src/dialectic/workspace.py:118-132`; `plastic-labs/honcho:src/dialectic/chat.py:104-125`.

### 10.7 Deleting sessions does not erase all learned knowledge

Session deletion removes session-associated documents. Derived cross-session conclusions can have no owning session and survive. Peer cards also require an explicit erasure policy.

Furthermore, user documentation says there is “no soft delete,” while the implementation does use internal conclusion tombstones before reconciliation/hard deletion. The statements are reconcilable only as “no user-facing restore,” not “no internal soft-delete state.”  
Sources: `plastic-labs/honcho:src/crud/session.py:724-755`; `plastic-labs/honcho:src/crud/document.py:992-1027`; [Deleting data documentation](https://honcho.dev/docs/v3/documentation/features/advanced/deleting-data.md).

### 10.8 Scope removal and session deletion are not interchangeable

Scope removal explicitly cascades unsupported derived facts and rebuilds cards. Ordinary session deletion follows different logic. Do not assume one generic cascade captures both semantics.  
Source: `plastic-labs/honcho:src/deriver/scope_backfill.py:5-20`.

### 10.9 Work completion and success are different

- Queue retries are at least once.
- Dream failures may be acknowledged as processed.
- Webhook delivery lacks durable per-endpoint retries in the inspected path.
- Public queue status omits deletion and reconciliation.
- A 202 deletion response is acceptance, not completion.

Sources: `plastic-labs/honcho:src/deriver/queue_manager.py:561-585`; `plastic-labs/honcho:src/dreamer/orchestrator.py:658-665`; `plastic-labs/honcho:src/webhooks/webhook_delivery.py:58-78`; `plastic-labs/honcho:src/routers/workspaces.py:206-213`.

Nachos should make operational failure and freshness visible rather than reproduce silent ambiguity.

### 10.10 Authentication parity versus Azure-native convenience

Entra authentication alone cannot reproduce Honcho’s peer/session scopes. Conversely, preserving HS256 compatibility alone is not an enterprise identity integration. Both layers need explicit design.

### 10.11 Rate limits remain partially unverified

The inspected open-source startup/configuration does not establish managed-service request quotas. SDK retry support is verified; hosted quota policy is not. Treat Azure quota management and inbound tenant limits as Nachos product requirements rather than inferred Honcho parity.

### 10.12 Structured-output guarantees are weaker than they may appear

Some JSON Schema constraints are documented as hints rather than enforced validations. Decide whether Nachos should match this or provide stronger validation with a clearly documented difference.  
Source: `plastic-labs/honcho:src/schemas/api.py:813-820`.

### 10.13 Multi-tenancy and hosted organization isolation differ

Workspace-level isolation is visible in the server. Hosted organization-level dedicated instances are described in the README but their provisioning/authentication control plane was not established from the core source. Do not conflate these two tenancy layers.  
Sources: `plastic-labs/honcho:src/models.py:104-155`; `plastic-labs/honcho:README.md:75-77`.

### 10.14 Research boundaries

- Implementation, schema, migration, SDK, MCP, examples and selected tests were inspected directly.
- This was static research, not a live server conformance run.
- No claims are made that upstream tests were executed.
- Historical comparison concentrated on v2.5.1’s routing and deriver, not every historical release.
- Hosted dashboard/control-plane implementation was not inspected.
- Azure deployment recommendations were checked against Microsoft/Aspire documentation, but no infrastructure was provisioned.

---

## 11. Sources

### Upstream repository and pinned revisions

- [Honcho repository](https://github.com/plastic-labs/honcho)
- [Pinned main revision](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16)
- [v3.2.2 release](https://github.com/plastic-labs/honcho/releases/tag/v3.2.2)
- [v2.5.1 historical revision](https://github.com/plastic-labs/honcho/tree/a42252867fff80695abedca347eee397fefc7d44)

### Core implementation

- [Models](https://github.com/plastic-labs/honcho/blob/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/models.py)
- [Public schemas](https://github.com/plastic-labs/honcho/blob/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/schemas/api.py)
- [Configuration schemas](https://github.com/plastic-labs/honcho/blob/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/schemas/configuration.py)
- [Runtime configuration](https://github.com/plastic-labs/honcho/blob/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/config.py)
- [REST routers](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/routers)
- [OpenAPI v3](https://github.com/plastic-labs/honcho/blob/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/docs/v3/openapi.json)
- [Authentication](https://github.com/plastic-labs/honcho/blob/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/security.py)

### Reasoning and background processing

- [Deriver](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/deriver)
- [Dialectic](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/dialectic)
- [Dreamer](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/dreamer)
- [Summarizer](https://github.com/plastic-labs/honcho/blob/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/utils/summarizer.py)
- [Agent tools](https://github.com/plastic-labs/honcho/blob/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/utils/agent_tools.py)
- [Search](https://github.com/plastic-labs/honcho/blob/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/utils/search.py)
- [LLM abstraction](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/llm)
- [Vector stores](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/vector_store)
- [Reconciliation](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/src/reconciler)

### Integrations and verification material

- [Python SDK](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/sdks/python)
- [TypeScript SDK](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/sdks/typescript)
- [MCP server](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/mcp)
- [CLI](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/honcho-cli)
- [Tests](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/tests)
- [Migrations](https://github.com/plastic-labs/honcho/tree/e8d8b4a5aa770f9b3f3993b88a5f3aac4c944d16/migrations)

### Documentation and Azure references

- [Honcho documentation](https://honcho.dev/docs/v3/documentation/introduction/overview)
- [Honcho documentation index](https://honcho.dev/docs/llms.txt)
- [Honcho deletion semantics](https://honcho.dev/docs/v3/documentation/features/advanced/deleting-data.md)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Azure OpenAI chat quickstart](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-chat-app)
- [Azure PostgreSQL pgvector](https://learn.microsoft.com/en-us/azure/postgresql/flexible-server/how-to-use-pgvector)
- [Azure Service Bus sessions](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sessions)
- [Azure Monitor OpenTelemetry](https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-enable)
- [Aspire Container Apps integration](https://aspire.dev/integrations/cloud/azure/configure-container-apps/)
- [Aspire/azd in-depth deployment guide](https://github.com/dotnet/docs-aspire/blob/main/docs/deployment/azd/aca-deployment-azd-in-depth.md)

---

## Bottom line

**Build Nachos as a v3-compatible memory server with PostgreSQL/pgvector and a durable .NET worker, then layer Azure-native services where they demonstrably improve operations.**

The highest-risk omissions in another mapping would be:

1. Treating v2’s reasoning-heavy deriver as current v3 behavior.
2. Missing scopes and their provenance-sensitive visibility rules.
3. Treating peer cards as ordinary session-local metadata.
4. Replacing hybrid PostgreSQL search with generic vector search.
5. Assuming queue processing is exactly once.
6. Assuming session deletion erases all learned knowledge.
7. Assuming SDK version 2.x means API v2.
8. Ignoring the precise SSE and structured-response contracts.
9. Treating Entra authentication as a replacement for domain scoping.
10. Claiming full Honcho parity without dreaming and memory-quality evaluation.
