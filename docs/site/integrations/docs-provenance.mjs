import { join, relative } from 'node:path';
import { lstatSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { createRenderer } from 'rehype-expressive-code';
import starlight from '@astrojs/starlight';
import { pluginFrames } from '@expressive-code/plugin-frames';
import { pluginShiki } from '@expressive-code/plugin-shiki';
import { pluginTextMarkers } from '@expressive-code/plugin-text-markers';
import * as pagefind from 'pagefind';
import { createProvenance } from './bundle-provenance.mjs';
import { files, regularFile, safePath } from './output-evidence.mjs';

const require = createRequire(import.meta.url);
const { preprocessThemes } = require(fileURLToPath(new URL(
  './integrations/expressive-code/theming.js', import.meta.resolve('@astrojs/starlight'))));
const { resolveBinaryPath } = require(fileURLToPath(new URL('./resolveBinary.js', import.meta.resolve('pagefind'))));

export function docsProvenance(root) {
  if (process.env.PAGEFIND_BINARY_PATH || process.env.PAGEFIND_EXTENDED_BINARY_PATH || process.env.npm_config_arch) {
    throw new Error('Output provenance requires the locked, host-native Pagefind content supplier; overrides are unsupported');
  }
  const provenance = createProvenance(root);
  const source = (name) => fileURLToPath(import.meta.resolve(name));
  const starlightRoot = fileURLToPath(new URL('.', import.meta.resolve('@astrojs/starlight')));
  let build;
  let pagefindEnabled = true;
  let openapiConstructionSources = [];

  async function renderRenderer(options, nativeWrappers, inlineStyles = false) {
    if (JSON.stringify(options?.themes) !== JSON.stringify(preprocessThemes())) {
      throw new Error('Unregistered Expressive Code theme source; integrate explicit theme evidence before building');
    }
    if (!nativeWrappers && (options.plugins !== undefined &&
        (!Array.isArray(options.plugins) || options.plugins.flat().length))) {
      throw new Error('Unknown Expressive Code content producer: custom plugins are unsupported');
    }
    if (!nativeWrappers && Object.keys(options).some((key) => !['themes', 'plugins'].includes(key))) {
      throw new Error('Unregistered renderer configuration; use the supported default construction');
    }
    const bindings = [
      { plugin: pluginShiki(), source: source('@expressive-code/plugin-shiki') },
      { plugin: pluginTextMarkers(), source: source('@expressive-code/plugin-text-markers') },
      { plugin: pluginFrames(), source: source('@expressive-code/plugin-frames') },
    ];
    const renderer = await createRenderer({
      ...options,
      shiki: false, textMarkers: false, frames: false,
      plugins: [...bindings.map(({ plugin }) => plugin), ...(nativeWrappers ?? [])],
    });
    const themeSources = [
      join(starlightRoot, 'integrations', 'expressive-code', 'themes', 'night-owl-dark.js'),
      join(starlightRoot, 'integrations', 'expressive-code', 'themes', 'night-owl-light.js'),
    ];
    const css = [source('@expressive-code/core'), ...themeSources,
      ...bindings.filter(({ plugin }) => plugin.baseStyles).map(({ source }) => source)];
    const js = bindings.filter(({ plugin }) => plugin.jsModules).map(({ source }) => source);
    const html = [source('@expressive-code/core'), ...themeSources, ...bindings.map(({ source }) => source)];
    if (nativeWrappers) {
      html.push(join(starlightRoot, 'integrations', 'expressive-code', 'preprocessor.js'), source('astro-expressive-code'));
    }
    const stylesheet = `${renderer.baseStyles}${renderer.themeStyles}`;
    if (!inlineStyles) await provenance.registerGenerated(stylesheet, css, 'expressive-stylesheet');
    if (renderer.jsModules.length) {
      await provenance.registerGenerated([...new Set(renderer.jsModules)].join('\n'), js, 'expressive-script');
    }
    const render = renderer.ec.render.bind(renderer.ec);
    renderer.ec.render = async (...args) => {
      const result = await render(...args);
      await provenance.registerInline(JSON.stringify(result.renderedGroupAst), html, 'expressive-html');
      if (inlineStyles) await provenance.registerInline(stylesheet, css, 'expressive-inline-style');
      for (const style of result.styles) await provenance.registerInline(style, html, 'expressive-inline-style');
      return result;
    };
    return renderer;
  }

  return {
    async recordMermaid(svg, file) {
      await provenance.registerInline(svg, [
        file, fileURLToPath(import.meta.resolve('mermaid/dist/mermaid.js')),
      ], 'mermaid-svg');
    },
    createRenderer(options) {
      return renderRenderer(options);
    },
    async starlight(options) {
      pagefindEnabled = options.pagefind !== false;
      if (options.plugins !== undefined) {
        throw new Error('Unknown Expressive Code content producer: custom configuration plugins are unsupported');
      }
      try {
        lstatSync(join(root, 'ec.config.mjs'));
        throw new Error('Custom ec.config.mjs is unsupported by the default renderer provenance');
      } catch (error) {
        if (error.code !== 'ENOENT') throw error;
      }
      const ec = options.expressiveCode ?? {};
      if (typeof ec !== 'object' || Array.isArray(ec) ||
          Object.keys(ec).some((key) => key !== 'emitExternalStylesheet') ||
          (ec.emitExternalStylesheet !== undefined && typeof ec.emitExternalStylesheet !== 'boolean')) {
        throw new Error('Unknown Expressive Code content producer: custom configuration is unsupported');
      }
      let validated = false;
      let inlineStyles = false;
      const renderDefault = (rendererOptions) => {
        if (!validated || rendererOptions.plugins?.length !== 2) {
          throw new Error('Unknown Expressive Code wrapper construction');
        }
        return renderRenderer(rendererOptions, rendererOptions.plugins, inlineStyles);
      };
      // Its TypeScript entry must load while Astro's config runner is active.
      const { default: linksValidator } = await import('starlight-links-validator');
      const { default: starlightOpenAPI, openAPISidebarGroups } = await import('starlight-openapi');
      const schema = join(root, 'src/content/docs/reference/_generated/rest-api.json');
      const document = JSON.parse(await regularFile(schema));
      function localReferences(value) {
        if (!value || typeof value !== 'object') return;
        for (const [key, item] of Object.entries(value)) {
          if (key === '$ref' && (typeof item !== 'string' || !item.startsWith('#/'))) {
            throw new Error('The trusted OpenAPI plugin accepts only generated local schema references.');
          }
          localReferences(item);
        }
      }
      localReferences(document);
      // This known factory and fixed local schema are the only admitted OpenAPI construction.
      const openapi = starlightOpenAPI([{
        base: 'reference/rest-api',
        schema,
        sidebar: { label: 'REST declarations', operations: { labels: 'path', badges: true } },
        // Native Code serializes EC config, which cannot carry our private factory.
        // Do not reopen ec.config.mjs or admit caller plugins to manufacture examples.
        snippets: { operation: false, requestBody: false, response: false },
      }]);
      openapiConstructionSources = [
        source('starlight-openapi'),
        fileURLToPath(new URL('./libs/vite.ts', import.meta.resolve('starlight-openapi'))),
      ];
      return starlight({
        ...options,
        sidebar: [...(options.sidebar ?? []), ...openAPISidebarGroups],
        expressiveCode: { ...ec, removeUnusedThemes: false, customCreateRenderer: renderDefault },
        plugins: [openapi, linksValidator({ failOnError: true }), {
          name: 'nachos-default-renderer-provenance',
          hooks: {
            'config:setup': ({ config }) => {
              const configured = config.expressiveCode;
              if (!configured || configured.customCreateRenderer !== renderDefault ||
                  configured.removeUnusedThemes !== false ||
                  Object.keys(configured).some((key) => !['customCreateRenderer', 'emitExternalStylesheet', 'removeUnusedThemes'].includes(key)) ||
                  (configured.emitExternalStylesheet !== undefined && typeof configured.emitExternalStylesheet !== 'boolean')) {
                throw new Error('Unknown Expressive Code content producer: custom configuration is unsupported');
              }
              Object.freeze(configured);
              inlineStyles = configured.emitExternalStylesheet === false;
              validated = true;
            },
          },
        }],
      });
    },
    integration: {
      name: 'nachos-docs-provenance',
      hooks: {
        'astro:config:setup': ({ updateConfig }) => {
          updateConfig({ vite: { plugins: [provenance.vitePlugin] } });
        },
        'astro:config:done': ({ config }) => {
          if (fileURLToPath(config.outDir) !== join(root, 'dist') + (process.platform === 'win32' ? '\\' : '/')) {
            throw new Error('Output provenance requires the fixed docs/site/dist output directory');
          }
          build = { site: config.site ?? '', base: config.base };
        },
        'astro:build:done': async ({ dir, assets }) => {
          const output = fileURLToPath(dir);
          const logo = join(root, '..', 'assets', 'nachos-logo.svg');
          await provenance.registerGenerated(await regularFile(logo), [logo], 'copy');
          for (const file of await files(join(root, 'public'))) {
            await provenance.recordFile(join(output, relative(join(root, 'public'), file)), [file], 'copy');
          }
          const html = await provenance.renderEvidence(openapiConstructionSources);
          for (const urls of assets.values()) {
            for (const url of urls) {
              if (url.pathname.endsWith('.html')) await provenance.recordRendered(fileURLToPath(url), html);
            }
          }
          for (const name of ['sitemap-index.xml', 'sitemap-0.xml']) {
            await provenance.recordFile(join(output, name), [source('@astrojs/sitemap'), join(root, 'astro.config.mjs')], 'sitemap');
          }
          if (!pagefindEnabled) {
            await provenance.write(output, { pending: join(root, '.astro', 'nachos-output.pending.json'), build });
            return;
          }
          // writeFiles()+close() can leave native writes unfinished. Materialize
          // the returned bytes ourselves, then seal that exact final producer.
          try {
            const created = await pagefind.createIndex();
            if (created.errors.length) throw new Error(created.errors.join('\n'));
            const added = await created.index.addDirectory({ path: output });
            if (added.errors.length) throw new Error(added.errors.join('\n'));
            const generated = await created.index.getFiles();
            if (generated.errors.length) throw new Error(generated.errors.join('\n'));
            const names = new Set();
            const suppliers = [
              source('pagefind'), resolveBinaryPath(['pagefind_extended', 'pagefind']),
            ];
            for (const file of generated.files) {
              const name = safePath(file.path);
              if (names.has(name.toLowerCase())) throw new Error(`Duplicate Pagefind output path: ${name}`);
              names.add(name.toLowerCase());
              await provenance.emitGenerated(join(output, 'pagefind', name), file.content, suppliers, 'pagefind', html);
            }
          } finally {
            await pagefind.close();
          }
          await provenance.write(output, { pending: join(root, '.astro', 'nachos-output.pending.json'), build });
        },
      },
    },
  };
}
