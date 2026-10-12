import { defineConfig, passthroughImageService } from 'astro/config';
import { unified } from '@astrojs/markdown-remark';
import mermaidSvg from './integrations/mermaid-svg.mjs';
import { fileURLToPath } from 'node:url';
import { docsProvenance } from './integrations/docs-provenance.mjs';
import { assertReferencesReady } from './integrations/reference-stage.mjs';
import { baseLinks } from './integrations/base-links.mjs';
import { docsBuildSettings } from './integrations/output-evidence.mjs';

process.env.PLAYWRIGHT_BROWSERS_PATH ??= fileURLToPath(new URL('./.playwright/', import.meta.url));
await assertReferencesReady(fileURLToPath(new URL('.', import.meta.url)), process.env.NACHOS_REFERENCE_RECEIPT);
const provenance = docsProvenance(fileURLToPath(new URL('.', import.meta.url)));
const { base, site } = docsBuildSettings();

export default defineConfig({
  site,
  base,
  image: { service: passthroughImageService() },
  markdown: {
    processor: unified({
      remarkPlugins: [[baseLinks, { base }]],
      rehypePlugins: [[mermaidSvg, { onRender: provenance.recordMermaid }]],
    }),
  },
  integrations: [
    await provenance.starlight({
      title: 'Nachos',
      description: 'Memory for stateful agents, built on .NET and Azure. An early clean-room reference implementation.',
      logo: { src: '../assets/nachos-logo.svg', replacesTitle: false },
      favicon: '/favicon.svg',
      customCss: ['./src/styles/custom.css'],
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/brendankowitz/nachos' }],
      expressiveCode: {},
      components: { MarkdownContent: './src/components/ReferenceMarkdownContent.astro' },
      sidebar: [
        { label: 'Getting started', items: [{ autogenerate: { directory: 'getting-started' } }] },
        { label: 'Concepts', items: [{ autogenerate: { directory: 'concepts' } }] },
        { label: 'Guides', items: [{ autogenerate: { directory: 'guides' } }] },
        { label: 'Reference', items: [{ autogenerate: { directory: 'reference' } }] },
        { label: 'Architecture & ADRs', slug: 'architecture' },
        { label: 'Contributing', slug: 'contributing' },
      ],
    }),
    provenance.integration,
  ],
});
