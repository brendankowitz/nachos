# Team Nachos: shared learnings for developers, teams and their agents

Status: draft for owner review. Date: 2026-11-09.
Builds on the Nachos design (`2026-10-07-nachos-design.md`) and roadmap (`../plans/2026-10-07-nachos-roadmap.md`).
Honcho facts come only from Honcho's **public docs**. The clean-room rule (spec §3) applies.

## 1. Intent

Software teams and their AI coding agents keep relearning the same things. On microsoft/fhir-server, people and agents repeatedly investigate security issues and SQL performance and schema problems. Each developer also keeps steering their own agents with the same best practices. None of this is shared today, so every session starts from zero.

Team Nachos is one **central Nachos server**. Each developer and agent keeps working in individual or shared sessions, as Nachos already supports. On top of that, what they learn is captured and made available to the rest of their team, and to wider groups, through any agent harness.

**Success looks like this.** Agents across the team:
- stop repeating known mistakes;
- flag likely problems early;
- name the common patterns and pitfalls without anyone teaching them again.

## 2. Goals, non-goals, anti-goals

**Goals**
- Model individuals, teams and groups:
  - An **Individual** (one developer) belongs to one or more **Teams**.
  - A **Team** belongs to one or more **Groups**.
- Let each Individual and Team choose which upstreams its learnings **contribute to**, and which upstream knowledge it **pulls down**.
- Make access harness-neutral. Claude Code, Copilot, and anything that speaks MCP or can run a CLI use the same small set of verbs: *remember*, *retrieve/search*, *summarize*. They are delivered as hooks and skills.
- Keep specific facts, such as an exact schema, with the project where they were learned. Let general lessons, such as how to investigate a SQL performance regression, travel. Relevance to the current task decides what an agent sees.
- Base confidence on **reinforcement**: the same lesson arriving from more distinct contributors means higher confidence.

**Non-goals (for now)**
- Recency decay or time-weighted confidence. The owner removed it; reconsider it only if stale knowledge becomes a real problem.
- Copying lessons into downstream workspaces (see §5.3).
- Human approval workflows for promotion. Confidence comes from reinforcement, not sign-off. Revisit this if evidence demands it.
- Company-wide or cross-tenant sharing beyond the configured Group graph.

**Anti-goals (failure even if it works)**
- **Any PII, password, credential, secret or customer data is stored or spreads.** This is absolute.
- One developer's preference, or a single wrong claim, becomes "team truth" because one person repeated it.
- Project-specific facts leak into other projects' agents as if they were general.
- The design only works with one agent harness.

## 3. Phasing (owner's decision)

**Phase 1: remember and retrieve, raw but useful soon.**
- The central server runs with per-developer and per-team workspaces.
- MCP and CLI provide the verbs (remember, search/retrieve, summarize, plus a short topic map).
- Hooks and skills for Claude Code and Copilot.
- Server-side rejection of secrets and PII.
- Project tagging.

**Phase 2: promotion and federation.**
- The promotion step, which generalizes, scrubs and writes lessons upstream.
- Corroboration counted by distinct contributor.
- Federated retrieval across subscribed upstreams.
- Retraction that propagates.
- Group-level clustering, built on dreaming.

## 4. Model (recommended; a builder may refine it)

### 4.1 Levels map to workspaces
- **Each Individual, Team and Group is its own Nachos workspace.** Honcho's public docs describe the workspace as the hard isolation boundary ("split for hard isolation between products, customers, environments, or agents"), and dreams never cross workspaces. Nachos enforces this with composite foreign keys (spec §7.2).
- **A new link layer** sits above workspaces and records:
  - membership (Individual → Teams, Team → Groups);
  - each node's **contribute-to** set and **pull-from** set.

  These sets must be subsets of its memberships. Admins of a Team or Group may restrict what members can choose; whether they can force contribution is left to the builder.
- **Rejected alternatives:**
  - One shared workspace with M6 scopes. Honcho documents scopes as "a recall boundary, not an authorization boundary", and Nachos scopes are derived from raw member messages, so raw facts would reach upstream.
  - A "team" peer that observes everyone. It would need to be in every session, and it also derives from raw messages.

