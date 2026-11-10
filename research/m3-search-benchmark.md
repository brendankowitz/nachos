# M3 search benchmark (R1)

Plan: [M3 Task 10](../docs/superpowers/plans/2026-11-08-nachos-m3-recall.md). Risk: design spec §21 R1 ("benchmark exact
search at 1M messages/workspace in M3"). Harness: [`test/Nachos.Benchmarks`](../test/Nachos.Benchmarks/README.md).

**Decision: exact workspace vector search misses the 1 s budget at 1M messages (W/search p95 5.7 s), so a workspace
search's vector half is now capped by `Nachos:Search:MaxVectorCandidates` (default 100,000 chunks, most recently stored
first). Session and peer searches stay exact.** ANN (spec §7.4, M7) remains the real fix for the vector half. Details
under [Decision](#decision).

## Host

| | |
| --- | --- |
| CPU | Intel Core i7-14700K, 20 cores / 28 threads |
| Host RAM | 63.7 GB, Windows 11 Pro (build 26300) |
| Docker | Docker Desktop 28.5.1 on WSL 2 (31 GB, 28 CPUs to the VM); data VHDX on C: (106 GB free) |
| SQL Server | 2025 CU9 (17.0.5005.3) with Full-Text Search, the `eng/docker/mssql-fts` image |
| Memory | container capped at 12 GB; `MSSQL_MEMORY_LIMIT_MB` 10,240, so a ~9.2 GB memory target |
| Client | .NET 10.0.14, the benchmark process on the same host, over TCP to the container |

## Method

- **Data.** One workspace of 1,000 sessions and 50 peers (two peers per session), made through `INachosClient`, so the
  workspace, peer, session and membership rows are exactly what the product writes. Messages and chunks are then
  bulk-copied into `dbo.Messages` and `dbo.MessageEmbeddings` in batches of 10,000 (about 5,500 messages/s), with the
  foreign keys and checks enforced. Messages are 30 to 70 made-up words drawn from a 5,000-word Zipf vocabulary,
  round-robin over the sessions (1,000 per session at 1M), one second apart. Half have one chunk and half two (1.5 chunks
  per message on average), each with a synced, unit-norm random 1536-dimension vector. A second workspace holds 2,000
  messages, to measure how much of a full-text query's candidate set comes from other workspaces.
- **Steps.** 100k, 250k, 500k and 1M messages in the main workspace, each step adding to the last. Before each step's
  measurements the full-text index is waited on until it has no pending changes.
- **Paths.** One database, two service containers: one runs the hosted full-text maintenance and searches with
  `CONTAINSTABLE`; the other never starts it and searches with the `LIKE` fallback, exactly as a server without
  Full-Text Search does. Every full-text series first asserts that the index is ready.
- **Series.** Sequential queries (one at a time) after two warm-ups. `W/search` and `S/search` run the plan's 200
  queries; every other series stops after 60 s once it has run 10, so its `n` is shown. `limit = 10`, so each retrieval
  takes 40. p50 and p95 are nearest-rank. A query that hit the 30 s command timeout would count as 30,000 ms; none did.
  A series of fewer than 50 queries is marked *indicative*. Before each series the plan cache is cleared, and the
  slowest cached statement afterwards gives the logical reads, CPU time and plan shape quoted below.
- **Queries.** End-to-end queries are three to five words from the corpus distribution. Lexical store series use one
  rare word; three words with one common word (in roughly half of the messages); or eight words with one common word.
  Vector series use random unit vectors.

The runs: the 100k step comes from a first run that the Docker engine ended during its 250k step (the engine restarted
and took the container with it; no OOM, the container stayed under its cap). The 250k, 500k and 1M steps come from a
complete second run. The capped series come from a third run at 1M, and the shipped configuration from a fourth. These
were the only Docker workloads on the host.

## Results

### Data size

| Messages | Chunks | `MessageEmbeddings` | `Messages` |
| ---: | ---: | ---: | ---: |
| 100k | 153k | 1.2 GB | 107 MB |
| 250k | 378k | 3.0 GB | 252 MB |
| 500k | 753k | 6.0 GB | 482 MB |
| 1M | 1.50M | 12.0 GB | 954 MB |

A `VECTOR(1536)` value is 6 KB, so each chunk row takes about one 8 KB page. At 1M the chunks no longer fit the 9.2 GB
buffer pool, and a workspace vector search reads from disk.

### End to end (`INachosClient`, vector and lexical concurrently, fused), p50 / p95 ms

| Series | 100k | 250k | 500k | 1M |
| --- | ---: | ---: | ---: | ---: |
| `W/search`, full text | 309 / 354 | 514 / 663 | 872 / 947 | **4,402 / 5,660** |
| `W/search`, `LIKE` | 294 / 339 | 648 / 770 | 1,285 / 2,130 | **4,315 / 4,864** |
| `S/search`, full text | 82 / 160 | 73 / 123 | 426 / 589 | 212 / 228 |
| `S/search`, `LIKE` | 48 / 50 | 48 / 52 | 51 / 54 | 205 / 214 |
| `P/search`, full text | 95 / 138 | 229 / 322 | 412 / 451 (n 147) | 576 / 678 (n 104) |
| `P/search`, `LIKE` | 87 / 99 | 186 / 227 | 367 / 405 (n 163) | 542 / 654 (n 111) |

All `W/search` and `S/search` series ran 200 queries, with no timeouts.

### Vector retrieval alone (store, take 40), p50 / p95 ms

| Scope | 100k | 250k | 500k | 1M |
| --- | ---: | ---: | ---: | ---: |
| workspace (exact) | 157 / 183 | 303 / 338 (n 195) | 562 / 597 (n 106) | 3,820 / 4,136 (n 16, indicative) |
| session | 48 / 50 | 49 / 53 | 52 / 55 | 84 / 92 |
| peer (about 40 of 1,000 sessions) | 91 / 168 | 171 / 184 | 329 / 441 (n 176) | 499 / 598 (n 123) |

Workspace search is a parallel (DOP 28) scan of every chunk: 1.63M logical reads and 15.5 s of CPU per query at 1M.

### Lexical retrieval alone (store, take 40, workspace scope), p50 / p95 ms

| Terms | Path | 100k | 250k | 500k | 1M |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 rare | full text | 8 / 13 | 4 / 6 | 6 / 10 | 13 / 31 |
| 3, one common | full text | 176 / 218 | 152 / 176 | 211 / 261 | 355 / 476 (n 161) |
| 8, one common | full text | 323 / 402 (n 184) | 188 / 214 | 265 / 321 | 409 / 577 (n 140) |
| 1 rare | `LIKE` | 30 / 37 | 42 / 46 | 72 / 132 | 113 / 128 |
| 3, one common | `LIKE` | 103 / 131 | 227 / 285 | 501 / 630 (n 117) | 1,010 / 1,339 (n 58) |
| 8, one common | `LIKE` | 195 / 265 | 529 / 671 (n 113) | 1,164 / 1,344 (n 53) | 2,369 / 3,233 (n 26, indicative) |
| 1 common, 2,000-message workspace | full text | 6 / 7 | 6 / 7 | 8 / 12 | 7 / 8 |
| 1 common, 2,000-message workspace | `LIKE` | 13 / 16 | 12 / 15 | 12 / 15 | 13 / 16 |

`CONTAINSTABLE` candidate volume at 1M (all workspaces / main / small): one common word 323,675 / 323,056 / 619;
three words 332,753 / 332,122 / 631; eight words 363,624 / 362,931 / 693; one rare word 1,255 / 1,252 / 3.

### Reconciler tick

`ListUnchunkedMessagesAsync(500)` with nothing left to chunk (the steady state): 51 / 59 ms at 100k, 123 / 125 at 250k,
256 / 260 at 500k, 548 / 560 at 1M. The plan is a full scan of `PK_Messages` merged against
`UQ_MessageEmbeddings_Message_Chunk` (97k logical reads at 1M), linear in the number of messages.

### Capped vector search (1M messages, third run)

Store-level workspace vector search with a newest-first chunk cap, p50 / p95 ms:

| Cap (chunks) | p50 / p95 | Logical reads | Messages covered (~1.5 chunks each) |
| ---: | ---: | ---: | ---: |
| none (exact) | 3,782 / 4,231 (n 16, indicative) | 1.63M | all 1M |
| 50,000 | 302 / 321 | 0.42M | ~33k newest |
| 100,000 | 492 / 519 (n 122) | 0.83M | ~67k newest |
| 200,000 | 996 / 1,020 (n 61) | 1.33M | ~133k newest |
| 400,000 | 1,713 / 2,242 (n 32, indicative) | 2.57M | ~267k newest |

The capped plan is a serial (DOP 1) backward scan of `PK_MessageEmbeddings` that stops at the cap: about 5 ms per
thousand chunks. The exact plan runs at DOP 28, which makes it cheaper per chunk but saturates the server.

The third run's binary applied the cap to every scope. It showed the cap is harmful outside workspace scope. Peer search
rose from 576 / 678 ms (uncapped, second run) to 1,568 / 2,696 ms (n 37, indicative): the backward scan tests peer
membership row by row and, with only about 60k chunks in the peer's scope, never reaches the cap, so it reads all 1.5M
chunks instead of seeking the peer's sessions. The shipped code therefore caps workspace scope only.

With the cap at 200,000 on every scope, `W/search` was 2,161 / 3,139 ms (full text) and 2,115 / 2,687 ms (`LIKE`). Its
slowest statement was the capped vector query (about 1 s, serial). The lexical query, at DOP 28, ran beside it on the
same server, so the two halves contend for CPU.

### Shipped configuration (1M messages, fourth run)

The shipped code caps workspace search at 100,000 chunks and leaves session and peer search exact. p50 / p95 ms, 200
queries unless noted:

| Series | Exact (second run) | Shipped |
| --- | ---: | ---: |
| `W/search`, full text | 4,402 / 5,660 | **1,474 / 2,429** |
| `W/search`, `LIKE` | 4,315 / 4,864 | **1,741 / 2,947** |
| `S/search`, full text | 212 / 228 | 231 / 248 |
| `S/search`, `LIKE` | 205 / 214 | 161 / 339 |
| `P/search`, full text | 576 / 678 (n 104) | 567 / 690 (n 105) |
| `P/search`, `LIKE` | 542 / 654 (n 111) | 558 / 782 (n 105) |

`W/search` is about three times faster but still above 1 s. Its slowest statement is now the lexical one: the full-text
query at DOP 28, 8.9 s of CPU per execution, enumerating the matches of words such as the corpus's top-ranked ones, which
occur in nearly every message. On `LIKE` it is the scan of every message. Session and peer search are unchanged within
run-to-run noise. The store-level series of this run repeat the second run's within ±10%.

## Findings on the deferred review items

1. **Reconciler `afterId` cursor (Task 3 I4).** One tick costs about 0.55 ms per thousand messages: 0.55 s at 1M, once
   every 30 s, on one core. That is not slow enough to justify the cursor now. It is linear, so at ten million messages it
   would be about 5.5 s per tick; the cursor (remember the highest message id found fully chunked, start the next scan
   after it) is the follow-up when a deployment approaches that size. No code change.
2. **Session-scoped vector seeks (Task 3 M7).** A session search never uses `IX_MessageEmbeddings_Workspace_Session`. The
   plan seeks the session's messages on `UX_Messages_Workspace_Session_Id`, then each message's chunks on
   `UQ_MessageEmbeddings_Message_Chunk`, and reads only that session's chunks (19k logical reads at 1M, 84 ms p50).
   This is good, and the index is unused by search. Every vector query also has a floor of about 45 ms that is
   neither logical reads nor CPU (a 1,500-read session search takes 48 ms with 3 ms of CPU); its cause was not isolated
   (candidates: per-execution compilation of the vector statement, or the 6 KB vector parameter).
3. **`CONTAINSTABLE` cross-workspace volume (Task 3 M7).** `CONTAINSTABLE` returns matches from every workspace before
   the workspace filter applies: a common word yields 323k rows at 1M. A small workspace is not hurt: its plan seeks the
   workspace's own messages and probes the index per row (7 ms at 1M, flat across scales). The cost lands on scopes the
   optimizer drives from `CONTAINSTABLE`. That plan choice is unstable for session search: at 100k and 500k the
   session-scoped full-text query enumerated the whole match set (274k and 286k logical reads; 589 ms p95 at 500k,
   against 54 ms on `LIKE`), while at 1M the vector statement was the slower one. Follow-up for M7: for a session or peer scope, drive
   from the scope's messages with `CONTAINS` rather than from `CONTAINSTABLE`.
4. **Any-term lexical cost (Task 5, D9).** On the full-text path, going from three to eight terms costs +15 to +25%
   (476 to 577 ms p95 at 1M). The cost is dominated by the common word's candidate volume (323k rows), not by the
   per-term `CONTAINS` probes. One `CONTAINSTABLE` per term, unioned and grouped, would enumerate the same common-word
   rows, so it is not adopted. The `LIKE` fallback grows with terms × rows: eight terms take 2.4 / 3.2 s at 1M
   (indicative), and three take 1.0 / 1.3 s. The fallback is for servers without Full-Text Search and small data; a large
   deployment needs Full-Text Search, which the docs say.
5. **Reset `DELETE TOP` ghost records (Task 11).** Not exercised; not observed.

## Decision

The rule (plan Task 10): keep exact search if `W/search` p95 is at most 1 s on the dev host; otherwise add
`Search:MaxVectorCandidates`, a newest-first chunk cap before `VECTOR_DISTANCE`.

`W/search` p95 is 947 ms at 500k but 5,660 ms (full text) and 4,864 ms (`LIKE`) at 1M, so the cap is added. Both
providers implement it, and it is pinned by a shared contract test:

- **Option:** `Nachos:Search:MaxVectorCandidates`, default 100,000; an empty value turns the cap off. It lives under
  `Nachos`, not a top-level `Search` section, so it binds wherever `NachosOptions` already binds, including in-process
  hosts that have no `IConfiguration`. A top-level section bound in `AddNachos` would fail there.
- **Scope:** workspace search only. Session and peer searches read their own sessions' chunks through index seeks; the
  newest-first cap would turn those into a full backward scan (peer search measured 0.6 s → 1.6 s). `HybridSearchService`
  passes the cap only for `MessageSearchScope.Workspace`; the stores honour whatever cap they are given.
- **Semantics:** at most that many embedded chunks in scope are compared, the most recently stored first (highest
  `MessageEmbeddings.Id`; insertion order in memory), not by `CreatedAt`, which a caller may backdate. Distances stay
  exact.
- **SQL:** `TOP (@maxCandidates) … ORDER BY e.Id DESC` inside the grouping, a backward scan of the clustered key that
  stops at the cap.
- **Recall trade-off:** a scope with more chunks than the cap loses its oldest messages from the vector half. The
  lexical half still searches the whole scope. Scopes below the cap, including most sessions, are unchanged. Documented
  in the Search concept page and spec §11.5.
- **Default: 100,000 chunks.** At 1M messages it bounds the vector half to 0.49 / 0.52 s, half the 1 s budget, and still
  covers the newest ~67k messages, so a workspace of up to ~67k messages is searched exactly. 200,000 already costs a
  full second for the vector half alone; 50,000 (0.3 s) would cover only ~33k messages.
- **What the cap does not fix:** with it, `W/search` at 1M is 1.47 / 2.43 s (full text) and 1.74 / 2.95 s (`LIKE`), still
  above the 1 s rule. The lexical half of a workspace search whose query contains a very common word enumerates most of
  the workspace's messages, on full text as well as on `LIKE`. Bounding it is outside this rule's remedy, so it is
  raised for the owner rather than built here. Options: drop terms that occur in more than some fraction of the
  messages, as the English stopwords already are; cap the full-text candidates newest-first, as the vector half now is;
  or accept seconds-long workspace search at this size until M7. Session search, the common agent path, is about 0.25 s
  at 1M.
- **Real fix:** ANN (spec §7.4, M7) for the vector half.
