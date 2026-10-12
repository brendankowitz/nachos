import { copyFile, mkdir, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  assertUnlinked, compare, digest, docsBuildSettings, files, ordered, outputInventory, packageUnion, producers, regularFile,
  slash, snapshotInputs, sourceEvidence, validateEvidence, writeEvidence,
} from './output-evidence.mjs';
export { verifyProvenance } from './output-evidence.mjs';

export function createProvenance(root) {
  const require = createRequire(join(root, 'package.json'));
  const recorded = new Map();
  const generated = new Map();
  const sources = new Map();
  const templates = new Set();
  const inline = new Map();
  let client = false;
  let outDir;

  async function source(id) {
    const openapiRoute = fileURLToPath(import.meta.resolve('starlight-openapi/routes/static'));
    const openapiFacade = 'virtual:astro:page:' + slash(relative(root, openapiRoute)).replace(/\.astro$/, '@_@astro');
    if (slash(id) === slash(resolve(root, openapiFacade))) {
      id = openapiRoute;
    }
    if (id === '\0vite/modulepreload-polyfill.js' || id === '\0vite/preload-helper.js') {
      id = require.resolve('vite/package.json');
    }
    if (id === '\0virtual:starlight/pagefind-config') id = fileURLToPath(import.meta.resolve('@astrojs/starlight'));
    if (id === '\0virtual:starlight-openapi/schemas' || id === '\0virtual:starlight-openapi/context') {
      id = fileURLToPath(new URL('./libs/vite.ts', import.meta.resolve('starlight-openapi')));
    }
    if (id === '\0virtual:astro:adapter-config/client' || id === 'astro:scripts/page.js') {
      id = require.resolve('astro/package.json');
    }
    if (id.startsWith('\0')) throw new Error(`Unknown shipping module provenance: ${id}`);
    const path = id.startsWith('file:') ? fileURLToPath(id) : id.split('?')[0];
    if (!isAbsolute(path)) throw new Error(`Unknown shipping module provenance: ${id}`);
    const evidence = await sourceEvidence(root, path);
    const existing = sources.get(evidence.path);
    if (existing && JSON.stringify(existing) !== JSON.stringify(evidence)) {
      throw new Error(`Source changed during provenance capture: ${evidence.path}`);
    }
    sources.set(evidence.path, evidence);
    return evidence.path;
  }

  async function contribution(bytes, ids, producer) {
    if (!producers.has(producer)) throw new Error(`Unknown generated producer: ${producer}`);
    if (!ids.length) throw new Error(`Missing source evidence for ${producer}`);
    return { producer, sources: ordered(await Promise.all(ids.map(source))), sha256: digest(bytes) };
  }

  function remember(map, key, value) {
    const previous = map.get(key);
    if (previous && JSON.stringify(previous) !== JSON.stringify(value)) {
      throw new Error(`Conflicting provenance evidence: ${key}`);
    }
    map.set(key, value);
  }

  function rememberOutput(output, bytes, evidence) {
    remember(recorded, resolve(output), { sha256: digest(bytes), evidence });
  }

  async function record(output, bytes, ids, producer) {
    rememberOutput(output, bytes, [await contribution(bytes, ids, producer)]);
  }

  const api = {
    async registerGenerated(bytes, ids, producer = 'vite-asset', inherited = []) {
      const evidence = [await contribution(bytes, ids, producer), ...inherited];
      remember(generated, digest(bytes), evidence);
    },
    async registerInline(bytes, ids, producer) {
      const evidence = await contribution(bytes, ids, producer);
      inline.set(JSON.stringify(evidence), evidence);
    },
    async emitGenerated(output, bytes, ids, producer, inherited = []) {
      const evidence = [await contribution(bytes, ids, producer), ...inherited];
      await assertUnlinked(output);
      rememberOutput(output, bytes, evidence);
      await mkdir(dirname(output), { recursive: true });
      await writeFile(output, bytes);
    },
    async recordFile(output, ids, producer = 'copy', inherited = []) {
      const bytes = await regularFile(output);
      if (producer === 'copy' && (ids.length !== 1 || !bytes.equals(await regularFile(ids[0])))) {
        throw new Error(`Copied output changed from its source evidence: ${output}`);
      }
      rememberOutput(output, bytes, [await contribution(bytes, ids, producer), ...inherited]);
    },
    async copyAsset(input, output) {
      await regularFile(input);
      await assertUnlinked(output);
      await mkdir(dirname(output), { recursive: true });
      await copyFile(input, output);
      await api.recordFile(output, [input]);
    },
    async renderEvidence(constructionSources = []) {
      if (!templates.size) throw new Error('No actual Astro render template sources were captured');
      const authored = (await files(join(root, 'src'))).filter((file) => /\.(md|mdx|astro|json)$/.test(file));
      const ids = ordered([...templates, ...authored, ...constructionSources]);
      const templateEvidence = await contribution(
        (await Promise.all(ids.map((id) => regularFile(id.split('?')[0])))).map(digest).join('\n'),
        ids, 'astro-render');
      return [templateEvidence, ...inline.values()];
    },
    async recordRendered(output, evidence) {
      if (!evidence.length) throw new Error('Missing rendered output evidence');
      rememberOutput(output, await regularFile(output), evidence);
    },
    async write(directory, { pending, build = docsBuildSettings() } = {}) {
      const inventory = await outputInventory(directory);
      const outputs = [];
      const present = new Set(inventory.map(({ path }) => resolve(directory, path)));
      for (const path of recorded.keys()) {
        const rel = relative(directory, path);
        if (!rel.startsWith('..') && !isAbsolute(rel) && !present.has(path)) {
          throw new Error(`Recorded output missing from closure: ${path}`);
        }
      }
      const used = new Set();
      for (const { path, sha256 } of inventory) {
        const entry = recorded.get(resolve(directory, path));
        if (entry && entry.sha256 !== sha256) throw new Error(`Asset changed after provenance capture: ${path}`);
        const captured = entry?.evidence ?? generated.get(sha256);
        if (!captured) throw new Error(`Unknown shipping asset provenance: ${path}`);
        const evidence = [...captured].sort((a, b) => compare(JSON.stringify(a), JSON.stringify(b)));
        const members = ordered(evidence.flatMap((item) => item.sources));
        for (const member of members) used.add(member);
        outputs.push({ path, sha256, evidence,
          packages: packageUnion(members.map((member) => sources.get(member))) });
      }
      const data = { schemaVersion: 1, build, inputs: await snapshotInputs(root),
        sources: ordered(used).map((path) => sources.get(path)), outputs };
      await validateEvidence(root, directory, data);
      if (pending) {
        await assertUnlinked(pending);
        await mkdir(dirname(pending), { recursive: true });
        await writeFile(pending, `${JSON.stringify(data, null, 2)}\n`);
      } else {
        await writeEvidence(directory, data);
      }
      return data;
    },
    vitePlugin: {
      name: 'nachos-bundle-provenance',
      enforce: 'post',
      configResolved(config) {
        client = !config.build.ssr;
        outDir = resolve(config.root, config.build.outDir);
      },
      generateBundle: {
        order: 'post',
        async handler(options, bundle) {
          const isClient = this.environment ? this.environment.name === 'client' : client;
          const directory = options.dir ?? outDir;
          const cssSources = new Map();
          for (const chunk of Object.values(bundle)) {
            if (chunk.type !== 'chunk') continue;
            const ids = Object.keys(chunk.modules);
            if (!isClient) {
              for (const id of ids.filter((id) => /\.astro(?:\?|$)/.test(id) ||
                (/\/node_modules\/starlight-openapi\/.+\.(?:ts|js)$/.test(slash(id)) && chunk.modules[id].renderedLength > 0) ||
                slash(id).endsWith('/@astrojs/starlight/dist/components-internals/Icons.js'))) {
                const path = id.split('?')[0];
                if (isAbsolute(path)) templates.add(path);
              }
            }
            const css = ids.filter((id) => /\.css(?:\?|$)|type=style/.test(id));
            for (const name of chunk.viteMetadata?.importedCss ?? []) {
              cssSources.set(name, [...(cssSources.get(name) ?? []), ...css]);
            }
          }
          for (const output of Object.values(bundle).sort((a, b) => compare(a.fileName, b.fileName))) {
            if (output.type === 'chunk') {
              if (!isClient) continue;
              const ids = Object.entries(output.modules)
                .filter(([, module]) => module.renderedLength > 0).map(([id]) => id);
              // Import-only facades have zero rendered module length but still
              // emit a real import statement originating in the facade source.
              if (!ids.length && output.facadeModuleId && Object.hasOwn(output.modules, output.facadeModuleId)) {
                ids.push(output.facadeModuleId);
              }
              await record(join(directory, output.fileName), output.code, ids, 'vite-chunk');
            } else {
              const ids = ordered([...(output.originalFileNames ?? []), ...(cssSources.get(output.fileName) ?? [])]);
              const known = generated.get(digest(output.source));
              if (known) {
                if (isClient) rememberOutput(join(directory, output.fileName), output.source, known);
              } else if (ids.length) {
                const resolved = ids.map((id) => resolve(root, id));
                await api.registerGenerated(output.source, resolved);
                if (isClient) await record(join(directory, output.fileName), output.source, resolved, 'vite-asset');
              } else if (isClient) {
                throw new Error(`Unknown shipping asset provenance: ${output.fileName}`);
              }
            }
          }
        },
      },
      writeBundle(options, bundle) {
        for (const output of Object.values(bundle)) {
          const path = resolve(options.dir ?? outDir, output.fileName);
          const evidence = recorded.get(path);
          if (!evidence) continue;
          const bytes = output.type === 'chunk' ? output.code : output.source;
          if (evidence.sha256 !== digest(bytes)) throw new Error(`Asset changed during bundle write: ${path}`);
        }
      },
    },
  };
  return api;
}
