import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { verifyProvenance } from '../integrations/output-evidence.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
if (process.argv.length > 3) throw new Error('Usage: node scripts/verify-provenance.mjs [output-directory]');
const data = await verifyProvenance(root, process.argv[2] ? resolve(process.argv[2]) : resolve(root, 'dist'));
console.log(`Verified complete output provenance: ${data.outputs.length} files, ${data.inputs.length} inputs, ${data.sources.length} sources.`);
