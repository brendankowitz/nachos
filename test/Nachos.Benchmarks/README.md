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

It needs Docker and a lot of disk: 1M messages is about 1.5M vectors, roughly 9 GB, plus text and indexes, inside the
container. Do not run it beside another Testcontainers run.

```powershell
dotnet run --project test/Nachos.Benchmarks -c Release -- --memory-gb 10
dotnet run --project test/Nachos.Benchmarks -c Release -- --scales 100000,250000 --queries 50 --budget-seconds 30
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--scales` | `100000,250000,500000,1000000` | Ascending message counts of the main workspace; each step adds to the previous one. |
| `--queries` | `200` | The most queries per series. |
| `--budget-seconds` | `60` | A series stops after this long, once it has run 10 queries. |
| `--memory-gb` | `10` | The container's memory cap; SQL Server gets 1 GB less. |
| `--connection` | (none) | Use this server instead of starting a container. The benchmark creates its own database there. |
| `--out` | `artifacts/benchmarks` | Where the report goes. |
