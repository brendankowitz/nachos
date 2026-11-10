# Team Nachos draft: principal review notes (2026-11-10)

Review of the 2026-11-09 draft of `../2026-11-09-team-nachos-design.md` against the Nachos code on branch `nachos-m3` and the main spec. These notes explain revision 2; the spec is the source of truth.

Honcho facts come from its public docs (honcho.dev/docs) and public OpenAPI only, checked on 2026-11-10:

- design-patterns: "A workspace is a hard isolation boundary." "Split workspaces only when you need hard isolation between products, customers, environments, or agents."
- design-patterns, on scopes: "Not authorization. A workspace key reads any session, scoped or not." "A scope bounds *recall*, not *access*." The scopes page adds that a named-scope key "enforces the boundary on the server".
- platform reference: a peer-scoped key has "**read-only** access to the sessions its peer is an active member of … It cannot write to those sessions or act on other peers." Conclusions are not mentioned.
- dreaming: "Dreams do not span across workspaces or across different peer pairs."
- No cross-workspace, team or organization memory feature is documented; the documented way to share is to point clients at one workspace.
- OpenAPI: `Conclusion` and `ConclusionCreate` have no `metadata` field.

## Findings

Ordered by severity. "Code" names what was checked.

### Security

1. **Shared memory is a prompt-injection channel, and the draft did not mention it.** A lesson is text written by one person's agent and placed in every teammate's agent context. One mistaken or compromised agent could instruct the rest. Revision 2 adds §9: framing, a rule that another person's unconfirmed lesson is never pushed, dispute, admin delete, an audit of contributors, invisible-character rejection and a flood bound.
2. **The gate could be switched off.** A "per-workspace setting" is writable by a workspace key, which every agent would hold for its own workspace. The gate is now bound to node registration, which only an admin performs and nobody can undo.
3. **The gate covered content only.** Metadata, ids, configuration, custom instructions, search queries (sent to the embedding provider) and model output were unlisted. A git remote with a token in its userinfo would have been stored as the project tag. The gate now scans every string of every mutating request, queries, and worker output, with a completeness test.
4. **Contributing was all or nothing.** A developer on a work team would have shared lessons from unrelated or private repositories. Links now carry a child-owned project list.
5. **Corroboration could be inflated or never counted.**
   - Phase 1 had no node identity for a writer, and one human with two agent peers would count twice.
   - The 0.05 cosine threshold (code: `ConclusionDeduplicator`, main spec §8) recognises near-verbatim text only, so two developers' phrasings of one lesson would never match and nothing would reach a count of two.
   - Revision 2 counts distinct contributor *nodes*, has the agent confirm existing lessons in T1, and uses an LLM same-lesson judge in T2 with a measured threshold (E3).
6. **No incident path.** Nothing said how to remove a gate miss that had already been promoted and confirmed by others. `cascade=upstream` (admin) does.

### Fights with the existing model

7. **Lessons as conclusions about a `practice` peer.** Six concrete conflicts, listed in the spec §3.2 (code: `RouteRequirements` puts `POST C` at workspace access; `IConclusionStore.InsertAsync` does not deduplicate; the wire `Conclusion` has no metadata; collections are per observer). Replaced by a `Lesson` resource.
8. **Peer-scoped agent keys cannot capture.** Code: `NachosAuthorizationHandler` and `MemberReadRoutes` give a peer key read-only session access; it cannot create a session or append a message. The draft's 8a.3 would have needed a second widening of the Honcho key matrix. Agents now hold a workspace key for the developer's own workspace and reach upstream lessons through links.
9. **Federated reads "extend `VisibilityPolicy`".** Code: `VisibilityPolicy` is a session allowlist; it has no notion of several workspaces. Lessons get their own single read-scope object instead, with the same "no read path without it" rule.
10. **Fusing per-workspace result lists with RRF** ranks each workspace's first hit equally whatever its distance. Lessons are queried as one set across the readable workspaces.
11. **New work task types are not an auto-safe schema change.** Code: `CK_WorkItems_TaskType` lists values 1–8, and `DeployReportClassifier` classifies a constraint created on an existing table as `Unsafe`. T1 needs none; T2 records the reviewed upgrade step. For the same reason `Lessons.SessionId` and its FK are created in T1.
12. **Retraction across workspaces had no lock rule.** Revision 2 adds one: two workspace rows are locked in ascending id order, and a deleting workspace's outbound contributions are removed as ordinary writes under each upstream.

### Gaps a builder would have had to guess

13. Who may create, change or remove a link; whether a parent can force contribution (it cannot).
14. What `project` is, how it is derived, and what happens with forks or no remote.
15. Where a conclusion's project tag would be stored (nowhere; see 7).
16. What the "topic map" contains in phase 1 without clustering (agent-supplied topics, counted).
17. What "summarize" means. The draft mapped it to session summaries; the owner's "summary" is read here as the brief of what the team knows.
18. The loop guard. With `individual → team → group` links only and no downward copies, no loop can form; a unique promotion row is enough.
19. The experiments had no arms, fixtures, measures or decision rules.

### Over-engineering removed

- A new multi-workspace key format (the draft's open decision 4): a link is the grant.
- Client-side multi-workspace retrieval in both the CLI and the MCP server.
- A strict and a standard gate profile: one rule table, plus identifier checks at promotion.
- Downstream notification of upstream edits: lessons are immutable and reads federate.

## Draft open decisions, resolved

| Draft §9 | Resolution |
|---|---|
| 1. Promotion thresholds | Per-node `PushThreshold` and `PromoteThreshold`; defaults in Owner decision 4 |
| 2. Upstream edits and deletes | Lessons are immutable. A contributor withdraws its stance; workspace-level access on the lesson's node deletes it. No notification is needed. |
| 3. Conflicting lessons | Both kept and linked; Owner decision 6 |
| 4. Federated auth | No new key format; §10.3 |
| 5. Raw messages in Individual workspaces | Owner decision 8 |

## Known limits accepted

- An Individual cannot give feedback on a Group lesson in T1.
- The diamond case (one Individual in two Teams of one Group) can count twice at the Group.
- A single HS256 key cannot be revoked (main spec §9.4 item 21); expiry or Entra.
- Detection of names and free-text customer data is best effort.
