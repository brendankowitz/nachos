import { readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// Build real products in the original test repository, then generate and hash
// the references consumed by each isolated site fixture before its snapshot.
export async function useRealReferenceRepository(site) {
  const runner = join(site, 'scripts/run.mjs');
  const text = await readFile(runner, 'utf8');
  const declaration = "const repository = resolve(root, '..', '..');";
  if (!text.includes(declaration)) throw new Error('The fixture must retain the mandatory reference generation runner.');
  await writeFile(runner, text.replace(declaration,
    `const repository = ${JSON.stringify(resolve(fileURLToPath(new URL('../../../', import.meta.url))))};`));
}
