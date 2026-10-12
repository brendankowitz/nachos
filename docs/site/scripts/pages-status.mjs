import { appendFile } from 'node:fs/promises';

export async function pagesEnabled({ apiUrl, repository, token }) {
  if (!token) throw new Error('Pages inspection requires a token.');
  if (!/^[^/]+\/[^/]+$/.test(repository ?? '')) throw new Error('Expected owner/repository.');
  const response = await fetch(new URL(`repos/${repository}/pages`, `${apiUrl.replace(/\/$/, '')}/`), {
    headers: {
      Accept: 'application/vnd.github+json',
      Authorization: `Bearer ${token}`,
      'X-GitHub-Api-Version': '2022-11-28',
    },
    redirect: 'error',
    signal: AbortSignal.timeout(15_000),
  });
  if (response.status === 404) return false;
  if (!response.ok) throw new Error(`Pages inspection failed: HTTP ${response.status}.`);
  const result = await response.json();
  if (!['workflow', 'legacy'].includes(result.build_type)) throw new Error('Unexpected Pages inspection response.');
  return result.build_type === 'workflow';
}

if (import.meta.main) {
  const enabled = await pagesEnabled({
    apiUrl: process.env.GITHUB_API_URL,
    repository: process.env.GITHUB_REPOSITORY,
    token: process.env.GITHUB_TOKEN,
  });
  if (!process.env.GITHUB_OUTPUT) throw new Error('Missing GITHUB_OUTPUT.');
  await appendFile(process.env.GITHUB_OUTPUT, `enabled=${enabled}\n`);
  console.log(enabled ? 'GitHub Actions Pages is already enabled.' : 'Skipping publication: GitHub Actions Pages is not enabled.');
}
