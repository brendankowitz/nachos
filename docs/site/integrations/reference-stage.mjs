import { spawnSync } from 'node:child_process';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { dirname, isAbsolute, join, resolve, sep } from 'node:path';
import { digest, regularFile } from './output-evidence.mjs';

export const referenceNames = ['cli.md', 'compatibility.json', 'options.md', 'rest-api.json'];
const referenceDirectory = 'src/content/docs/reference/_generated';

export async function assertReferencesReady(site, receipt) {
  if (!receipt) throw new Error('Mandatory reference generation required: use npm run build or npm run dev.');
  const hashes = JSON.parse(receipt);
  if (JSON.stringify(Object.keys(hashes).sort()) !== JSON.stringify(referenceNames)) {
    throw new Error('Mandatory reference generation receipt is incomplete.');
  }
  for (const name of referenceNames) {
    if (digest(await regularFile(join(site, referenceDirectory, name))) !== hashes[name]) {
      throw new Error('Mandatory reference generation output changed.');
    }
  }
}

export async function generateReferences(repository, site) {
  const configuration = process.env.DOCS_CONFIGURATION ?? 'Debug';
  if (!['Debug', 'Release'].includes(configuration)) throw new Error('Invalid reference generation configuration.');
  const projects = ['src/Nachos.Api/Nachos.Api.csproj', 'src/Nachos.Cli/Nachos.Cli.csproj', 'eng/DocsGen/DocsGen.csproj'];
  try {
    for (const project of projects) await regularFile(join(repository, project));
  } catch {
    throw new Error('Mandatory reference generation requires all actual source producers.');
  }
  const home = join(repository, 'eng/DocsGen/obj/site-home');
  await mkdir(home, { recursive: true });
  const env = {};
  for (const key of ['SystemRoot', 'WINDIR', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432',
    'PATH', 'DOTNET_ROOT', 'NUGET_PACKAGES']) {
    if (process.env[key]) env[key] = process.env[key];
  }
  Object.assign(env, {
    HOME: home, USERPROFILE: home, APPDATA: home, LOCALAPPDATA: home, PROGRAMDATA: home, ALLUSERSPROFILE: home,
    DOTNET_CLI_HOME: home, TEMP: home, TMP: home, DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1',
    MSBUILDDISABLENODEREUSE: '1', DOTNET_CLI_USE_MSBUILD_SERVER: '0', UseSharedCompilation: 'false',
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: 'true', NO_COLOR: '1', LANG: 'en_US.UTF-8',
  });
  const config = join(home, 'NuGet.Config');
  await writeFile(config, '<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>');
  const dotnet = process.env.DOTNET_ROOT ? join(process.env.DOTNET_ROOT, process.platform === 'win32' ? 'dotnet.exe' : 'dotnet') : 'dotnet';
  function run(args) {
    const result = spawnSync(dotnet, args, { cwd: repository, env, encoding: 'utf8', timeout: 300_000, maxBuffer: 8 * 1024 * 1024 });
    const log = (result.stdout ?? '') + (result.stderr ?? '');
    // Only clean-environment first-party build diagnostics are forwarded, never ambient credentials.
    process.stdout.write(log);
    if (result.error) throw new Error('Mandatory reference generation process failed or exceeded its deadline.');
    return { status: result.status, log };
  }
  const openApi = join(repository, 'src/Nachos.Api/obj/Nachos.Api.json');
  // GenerateOpenApiDocuments is incremental against this file-list cache,
  // not the JSON itself. Remove both so even a no-op build must run discovery.
  for (const output of [openApi, join(repository, 'src/Nachos.Api/obj/Nachos.Api.OpenApiFiles.cache')]) {
    try {
      await regularFile(output);
      await rm(output);
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
    }
  }
  for (const project of projects) {
    let packages = env.NUGET_PACKAGES ?? join(home, '.nuget/packages');
    try {
      // Restored props can use $(UserProfile) for NuGetPackageRoot. Changing the
      // child home must not silently drop the API's build-time OpenAPI targets.
      const assets = JSON.parse(await readFile(join(repository, dirname(project), 'obj/project.assets.json'), 'utf8'));
      packages = assets.project.restore.packagesPath;
      if (typeof packages !== 'string' || !isAbsolute(packages)) throw new Error('Invalid restored package root.');
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
    }
    env.NuGetPackageRoot = resolve(packages) + sep;
    const args = ['build', project, '-c', configuration, '--no-restore', '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-v:minimal'];
    let result = run(args);
    if (result.status !== 0 && /error NETSDK1004:/.test(result.log)) {
      if (run(['restore', project, '--configfile', config, '--packages', packages, '-m:1', '-nr:false']).status !== 0) {
        throw new Error('Mandatory reference generation dependency restore failed.');
      }
      result = run(args);
    }
    if (result.status !== 0) throw new Error('Mandatory reference generation build failed.');
    if (project === projects[0]) {
      try { await regularFile(openApi); }
      catch { throw new Error('Mandatory reference generation: the API build did not emit fresh OpenAPI.'); }
    }
  }
  if (run([join(repository, `eng/DocsGen/bin/${configuration}/net10.0/DocsGen.dll`),
    '--root', repository, '--configuration', configuration]).status !== 0) {
    throw new Error('Mandatory reference generation failed.');
  }
  const source = join(repository, 'docs/site', referenceDirectory);
  const target = join(site, referenceDirectory);
  const bytes = await Promise.all(referenceNames.map((name) => regularFile(join(source, name))));
  // Isolated site test builds can consume the same real prepared repository, never fixture references.
  if (resolve(source) !== resolve(target)) {
    await mkdir(target, { recursive: true });
    for (let index = 0; index < referenceNames.length; index++) await writeFile(join(target, referenceNames[index]), bytes[index]);
  }
  const receipt = JSON.stringify(Object.fromEntries(referenceNames.map((name, index) => [name, digest(bytes[index])])));
  await assertReferencesReady(site, receipt);
  return receipt;
}
