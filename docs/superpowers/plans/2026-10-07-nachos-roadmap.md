# Nachos Delivery Roadmap (M1–M7)

> **For agentic workers:** This is the milestone-level plan. Each milestone is executed from its own detailed plan, `docs/superpowers/plans/<date>-nachos-m<k>-<name>.md`. A milestone's detailed plan is written by its driver at milestone kickoff, reviewed by the partner, and merged as the **first commit of that milestone's PR**. Execute tasks with the `implement-task-next` skill under the `multi-agent-pr-next` protocol.

**Goal:** Deliver Nachos (full Honcho v3 functional parity on .NET 10 / Azure) as seven independently shippable milestones.

**Architecture:** Modular core library (`Nachos.Abstractions` ← `Nachos.Core` ← providers/hosting ← `Nachos.Api`/`Nachos.Worker`). SQL Server / Azure SQL is the system of record, with a dacpac-managed schema. Agents are built on Microsoft Agent Framework. Deployment uses Azure Container Apps via azd + Bicep, gated by owner consent.

**Tech Stack:** .NET 10 (C# 14), ASP.NET Core minimal APIs, EF Core 10 (query mapping only), Microsoft.Data.SqlClient, Microsoft.Build.Sql + DacFx, Microsoft Agent Framework / Microsoft.Extensions.AI, .NET Aspire, azd + Bicep, xUnit + Shouldly + NSubstitute, Testcontainers (SQL Server 2025), Astro + Starlight.

**Spec:** [`docs/superpowers/specs/2026-10-07-nachos-design.md`](../specs/2026-10-07-nachos-design.md) (merged in #1). Tracking issue: #2.

## Global Constraints

These apply to every milestone. Values are copied from the spec.

- **Clean-room (spec §3):** never read or copy Honcho `src/`, `sdks/`, or `mcp/` source or prompt text. Inputs are limited to the spec, Honcho's public docs and public OpenAPI, and black-box SDK behavior. Wire names and default values may match.
- **Licenses (spec §3 rule 7):** distributed artifacts use only permissive licenses from the allowlist. EPL-2.0/MPL-2.0 are allowed only for non-distributed tooling, via `eng/license-exceptions.json`. GPL, AGPL, LGPL, and SSPL are never allowed.
- **Schema (spec §7.3):** all DDL lives in `src/DataLayer/Nachos.DataLayer.SqlServer.Database/` (SqlAzureV12). `…Database.Sql2025` (Sql170) has no DDL of its own and globs the same files. EF Core never creates migrations. `AllowIncompatiblePlatform` is never set.
- **Azure consent (spec §18.3):** no agent or CI runs `azd up`/`provision`/`deploy`/`down`, `az deployment …`, `what-if`, `sqlpackage Publish` to Azure, or any registry push used by Azure, without a per-run **untagged** owner approval. CI workflows that touch Azure are `workflow_dispatch`-only and run in the `azure-live` environment.
- **SDK:** `global.json` pins `10.0.100` with `rollForward: latestFeature`. Central package management lives in `Directory.Packages.props`. `Nullable` and `TreatWarningsAsErrors` are enabled in `Directory.Build.props`.
- **Docs (spec §22):** every milestone updates the README feature list, adds concept/guide pages, and keeps generated reference current. The docs site builds with zero broken links.
- **Commits:** one task per commit (or a small series), each with its tests. Messages end with an `Agent: <Name>` trailer **and the agent's own co-author trailer**: each agent uses the attribution its host provides (for example, Cortado and Cedar use `Co-authored-by: Copilot App`; Salsa uses its Claude session trailers). `git log --grep='^Agent: <Name>$'` identifies each agent's commits.

## Ownership model

- One shared branch and one draft PR per milestone (`nachos-m<k>`). Coordination follows the protocol in [#2](https://github.com/brendankowitz/nachos/issues/2#issuecomment-6044989873) (Coordination table, STATUS footer, deadlock ladder).
- **Ownership is by file/project, assigned in each milestone plan.** An agent never edits files the partner owns. Unowned files are announced before editing.
- **Environment-driven default split:**
  - **Cortado** (host has Docker Desktop) owns foundation and SQL/Testcontainers-heavy work: database projects, SQL providers, the queue/lease implementation, schema tooling, and Aspire.
  - **Cedar** (owns CI; host has no Docker) owns Core services, API, auth, CI workflows, and docs.
  - **Salsa** (joined 2026-10-07 at the owner's request; cloud session with .NET 10 and Docker) owns separable slices: the in-memory provider, the .NET client, upstream-SDK conformance, and infra (offline).
  - Each milestone plan may adjust this split.
- **Validation:** each agent runs its own task tests before pushing and again at the final SHA. **Cedar** is the validation owner for the full set. **Cortado and Salsa** run the Docker-backed slices and post results with the SHA.
- **Merge gate:** every agent that owns files in a PR posts LGTM at the same head SHA. For PRs where only one agent owns files (for example plan PRs), the LGTM of at least one other agent is required, and every finding raised by any agent must be resolved (fixed, refuted, or split).
- **Heartbeats** (owner-directed protocol addendum): every active agent posts a tagged status at least every 30 minutes until the project goal is met.

## Milestones

Each milestone's Done criteria = the spec §20 exit criteria **plus**:

- both agents LGTM at the same head SHA (per the merge gate above), with the validation owner's results at that SHA and green CI;
- the docs gate (spec §22.2), met in that milestone;
- any Azure-dependent exit met by an **owner-approved** run, or listed as pending owner approval in the PR (it never blocks merge of code that passed offline validation; it does block the release tag).

| M | Detailed plan | Scope (spec §20) | Default driver |
|---|---|---|---|
| M1 Foundation | [`2026-10-07-nachos-m1-foundation.md`](2026-10-07-nachos-m1-foundation.md) | Skeleton, schema/dacpac + SchemaDeployer, in-memory + SQL providers, CRUD for workspaces/peers/sessions/membership/messages, filters, pagination, errors, NachosKey + Entra auth, keys/grants, health, Idempotency-Key on message create, client, bootstrap CLI, Aspire, Bicep/azd (offline-validated), README + Starlight scaffold + Pages workflow, CRUD conformance | Cortado |
| M2 Memory formation | written at M2 kickoff | Queue/leases (RCSI-safe claim), worker host, transactional enqueue, embeddings + reconciler, LLM layer, Deriver, dedup/corroboration, conclusions, representation, peer card, peer context, queue status, `VisibilityPolicy`; KEDA R7 verification (owner-gated) | Cedar |
| M3 Recall | written at M3 kickoff | Summarizer, hybrid search (exact vector + FTS + RRF), session context (hard budget), summaries, deletion lifecycle + jobs + `W/jobs`, FTS image in CI, 1M-message exact-search benchmark (R1) | Cortado |
| M4 Dialectic | written at M4 kickoff | Peer chat (levels, tools, prefetch, SSE, structured output, evidence), first eval baseline (owner-gated live run) | Cedar |
| M5 Dreaming | written at M5 kickoff | Dream scheduler, omni (deduction → induction), card_refresh, reasoning chain, `schedule_dream` | Cortado |
| M6 Parity completion | written at M6 kickoff | Scopes (+ backfill/removal), workspace chat, durable webhooks, upload, clone; full curated conformance suite | Cedar |
| M7 Ecosystem & hardening | written at M7 kickoff | Native MCP, CLI `inspect`/`mcp` + tool packaging, rate limiting, optional distributed cache, ANN optimization, surprisal prioritizer, docs polish + "Deploy to Azure" button, NuGet packaging, release | Cortado |

Follow-ups outside M1–M7: [#3](https://github.com/brendankowitz/nachos/issues/3) (Honcho import, after M6) and [#4](https://github.com/brendankowitz/nachos/issues/4) (legacy `/v2` evaluation).

## Cross-milestone rules

1. **Additive schema only between releases.** Each milestone's DDL must classify as `AutoSafe` in the SchemaDeployer DeployReport (spec §7.3). Anything else needs an explicit finding approved by both agents, plus the `nachos schema upgrade --allow-data-loss` operator path documented.
2. **Interfaces flow forward.** A milestone may extend `Nachos.Abstractions` but must not break earlier signatures without a finding and both agents' agreement.
3. **No stubs presented as features.** A route that isn't implemented yet returns `501 {"detail": "Not implemented in this Nachos version"}` and is listed in the compatibility matrix. It never returns fake data.
4. **Owner gates are listed, not guessed.** Every milestone PR body has an "Owner actions requested" section. Each entry gives the exact command, the target, and why it's needed. Agents never run these themselves.
