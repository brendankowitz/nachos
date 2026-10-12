import { registerHooks } from 'node:module';

const forbidden = /(?:^|\/)(?:sharp|@img\/sharp[^/]*|@fortawesome\/fontawesome-free|mermaid-isomorphic|rehype-mermaid)(?:\/|$)/;
registerHooks({
  resolve(specifier, context, nextResolve) {
    if (forbidden.test(specifier.replaceAll('\\', '/'))) {
      throw new Error(`Forbidden image/font tooling loaded: ${specifier}`);
    }
    const result = nextResolve(specifier, context);
    if (forbidden.test(result.url)) throw new Error(`Forbidden image/font tooling loaded: ${result.url}`);
    return result;
  },
});
