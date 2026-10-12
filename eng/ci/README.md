# CI workflow policy guards

`Test-WorkflowPolicy.ps1` and `Test-DocsWorkflowPolicy.ps1` parse every
`.github/workflows/*.yml` and `*.yaml` file with YamlDotNet and reject
credential-bearing documentation deployment shapes except the reviewed GitHub
Pages profile for #13. The guard also rejects new workflow/job-level Pages,
OIDC or write-all grants, credential-bearing reusable-workflow jobs, containers
and services outside that exact profile.

The guard is intentionally bounded static analysis. It proves the checked-in YAML shape only. It does **not** prove:

- behavior inside a pinned action beyond the reviewed full-length commit SHA;
- GitHub-side environment configuration, including the `github-pages` branch policy;
- runner or hosted-tool compromise after checkout;
- all possible expression-evaluation semantics beyond the modeled main-only conditions;
- behavior inside non-credentialed reusable workflows or composite actions if
  they are later introduced;
- repository or organization settings outside the workflow files.

Changing any of those trust boundaries requires a separate review rather than weakening the YAML guard.
