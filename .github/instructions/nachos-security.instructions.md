---
applyTo: "**"
excludeAgent: "cloud-agent"
---

# Nachos security review guidelines

Use these checks for affected trust boundaries, alongside `REVIEW.md`. Report the actual principal, input, control, resource, and unauthorized outcome; distinguish a demonstrated vulnerability from an unverified concern.

## Identity, authorization, and isolation

- When authentication changes, inspect NachosKey and Entra validation as applicable: signing keys and rotation, signatures, issuer and audience requirements, and token lifetime rules. Obtain claims from validated identity or trusted server context; a route value, request body, or header selects a resource and is not proof of permission.
- Check authentication and authorization separately. HTTP authentication is on by default and may be disabled only in Development. Respect deliberately trusted in-process callers and intentionally anonymous endpoints; do not treat their existence as evidence that an HTTP route is authorized.
- Enforce the applicable workspace, peer, and session scope on external reads and writes. A peer- or session-scoped key must not gain workspace-wide access merely because the workspace matches. Check the member-read allowlist and active membership when affected, including allowed reads and refused writes or other peers' resources.
- Trace observer and observed perspectives, session restrictions, and visibility through derived representations, peer cards, conclusions, filters, counts, and later pages where applicable. Unsupported restrictions must not be silently ignored. Check unauthorized existence and count leaks as well as returned content.
- Recheck the current caller's authorization before an idempotency replay exposes a stored response. Follow access and isolation through affected background work, caches, retries, and continuations; a key, job identifier, or privileged worker is not itself caller authorization.

## Data, execution, and operations

- Keep attacker-controlled values separate from SQL and other executable syntax. Check the filter compiler's supported fields and operators as well as value parameterization. Preserve strict in-process JSON validation: accept explicit JSON data and allowlisted scalar values, reject opaque CLR graphs, and do not execute caller-supplied scalar serializers.
- Treat user-supplied messages and model output as untrusted data. Any affected model-driven action must retain server-side validation and authorization. Bound input size, nesting, processing time, and external work where relevant; preserve cancellation and avoid silently accepting invalid data.
- Keep credentials, bearer tokens, and connection strings out of logs, errors, traces, and test artifacts. Real messages and conclusions must not be captured by default; assess any documented opt-in capture for redaction and retention. Examine outbound destinations and redirects, file paths, and archive expansion when a change introduces those surfaces.
- Review dependency provenance, permissions, secrets, and weakened build or test controls. Do not run untrusted PR code with privileged credentials. Apply the repository's licensing rules to relevant shipped dependencies, but do not gate Pages publication on license evidence or notices for `docs/site` libraries.
- Never provision, deploy, delete Azure resources, publish a schema to Azure, push an image consumed by Azure, or run Azure what-if or preview operations without the owner's explicit approval for that action, target, and run. Live Azure CI must be `workflow_dispatch`-only and require owner review in the `azure-live` environment; agents must not create or rotate Azure credentials or secrets. Prefer offline validation and state what remains unverified.

## Evidence

- Use synthetic identities and data for at least one authorized and one unauthorized case when the boundary changes. Check cross-workspace and mismatched peer or session attempts, including derived results and later pages when relevant.
- Verify that tests reach the actual enforcement path. A mocked handler, disabled-auth fixture, or test of only one storage provider does not establish protection for other affected paths. Identify skipped cases and the configurations actually exercised.
- For regression claims, seek base-fails/head-passes evidence; for a new guard, seek evidence that removing it causes a failure. Do not probe live services or use real user data to establish a review finding.
