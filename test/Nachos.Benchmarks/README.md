# Nachos.Benchmarks

The M3 search benchmark (plan Task 10, design-spec risk R1). It is a console app that is built with the solution but is
not a test project: `dotnet test` and the CI lanes never run it. The recorded results and the decision are in
[`research/m3-search-benchmark.md`](../../research/m3-search-benchmark.md).

## What it does

1. Starts the Full-Text Search SQL Server image (`eng/docker/mssql-fts`) with a container memory cap, unless
   `--connection` names an existing server.
2. Creates a database and, through `INachosClient`, a workspace of 1,000 sessions and 50 peers (two peers per session),
   plus a 2,000-message second workspace.
3. Bulk-copies messages straight into `dbo.Messages` and `dbo.MessageEmbeddings`, in batches of 10,000. The messages are
   lorem-style text of 30 to 70 made-up words drawn from a Zipf distribution. Half of the messages have two chunks. Every
   chunk has a synced, unit-norm random 1536-dimension vector.
4. At each scale step (default 100k, 250k, 500k and 1M messages), waits for the full-text index to catch up, then runs
   the timed series:
   - `W/search`, `S/search` and `P/search` end to end through `INachosClient` (limit 10), with the full-text path and
     with the `LIKE` fallback;
   - the store's vector search, for workspace, session and peer scope;
   - the store's lexical search with 1 rare term, 3 terms and 8 terms (one common), on both lexical paths, plus one
     common term in the small workspace (the cross-workspace `CONTAINSTABLE` candidate volume);
   - the reconciler's `ListUnchunkedMessagesAsync(500)` with nothing left to chunk.

   Each series records p50/p95 and, from the plan cache, the logical reads, CPU time, degree of parallelism and index
   operations of its slowest statement.
5. Writes a Markdown report to `artifacts/benchmarks/` after every step, so a partial run keeps its results.

## Running it

It needs Docker and a lot of disk: 1M messages is about 1.5M vectors, 12.0 GB of `MessageEmbeddings` as measured,
plus text and indexes, inside the container. Do not run it beside another Testcontainers run.

```powershell
dotnet run --project test/Nachos.Benchmarks -c Release -- --memory-gb 12
dotnet run --project test/Nachos.Benchmarks -c Release -- --scales 100000,250000 --queries 50 --budget-seconds 30
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--scales` | `100000,250000,500000,1000000` | Ascending message counts of the main workspace; each step adds to the previous one. |
| `--queries` | `200` | The most queries per series. |
| `--vector-caps` | (none) | Comma-separated candidate caps; each adds a store-level workspace vector series comparing only the newest that many chunks. |
| `--search-budget-seconds` | `3600` | The `W/search` and `S/search` series stop after this long, so they normally reach `--queries`. |
| `--budget-seconds` | `60` | Every other series stops after this long, once it has run 10 queries. |
| `--memory-gb` | `12` | The container's memory cap; SQL Server's own limit is 2 GB less, leaving room for the full-text daemon. |
| `--connection` | (none) | Use this server instead of starting a container. The benchmark creates a `nachos_bench_<timestamp>` database there and **leaves it behind**; drop it yourself when you are done. |
| `--out` | `artifacts/benchmarks` | Where the report goes. |

A query that hits the 30 s command timeout counts as 30,000 ms in the percentiles and in the `timeouts` column. A series
of fewer than 50 queries is marked "indicative only". The full-text series are skipped (and the report says so) on a
server without Full-Text Search; otherwise each one first checks that the index is ready, and the waits for the index
give up after 90 minutes.