### 4.2 Individuals and projects
- **One workspace per developer, across all their repos** (owner's decision). The developer's agents are peers in that workspace.
- **Every session records the project it belongs to**, for example `project: microsoft/fhir-server`, in session metadata. Messages and conclusions inherit it.
- Retrieval boosts same-project results. Promotion uses the project to decide which details are specific.

### 4.3 What a "lesson" is about
Honcho's model is peer-centric: explicit conclusions are facts about the target peer (spec §11.2). Team lessons are mostly about code and practice.
- **Recommendation:** each workspace has a fixed, person-free subject peer, for example `practice`. Lessons are conclusions about that subject.
- Phase 1 needs this too. A *remember* call writes a conclusion about `practice`, observed by the developer's agent or the developer, using the existing `POST C` path.
- The builder confirms this fits the observer rule and the visibility policy, or proposes an equivalent.

## 5. Flows

### 5.1 Capture (phase 1)
- **Paths in.** There are two:
  1. **Explicit:** a *remember* verb, called by a skill or hook or by the agent itself.
  2. **Implicit:** session content (agent transcripts, or a summary the agent writes at the end of a task) appended as messages. The Deriver extracts conclusions from it.

  Which implicit source works better, raw logs, end-of-task summaries or both, is **settled by experiment** (§8).
- **Secret and PII gate at the server** (owner's decision: **reject**).
  - `AppendAsync`, and every other content-ingesting write (conclusion create, peer card set, *remember*), runs a deterministic detector before the transaction.
  - On a hit, the whole call is rejected with a specific error, for example 422 `type: sensitive_content`, which carries `loc` and the category (credential, PII, connection string and so on) but **never echoes the value**. Nothing is stored or embedded.
  - The server logs counts only, never content.
- **Gate in the hook or skill.** The client side runs the same detector first, so agents rarely hit the server rejection. It is best effort; the server gate is the authoritative one.
- **Nachos has no content scrubbing today.** `SecretRedaction.cs` and `Redaction.cs` only hide Nachos's own keys in logs and errors.

### 5.2 Retrieval (phase 1)
Every agent session can get a small **topic map**: a tree or summary of what this workspace knows, plus subscribed upstreams once phase 2 lands. The agent then **searches or navigates** for detail when it needs it. How much to push up front and how big the map should be is **settled by experiment** (§8). The verbs map onto existing Nachos features:
- *search* uses hybrid search (M3);
- *summarize* uses session summaries and context (M3);
- *retrieve* uses representation and peer context (M2/M3);
- the topic map is new. One possible implementation is a periodic summary over the `practice` subject's conclusions, clustered by topic.

### 5.3 Promotion upstream (phase 2)
A background **promotion step**, scheduled like dreaming, runs per node, for each contribute-to target:
1. **Pick candidates.** Candidates are deductive and inductive conclusions plus instruction-like explicit conclusions about `practice`. Induction alone is the wrong filter: in Nachos, induction is designed for personal traits (spec §11.10), while many team lessons start as one standing instruction.
2. **Classify.** Is the candidate general (transferable across projects) or specific?
3. **Rewrite general ones** so they mention no person, project, repository, schema, host or customer.
4. **Run a stricter scrub gate.** The §5.1 detector plus identifier and name checks, plus an LLM generality check.
5. **Write upstream** as a conclusion about the upstream's `practice` subject.

**Corroboration counts distinct contributors.**
- Upstream dedup reuses the existing exact-then-semantic matcher (distance ≤ 0.05).
- A match from a **new** downstream node increments a contributor count. A repeat from the same node does not.
- One chatty developer cannot inflate confidence. Today's `times_derived` counts repeats within one collection, not people.

**Provenance is opaque.** Each upstream lesson stores only `{level, node_id, contributor_count, downstream_ref}`. Never raw text, message ids or names.

**Retraction.**
- When a downstream source is deleted or contradicted, that node's contribution is withdrawn.
- When the count reaches 0, the upstream lesson is removed.
- This plugs into §13 deletion.

**Loop guard.** Lessons that originate upstream are marked and never re-promoted, the same rule as "dream-generated conclusions never count toward the trigger" (§11.10).

### 5.4 Pull downstream (phase 2)
**Federate at read time; do not copy.** Retrieval for a node queries its own workspace plus each subscribed upstream workspace, then fuses the lists with the existing RRF (k = 60, §11.5).
- Specific facts never leave home. Copies would spread any scrub miss and would need sync.
- Each result carries its level and contributor count (for example "Team ×4, Group ×2"), so agents can weigh it.
- Every store method takes one workspace today, so multi-workspace reads are new. They must extend `VisibilityPolicy`, never bypass it.

## 6. Access and auth
- **Everyone connects to one central Team Nachos server**, for example the Azure deployment Nachos already targets.
- **Each developer and agent uses its own key.**
- **Federated reads need an identity that spans several workspaces.**
  - Entra `PrincipalGrants` already map one identity to many workspaces.
  - HS256 keys carry a single workspace.
  - Whether to add a multi-workspace key format is open (§9).
- **MCP and CLI front the verbs.** This pulls M7's native MCP and CLI tool packaging forward. They talk to the server over the existing HTTP API, which stays the foundation.

## 7. Roadmap fit
- **Uses:** M3 (search, summaries, context), M2 (Deriver, conclusions, cards).
- **Pulls forward:** M7 MCP/CLI packaging (phase 1) and M5 dreaming (phase 2, as the base for promotion and clustering).
- **Optional:** M6 scopes, for example per-project scopes inside an Individual workspace. Workspace chat (M6) and dialectic chat (M4) are useful but not required.
- **Genuinely new, beyond Honcho:**
  - the hierarchy and flow settings;
  - the content gate;
  - the `practice` subject;
  - the topic map;
  - the promotion step and the generality classifier;
  - distinct-contributor corroboration;
  - opaque provenance and retraction;
  - federated multi-workspace retrieval and auth.

  Each new wire surface is a Nachos extension, outside the Honcho manifest, and documented as such.

## 8. Experiments to run (decide by data, not opinion)
1. **Capture source.** Raw transcripts versus end-of-task agent summaries versus both. Measure lesson precision and recall, and cost.
2. **Delivery.** A topic map plus search on demand, versus pushing more context up front. Also measure map size.
3. **Promotion quality.** The generality classifier's false-promotion rate (specific facts leaking) and its miss rate, on a fixture of fhir-server-like lessons.
4. **Detector.** False positives (blocked legitimate content, such as code that looks like a key) and false negatives, on a fixture.

## 8a. Decisions after the builder check (2026-11-09)
The owner delegated these three ("make good choices for me"). They are coordinator calls, recorded here for the owner to check.

1. **Team workspaces take direct writes in phase 1.**
   - Shared team sessions live in the Team workspace, and a *remember* verb can target the Team as well as the Individual.
   - In phase 1 an agent's retrieval reads its Individual workspace and the Team workspaces the client is configured for. This is client-side multi-workspace config, not server federation.
   - The membership and flow tables (the link layer) ship in phase 2 with promotion and federation.
2. **PII is defined narrowly, for the reject rule.**
   - Rejected: secrets, credentials, keys and tokens, connection strings, and third-party or customer personal data (emails, phone numbers and national ids that are not the developer's own; health or patient data patterns).
   - Allowed: developer identity, meaning peer and workspace names and the developer's own git identity.
   - The client hook masks home-directory paths (`C:\Users\<name>\`, `/home/<name>/`) before sending.
   - Patient and customer data detection is best effort (rules plus heuristics), and is documented as such.
   - Raw messages in Individual workspaces are kept; open decision 5 below stays open.
3. **Each agent gets its own peer-scoped key.**
   - The key reads its own peer's sessions and the memberships the existing rules allow. It does not hold a workspace key, so it cannot read teammates' raw messages in a Team workspace.
   - A new permission lets a peer-scoped key write conclusions about the workspace's `practice` subject (*remember*), and read `practice` conclusions and the topic map.
   - Workspace keys stay with humans and admins.
   - This is a Nachos extension to the auth matrix, with its own security review in phase 1.

**Other calls made from the builder check:**
- The content gate is a **per-workspace setting** (on for Team Nachos workspaces, off by default). Honcho-compatible workspaces keep their current behaviour, and conformance is unaffected.
- **Lessons may climb more than one level** (Individual → Team → Group). The loop guard only stops re-promoting a lesson into a level it came from, or back down. An Individual reads Group knowledge **through its Team's** pull settings.
- **Phase 1 project scoping:** *remember* takes `scope: project | general`. Retrieval returns general items plus specific items for the current project, and other projects' specific items are excluded. The automatic general-versus-specific classifier is phase 2.
- **Phase 1 capture:**
  - The skill asks the agent to write lessons through *remember* at the end of each task.
  - Raw transcripts may still be appended as messages, which the Deriver turns into person-centric facts.
  - A `practice`-targeted extraction mode, and therefore experiment 1, is phase 2.
- **Experiments do not gate phase 1.** Phase 1 ships with defaults. The experiments tune them afterwards, on synthetic fixtures; real transcripts are never used as fixtures.
- **Copilot target:** GitHub Copilot CLI and VS Code agent mode, through MCP, with hooks wherever the harness supports them. The Copilot coding agent is later.

## 9. Open decisions (theirs, still to answer)
1. **Promotion thresholds per level.** For example, is one Individual's lesson enough to reach its Team? Does reaching a Group need N or more Teams?
2. **Upstream edits and deletes.** Who can edit or delete an upstream lesson, and are downstreams told?
3. **Conflicting lessons upstream.** Keep both as `contradiction`, or let one win?
4. **Federated auth.** A multi-workspace Entra identity only, or also a new multi-workspace key format?
5. **Raw messages in Individual workspaces.** May they be kept indefinitely after passing the gate, or should they be pruned once conclusions exist?

## 10. Left to the builder
- Detector rules and libraries. Licences must be permissive (spec §3 rule 7).
- The link-layer schema (additive, AutoSafe).
- The exact topic-map algorithm.
- MCP tool names.
- The hook and skill packaging per harness.
- The promotion schedule.
- How admins set restrictions.

## 11. Calls made by the coordinator (owner to check)
- Workspace per level plus a link layer, rather than scopes or an observer peer (from research).
- A person-free `practice` subject peer for lessons.
- 422 `sensitive_content` as the reject error shape.
- Promotion candidates beyond induction.
- Counting distinct contributors rather than `times_derived`.
- Federation rather than copying.
- Opaque provenance and retraction.
- The client gate is best effort and the server gate is authoritative.
