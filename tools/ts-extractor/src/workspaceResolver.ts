import { Node, SourceFile, SyntaxKind } from 'ts-morph';
import * as path from 'path';
import * as ts from 'typescript';
import type {
  GraphNodeDto,
  PendingEdgeDto,
  ResolvedImportDto,
  ResolvedImportKind,
  UnresolvedImportDto,
  WorkspacePackageDto,
} from './protocol';
import { ExtractionWorkBudget } from './extractionWork';
import { viteLibrarySource } from './viteLibraryExports';
import type { DiscoveryScope } from './discoveryScope';
import { BoundedTextReader } from './boundedTextReader';

/** Absolute paths of the files named `fileName` below `dir`, at most `limit` of them. */
type ManifestFinder = (dir: string, fileName: string, limit: number) => string[];

const SKIP_DIRS = new Set(['node_modules', 'dist', 'build', 'coverage', '.git', '.angular']);
const MAX_TSCONFIGS = 200;
const MAX_WORKSPACE_PACKAGES = 500;
const MAX_IMPORT_RESOLUTIONS = 20000;
const MAX_BARREL_EXPORTS = 25;

interface TsConfigInfo {
  path: string;
  dir: string;
  relativePath: string;
  options: ts.CompilerOptions;
  references: string[];
  baseUrl?: string;
  paths?: Record<string, readonly string[]>;
  rootDirs?: readonly string[];
  moduleResolution?: string;
}

interface WorkspacePackageInfo {
  name: string;
  qualifiedName: string;
  rootPath: string;
  relativeRoot: string;
  kind: string;
  packageJsonPath?: string;
  tsconfigPath?: string;
}

interface ResolverContext {
  repositoryName: string;
  rootPath: string;
  tsconfigs: TsConfigInfo[];
  fallbackConfig: TsConfigInfo;
  workspacePackages: WorkspacePackageInfo[];
  /** Workspace packages by name; several when same-named packages live in different directories. */
  packagesByName: Map<string, WorkspacePackageInfo[]>;
  sourceFilesByPath: Map<string, SourceFile>;
  moduleHost: ts.ModuleResolutionHost;
  diagnostics: string[];
  reader: BoundedTextReader;
  work: ExtractionWorkBudget;
}

export interface WorkspaceResolutionResult {
  nodes: GraphNodeDto[];
  edges: PendingEdgeDto[];
  workspacePackages: WorkspacePackageDto[];
  resolvedImports: ResolvedImportDto[];
  unresolvedImports: UnresolvedImportDto[];
  diagnostics: string[];
}

export function analyzeWorkspaceImports(
  repositoryName: string,
  rootPath: string,
  requestedTsconfigPath: string,
  sourceFiles: SourceFile[],
  log: (msg: string) => void = () => {},
  scope?: DiscoveryScope,
  reader = new BoundedTextReader(rootPath, scope ? file => scope.includesFile(file) : undefined, {}, log),
  work = new ExtractionWorkBudget(),
): WorkspaceResolutionResult {
  const diagnostics: string[] = [];
  const diag = (msg: string) => {
    diagnostics.push(msg);
    log(msg);
  };

  const normalizedRoot = normalizeAbsolute(rootPath);
  // With a discovery scope, manifests come from the admitted files only, never a disk walk.
  const findManifests: ManifestFinder = (dir, fileName, limit) => scope
    ? scope.absoluteFiles(file =>
        path.posix.basename(file) === fileName &&
        !file.split('/').some(segment => SKIP_DIRS.has(segment)))
      .map(normalizeAbsolute)
      .filter(file => isInside(dir, file))
      .slice(0, limit)
    : findFilesByName(dir, fileName, limit, reader);
  const tsconfigs = discoverTsConfigs(normalizedRoot, requestedTsconfigPath, diag, findManifests, scope, reader, work);
  const fallbackConfig = pickFallbackConfig(tsconfigs, requestedTsconfigPath);
  const workspacePackages = discoverWorkspacePackages(
    repositoryName,
    normalizedRoot,
    tsconfigs,
    diag,
    findManifests,
    reader,
    scope,
  ).filter(pkg => !scope || scope.includesDirectory(pkg.relativeRoot));
  qualifyCollidingPackages(repositoryName, workspacePackages);

  const sourceFilesByPath = new Map<string, SourceFile>();
  for (const sourceFile of sourceFiles) {
    sourceFilesByPath.set(normalizeAbsolute(sourceFile.getFilePath()), sourceFile);
  }

  const ctx: ResolverContext = {
    repositoryName,
    rootPath: normalizedRoot,
    tsconfigs,
    fallbackConfig,
    workspacePackages,
    packagesByName: groupPackagesByName(workspacePackages),
    sourceFilesByPath,
    moduleHost: {
      fileExists: file => sourceFilesByPath.has(normalizeAbsolute(file)) ||
        (/(?:package|[tj]sconfig[^/]*)\.json$/.test(file) && reader.exists(file)),
      readFile: file => reader.read(file),
      directoryExists: directory => scope ? scope.includesDirectory(toRelative(normalizedRoot, directory)) : reader.directoryExists(directory),
      realpath: file => file,
      getCurrentDirectory: () => normalizedRoot,
    },
    diagnostics,
    reader,
    work,
  };

  const nodes = buildWorkspacePackageNodes(workspacePackages, ctx);
  const edges = buildWorkspacePackageContainmentEdges(repositoryName, workspacePackages, work);
  const resolvedImports: ResolvedImportDto[] = [];
  const unresolvedImports: UnresolvedImportDto[] = [];

  let resolutionCount = 0;
  for (const sourceFile of sourceFiles) {
    const absPath = normalizeAbsolute(sourceFile.getFilePath());
    const relPath = toRelative(rootPath, absPath);
    if (shouldSkipRelativePath(relPath)) continue;

    const fileQN = `${repositoryName}:${relPath}`;
    const sourcePackage = findOwningPackage(workspacePackages, absPath);

    const specs = collectImportSpecs(sourceFile);
    for (const spec of specs) {
      if (resolutionCount >= MAX_IMPORT_RESOLUTIONS) {
        diag(`moduleResolution: stopped after ${MAX_IMPORT_RESOLUTIONS} import specifiers`);
        break;
      }

      resolutionCount++;
      const resolved = resolveImportSpec(ctx, absPath, fileQN, relPath, sourcePackage, spec);
      resolvedImports.push(resolved);
      addGraphEdgesForResolvedImport(edges, repositoryName, fileQN, sourcePackage, resolved, work);
      if (resolved.classification === 'unresolved_alias' ||
          resolved.classification === 'unsupported_dynamic_expression') {
        unresolvedImports.push(work.createMetadata(()=>({fileQN,importedNamespace:resolved.importedNamespace})));
      }
    }
  }

  nodes.push(...buildExternalPackageNodes(resolvedImports, work));

  diag(`workspaceDiscovery: ${workspacePackages.length} packages, ${tsconfigs.length} tsconfig files`);
  diag(`moduleResolution: ${resolvedImports.length} import/export/dynamic specifiers`);

  return {
    nodes,
    edges: dedupeEdges(edges),
    workspacePackages: workspacePackages.map(pkg=>work.createMetadata(()=>toWorkspacePackageDto(pkg))),
    resolvedImports,
    unresolvedImports,
    diagnostics,
  };
}

function discoverTsConfigs(
  rootPath: string,
  requestedTsconfigPath: string,
  diag: (msg: string) => void,
  findManifests: ManifestFinder,
  scope: DiscoveryScope | undefined,
  reader: BoundedTextReader,
  work: ExtractionWorkBudget,
): TsConfigInfo[] {
  const discovered = [...findManifests(rootPath, 'tsconfig.json', MAX_TSCONFIGS + 1), ...findManifests(rootPath, 'jsconfig.json', MAX_TSCONFIGS + 1)];
  const normalizedRequested = normalizeAbsolute(requestedTsconfigPath);
  if (!discovered.includes(normalizedRequested) && (!scope || scope.includesFile(normalizedRequested)) && reader.exists(normalizedRequested)) {
    discovered.unshift(normalizedRequested);
  }

  if (discovered.length > MAX_TSCONFIGS) {
    diag(`tsconfigDiscovery: limited from ${discovered.length} to ${MAX_TSCONFIGS} files`);
  }

  const configs: TsConfigInfo[] = []; const extendedConfigCache=new Map<string,ts.ExtendedConfigCacheEntry>();
  for (const configPath of discovered.slice(0, MAX_TSCONFIGS)) {
    const parsed = parseTsConfig(configPath, rootPath, diag, scope, reader, work, extendedConfigCache);
    if (parsed) configs.push(parsed);
  }

  if (configs.length === 0) {
    configs.push({
      path: normalizedRequested,
      dir: path.dirname(normalizedRequested),
      relativePath: toRelative(rootPath, normalizedRequested),
      options: {
        allowJs: true,
        skipLibCheck: true,
        noResolve: true,
        moduleResolution: ts.ModuleResolutionKind.NodeJs,
        target: ts.ScriptTarget.ES2020,
      },
      references: [],
    });
  }

  configs.sort((a, b) => b.dir.length - a.dir.length);
  return configs;
}

function parseTsConfig(
  configPath: string,
  rootPath: string,
  diag: (msg: string) => void,
  scope: DiscoveryScope | undefined,
  reader: BoundedTextReader,
  work: ExtractionWorkBudget,
  extendedConfigCache: Map<string,ts.ExtendedConfigCacheEntry>,
): TsConfigInfo | null {
  let budgetFailure:unknown;
  const host: ts.ParseConfigFileHost = {
    useCaseSensitiveFileNames: ts.sys.useCaseSensitiveFileNames,
    getCurrentDirectory: ts.sys.getCurrentDirectory,
    fileExists: file => reader.exists(file),
    readFile: file => {
      try { work.charge(0); const text=reader.read(file); if(text===undefined) return undefined;
      // The compiler's config API owns extends resolution. Preflight with the same JSON
      // parser, then reserve its exact hidden parse before returning text to that API.
      const source=work.parse(text,()=>ts.parseJsonText(file,text),value=>value,(node,visit)=>ts.forEachChild(node as ts.Node,child=>{visit(child);}));
      const count=work.accountAst(source,(node,visit)=>ts.forEachChild(node as ts.Node,child=>{visit(child);}));
      work.reserveSource(text.length); work.reserveAstNodes(count); return text;
      } catch(error) { if((error as {code?:string}).code==='extraction_work_incomplete') budgetFailure=error; throw error; }
    },
    // With a discovery scope, include/exclude globs see the admitted files; without one the
    // config's file list is never walked (sources are discovered separately).
    readDirectory: scope ? scopedReadDirectory(scope) : () => [],
    onUnRecoverableConfigFileDiagnostic: diagnostic => {
      if(budgetFailure) throw budgetFailure;
      diag(`tsconfig ${toRelative(rootPath, configPath)}: ${flattenDiagnostic(diagnostic)}`);
    },
  };

  const parsed = ts.getParsedCommandLineOfConfigFile(
    configPath,
    {
      noResolve: true,
      skipLibCheck: true,
      allowJs: true,
      types: [],
    },
    host,
    extendedConfigCache,
  );

  // TypeScript translates readFile exceptions into diagnostics; budget refusal must escape.
  if(budgetFailure) throw budgetFailure;
  if (!parsed) return null;
  for (const error of parsed.errors) {
    // Without a scope no directory is read, so "No inputs were found" would always be false.
    if (!scope && error.code === NO_INPUTS_FOUND_DIAGNOSTIC) continue;
    diag(`tsconfig ${toRelative(rootPath, configPath)}: ${flattenDiagnostic(error)}`);
  }

  const relativePath = toRelative(rootPath, configPath);
  const references = (parsed.projectReferences ?? [])
    .map(reference => toRelative(rootPath, normalizeAbsolute(reference.path)));

  return {
    path: normalizeAbsolute(configPath),
    dir: normalizeAbsolute(path.dirname(configPath)),
    relativePath,
    options: parsed.options,
    references,
    baseUrl: parsed.options.baseUrl ? toRelative(rootPath, parsed.options.baseUrl) : undefined,
    paths: parsed.options.paths,
    rootDirs: parsed.options.rootDirs?.map(dir => toRelative(rootPath, dir)),
    moduleResolution: moduleResolutionName(parsed.options.moduleResolution),
  };
}

/** TS18003: "No inputs were found in config file ...". */
const NO_INPUTS_FOUND_DIAGNOSTIC = 18003;

type MatchFiles = (
  path: string,
  extensions: readonly string[] | undefined,
  excludes: readonly string[] | undefined,
  includes: readonly string[] | undefined,
  useCaseSensitiveFileNames: boolean,
  currentDirectory: string,
  depth: number | undefined,
  getFileSystemEntries: (path: string) => { files: readonly string[]; directories: readonly string[] },
  realpath: (path: string) => string,
) => string[];

/**
 * A `readDirectory` that lists only the files of the discovery scope, matched against a
 * tsconfig's include/exclude globs with TypeScript's own matcher (as `ts.sys.readDirectory` does
 * against the disk), so a config whose sources are admitted is not reported as having no inputs.
 */
function scopedReadDirectory(scope: DiscoveryScope): ts.ParseConfigFileHost['readDirectory'] {
  const matchFiles = (ts as unknown as { matchFiles?: MatchFiles }).matchFiles;
  return (rootDir, extensions, excludes, includes, depth) => {
    if (typeof matchFiles === 'function') {
      return matchFiles(
        rootDir, extensions, excludes, includes, ts.sys.useCaseSensitiveFileNames,
        ts.sys.getCurrentDirectory(), depth, dir => scope.fileSystemEntries(dir), p => p);
    }
    // Fallback should the internal matcher disappear: admitted files below rootDir by extension.
    const root = normalizeAbsolute(rootDir);
    return scope.absoluteFiles()
      .map(normalizeAbsolute)
      .filter(file => isInside(root, file) && extensions.some(extension => file.endsWith(extension)));
  };
}

function pickFallbackConfig(configs: TsConfigInfo[], requestedTsconfigPath: string): TsConfigInfo {
  const requested = normalizeAbsolute(requestedTsconfigPath);
  return configs.find(config => config.path === requested) ?? configs[0];
}

function discoverWorkspacePackages(
  repositoryName: string,
  rootPath: string,
  tsconfigs: TsConfigInfo[],
  diag: (msg: string) => void,
  findManifests: ManifestFinder,
  reader: BoundedTextReader,
  scope: DiscoveryScope | undefined,
): WorkspacePackageInfo[] {
  const packagesByRoot = new Map<string, WorkspacePackageInfo>();
  const packageJsonPaths = findManifests(rootPath, 'package.json', MAX_WORKSPACE_PACKAGES + 1);
  if (packageJsonPaths.length > MAX_WORKSPACE_PACKAGES) {
    diag(`workspaceDiscovery: limited package.json scan from ${packageJsonPaths.length} to ${MAX_WORKSPACE_PACKAGES}`);
  }

  const rootPackageJson = path.join(rootPath, 'package.json');
  const workspacePatterns = [
    ...readPackageJsonWorkspacePatterns(rootPackageJson, reader),
    ...readPnpmWorkspacePatterns(path.join(rootPath, 'pnpm-workspace.yaml'), reader),
  ];
  const workspaceRoots = expandWorkspacePatterns(rootPath, workspacePatterns, findManifests, reader, scope);

  for (const packageJsonPath of packageJsonPaths.slice(0, MAX_WORKSPACE_PACKAGES)) {
    const pkg = readJsonFile(packageJsonPath, reader);
    const packageName = typeof pkg?.name === 'string' ? pkg.name : null;
    const packageRoot = normalizeAbsolute(path.dirname(packageJsonPath));
    const isRoot = packageRoot === rootPath;
    const matchesWorkspace = workspaceRoots.some(root => root === packageRoot);
    if (!packageName && !matchesWorkspace && !isRoot) continue;

    const inferredName = packageName ?? path.basename(packageRoot);
    addWorkspacePackage(packagesByRoot, {
      name: inferredName,
      qualifiedName: buildWorkspacePackageQN(repositoryName, inferredName),
      rootPath: packageRoot,
      relativeRoot: toRelative(rootPath, packageRoot),
      kind: isRoot ? 'root_package' : 'workspace_package',
      packageJsonPath: toRelative(rootPath, packageJsonPath),
      tsconfigPath: findNearestTsConfigForRoot(tsconfigs, packageRoot, rootPath),
    });
  }

  addAngularWorkspacePackages(repositoryName, rootPath, packagesByRoot, reader);

  for (const config of tsconfigs) {
    for (const reference of config.references) {
      const referencedPath = normalizeAbsolute(path.join(rootPath, reference));
      const referencedRoot = path.basename(referencedPath).startsWith('tsconfig.')
        ? normalizeAbsolute(path.dirname(referencedPath))
        : referencedPath;
      if (!isInside(rootPath, referencedRoot)) continue;
      addWorkspacePackage(packagesByRoot, {
        name: path.basename(referencedRoot),
        qualifiedName: buildWorkspacePackageQN(repositoryName, path.basename(referencedRoot)),
        rootPath: referencedRoot,
        relativeRoot: toRelative(rootPath, referencedRoot),
        kind: 'project_reference',
        tsconfigPath: findNearestTsConfigForRoot(tsconfigs, referencedRoot, rootPath),
      });
    }
  }

  return [...packagesByRoot.values()]
    .sort((a, b) => a.relativeRoot.localeCompare(b.relativeRoot));
}

function addWorkspacePackage(
  packagesByRoot: Map<string, WorkspacePackageInfo>,
  candidate: WorkspacePackageInfo,
): void {
  const existing = packagesByRoot.get(candidate.rootPath);
  if (!existing) {
    packagesByRoot.set(candidate.rootPath, candidate);
    return;
  }

  packagesByRoot.set(candidate.rootPath, {
    ...existing,
    name: existing.name || candidate.name,
    qualifiedName: existing.qualifiedName || candidate.qualifiedName,
    kind: mergePackageKinds(existing.kind, candidate.kind),
    packageJsonPath: existing.packageJsonPath ?? candidate.packageJsonPath,
    tsconfigPath: existing.tsconfigPath ?? candidate.tsconfigPath,
  });
}

function addAngularWorkspacePackages(
  repositoryName: string,
  rootPath: string,
  packagesByRoot: Map<string, WorkspacePackageInfo>,
  reader: BoundedTextReader,
): void {
  for (const workspaceFile of ['angular.json', 'workspace.json']) {
    const workspacePath = path.join(rootPath, workspaceFile);
    const workspace = readJsonFile(workspacePath, reader);
    const projects = workspace?.projects;
    if (!projects || typeof projects !== 'object') continue;

    for (const [name, value] of Object.entries(projects as Record<string, unknown>)) {
      if (!value || typeof value !== 'object') continue;
      const project = value as Record<string, unknown>;
      const rawRoot = typeof project.root === 'string' ? project.root : null;
      const rawSourceRoot = typeof project.sourceRoot === 'string' ? project.sourceRoot : null;
      const root = normalizeAbsolute(path.join(rootPath, rawRoot || rawSourceRoot || name));
      if (!isInside(rootPath, root)) continue;

      const projectType = typeof project.projectType === 'string'
        ? project.projectType
        : root.includes('/libs/') || root.startsWith(path.join(rootPath, 'libs')) ? 'library' : 'application';
      addWorkspacePackage(packagesByRoot, {
        name,
        qualifiedName: buildWorkspacePackageQN(repositoryName, name),
        rootPath: root,
        relativeRoot: toRelative(rootPath, root),
        kind: projectType === 'library' ? 'angular_library' : 'angular_application',
        tsconfigPath: undefined,
      });
    }
  }
}

function buildWorkspacePackageNodes(packages: WorkspacePackageInfo[], ctx: ResolverContext): GraphNodeDto[] {
  const work=ctx.work;
  return packages.map(pkg => (work.createNode(()=>({
    label: 'Module',
    name: pkg.name,
    qualifiedName: pkg.qualifiedName,
    filePath: pkg.relativeRoot,
    startLine: 0,
    endLine: 0,
    properties: {
      module_kind: 'typescript_workspace_package',
      package_name: pkg.name,
      package_entrypoints: packageEntryPoints(pkg, ctx),
      workspace_kind: pkg.kind,
      root_path: pkg.relativeRoot,
      ...(pkg.packageJsonPath ? { package_json_path: pkg.packageJsonPath } : {}),
      ...(pkg.tsconfigPath ? { tsconfig_path: pkg.tsconfigPath } : {}),
    },
  }))));
}

/** Public package entry identities, resolved only against already admitted ASTs. No dist or
 * node_modules reads are needed. An explicit compiler rootDir/outDir mapping can prove where a
 * manifest's generated JS entry originates; missing/alternative targets remain visible. */
function packageEntryPoints(pkg: WorkspacePackageInfo, ctx: ResolverContext): Array<{ import_path: string; file_path?: string }> {
  const work=ctx.work;
  if (!pkg.packageJsonPath) return [];
  const manifest = readJsonFile(path.join(ctx.rootPath, pkg.packageJsonPath), ctx.reader);
  if (!manifest || typeof manifest.name !== 'string' || manifest.name !== pkg.name) return [];
  const entries = new Map<string, string[]>();
  const runtimeTargets = (value: unknown, depth=0): string[] => {
    work.checkDepth(depth,128);
    if (typeof value === 'string') { work.text(value); return [value]; }
    if (Array.isArray(value)) { work.charge(value.length+1); return value.flatMap(item=>runtimeTargets(item,depth+1)); }
    if (!value || typeof value !== 'object') return [];
    const keys=Object.keys(value); work.charge(keys.length+1); const values:string[]=[];
    for(const key of keys) { work.text(key); if(key!=='types') { const nested=runtimeTargets((value as Record<string,unknown>)[key],depth+1); work.charge(nested.length+1); for(const target of nested) values.push(target); } }
    return values;
  };
  if (manifest.exports !== undefined) {
    const exports = manifest.exports;
    if (exports && typeof exports === 'object' && !Array.isArray(exports) && Object.keys(exports).some(key => key.startsWith('.'))) {
      for (const [subpath, target] of Object.entries(exports))
        if (subpath === '.' || subpath.startsWith('./')) entries.set(subpath, runtimeTargets(target));
    } else entries.set('.', runtimeTargets(exports));
  } else {
    const targets = [manifest.source, manifest.module, manifest.main].filter((value): value is string => typeof value === 'string');
    entries.set('.', targets.length ? targets : ['./index']);
  }
  const config = ctx.tsconfigs.find(config => config.relativePath === pkg.tsconfigPath);
  const emittedPath = (source: string, output: string): string | undefined => {
    work.charge(source.length+output.length+(config?.options.rootDir?.length??0)+8);
    if (!config?.options.rootDir || !isInside(normalizeAbsolute(config.options.rootDir), source)) return undefined;
    const jsx = /\.[jt]sx$/.test(source);
    if (jsx && config.options.jsx === undefined) return undefined;
    const extension = jsx && config.options.jsx === ts.JsxEmit.Preserve ? '.jsx' : '.js';
    return normalizeAbsolute(path.join(output, path.relative(config.options.rootDir, source))).replace(/\.[jt]sx?$/, extension);
  };
  const sourceCandidates = (absolute: string): string[] => {
    work.charge(absolute.length+pkg.rootPath.length+16);
    if (ctx.sourceFilesByPath.has(absolute)) return [absolute];
    const candidates = viteLibrarySource(pkg.rootPath, ctx.sourceFilesByPath, absolute,
      message => ctx.diagnostics.push(message));
    if (!path.extname(absolute)) candidates.push(...['.ts', '.tsx', '.js', '.jsx'].map(extension => absolute + extension),
      ...['index.ts', 'index.tsx', 'index.js', 'index.jsx'].map(index => path.join(absolute, index)));
    if (config?.options.rootDir && config.options.outDir && isInside(normalizeAbsolute(config.options.outDir), absolute)) {
      const stem = normalizeAbsolute(path.join(config.options.rootDir, path.relative(config.options.outDir, absolute))).replace(/\.[jt]sx?$/, '');
      for (const extension of ['.ts', '.tsx', '.js', '.jsx']) {
        const source = stem + extension;
        if (emittedPath(source, config.options.outDir) === absolute) candidates.push(source);
      }
    }
    return [...new Set(candidates)].filter(candidate => isInside(ctx.rootPath, candidate) && ctx.sourceFilesByPath.has(candidate));
  };
  const result: Array<{ import_path: string; file_path?: string }> = [];
  for (const [subpath, targets] of entries) {
    work.charge(subpath.length+pkg.name.length+targets.length+8);
    const importPath = subpath === '.' ? pkg.name : `${pkg.name}${subpath.slice(1)}`;
    if (!targets.length) { result.push(work.createMetadata(()=>({ import_path: importPath }))); continue; }
    const distinctTargets = [...new Set(targets)];
    const wildcardTargets = new Map<string, Set<string>>();
    for (const target of distinctTargets) {
      work.charge(target.length+pkg.rootPath.length+subpath.length+8);
      if (!target.startsWith('./') && manifest.exports !== undefined) { result.push(work.createMetadata(()=>({ import_path: importPath }))); continue; }
      const absolute = normalizeAbsolute(path.join(pkg.rootPath, target));
      if (!isInside(pkg.rootPath, absolute)) { result.push(work.createMetadata(()=>({ import_path: importPath }))); continue; }
      if (subpath.includes('*') || target.includes('*')) {
        // Expand a single public wildcard from the admitted source universe, never a filesystem glob.
        if (subpath.split('*').length !== 2 || target.split('*').length !== 2) { result.push(work.createMetadata(()=>({ import_path: importPath }))); continue; }
        const [prefix, suffix] = absolute.split('*');
        for (const admitted of ctx.sourceFilesByPath.keys()) {
          // Unmatched pairs still perform mapping and comparison work; reserve before arrays/paths.
          work.charge(admitted.length+prefix.length+suffix.length+(config?.options.rootDir?.length??0)+(config?.options.outDir?.length??0)+16);
          const emitted = [admitted];
          if (config?.options.rootDir && config.options.outDir && isInside(normalizeAbsolute(config.options.rootDir), admitted))
            { const compiled = emittedPath(admitted, config.options.outDir); if (compiled) emitted.push(compiled); }
          for (const candidate of emitted) {
            work.charge(candidate.length+prefix.length+suffix.length+8);
            if (candidate.startsWith(prefix) && candidate.endsWith(suffix)) {
            work.charge(candidate.length+importPath.length+admitted.length+ctx.rootPath.length+16);
            const wildcard = candidate.slice(prefix.length, candidate.length - suffix.length || undefined);
            const publicPath = importPath.replace('*', wildcard);
            result.push(work.createMetadata(()=>({ import_path: publicPath, file_path: toRelative(ctx.rootPath, admitted) })));
            if (!wildcardTargets.has(publicPath)) wildcardTargets.set(publicPath, new Set());
            wildcardTargets.get(publicPath)!.add(target);
            }
          }
        }
      } else {
        const candidates = sourceCandidates(absolute);
        if (!candidates.length) result.push(work.createMetadata(()=>({ import_path: importPath })));
        for (const candidate of candidates) result.push(work.createMetadata(()=>({ import_path: importPath, file_path: toRelative(ctx.rootPath, candidate) })));
      }
    }
    if (subpath.includes('*')) for (const [publicPath, resolvedTargets] of wildcardTargets)
      if (resolvedTargets.size !== distinctTargets.length) result.push(work.createMetadata(()=>({ import_path: publicPath })));
  }
  work.charge(result.length+1);
  return [...new Map(result.map(entry => { work.charge(entry.import_path.length+(entry.file_path?.length??0)+2); return [`${entry.import_path}\0${entry.file_path ?? ''}`, entry] as const; })).values()]
    .sort((a, b) => { work.charge(a.import_path.length+b.import_path.length+(a.file_path?.length??0)+(b.file_path?.length??0)+4); return a.import_path.localeCompare(b.import_path) || (a.file_path ?? '').localeCompare(b.file_path ?? ''); });
}

function buildWorkspacePackageContainmentEdges(
  repositoryName: string,
  packages: WorkspacePackageInfo[],
  work: ExtractionWorkBudget,
): PendingEdgeDto[] {
  return packages.map(pkg => (work.createEdge(()=>({
    sourceQN: repositoryName,
    targetQN: pkg.qualifiedName,
    type: 'CONTAINS_PROJECT',
    properties: {
      workspace_kind: pkg.kind,
      root_path: pkg.relativeRoot,
    },
  }))));
}

function buildExternalPackageNodes(resolvedImports: ResolvedImportDto[], work:ExtractionWorkBudget): GraphNodeDto[] {
  const packages = new Map<string, string>();
  for (const resolved of resolvedImports) {
    if (resolved.classification !== 'external_npm_package' ||
        !resolved.targetPackageQN ||
        !resolved.externalPackageName) {
      continue;
    }

    packages.set(resolved.targetPackageQN, resolved.externalPackageName);
  }

  return [...packages.entries()].map(([qualifiedName, name]) => (work.createNode(()=>({
    label: 'NuGetPackage',
    name,
    qualifiedName,
    filePath: '',
    startLine: 0,
    endLine: 0,
    properties: {
      package_type: 'npm',
    },
  }))));
}

function collectImportSpecs(sourceFile: SourceFile): Array<{ specifier: string; kind: ResolvedImportKind }> {
  const specs: Array<{ specifier: string; kind: ResolvedImportKind }> = [];

  for (const importDecl of sourceFile.getImportDeclarations()) {
    specs.push({ specifier: importDecl.getModuleSpecifierValue(), kind: 'static' });
  }

  for (const exportDecl of sourceFile.getExportDeclarations()) {
    const specifier = exportDecl.getModuleSpecifierValue();
    if (specifier) specs.push({ specifier, kind: 'export' });
  }

  for (const call of sourceFile.getDescendantsOfKind(SyntaxKind.CallExpression)) {
    if (call.getExpression().getKind() !== SyntaxKind.ImportKeyword) continue;
    const args = call.getArguments();
    const firstArg = args[0];
    if (!firstArg) {
      specs.push({ specifier: '<dynamic expression>', kind: 'dynamic' });
    } else if (Node.isStringLiteral(firstArg) || Node.isNoSubstitutionTemplateLiteral(firstArg)) {
      specs.push({ specifier: firstArg.getLiteralValue() as string, kind: 'dynamic' });
    } else {
      specs.push({ specifier: '<dynamic expression>', kind: 'dynamic' });
    }
  }

  // Angular's pre-import() lazy syntax still goes through the normal scoped module resolver.
  // Keep the export suffix out of the module specifier; route analysis resolves that export.
  for (const property of sourceFile.getDescendantsOfKind(SyntaxKind.PropertyAssignment)) {
    if (property.getName() !== 'loadChildren') continue;
    const initializer = property.getInitializer();
    if (initializer && (Node.isStringLiteral(initializer) || Node.isNoSubstitutionTemplateLiteral(initializer))) {
      const specifier = initializer.getLiteralText().split('#')[0];
      if (specifier) specs.push({ specifier, kind: 'dynamic' });
    }
  }

  return specs;
}

function resolveImportSpec(
  ctx: ResolverContext,
  importerPath: string,
  fileQN: string,
  filePath: string,
  sourcePackage: WorkspacePackageInfo | undefined,
  spec: { specifier: string; kind: ResolvedImportKind },
): ResolvedImportDto {
  ctx.work.reserveOutput('metadata',fileQN.length+filePath.length+spec.specifier.length);
  if (spec.specifier === '<dynamic expression>') {
    return {
      fileQN,
      filePath,
      importedNamespace: spec.specifier,
      importKind: spec.kind,
      classification: 'unsupported_dynamic_expression',
      diagnostic: 'dynamic import expression is not a string literal',
    };
  }

  const config = findConfigForFile(ctx.tsconfigs, ctx.fallbackConfig, importerPath);
  // The compiler rejects upper-case JS/TS suffixes. An explicit relative specifier can
  // still name one exact admitted source; preserve its case without probing or guessing.
  const literalRelativeTarget = /^\.{1,2}\//.test(spec.specifier)
    ? normalizeAbsolute(path.resolve(path.dirname(importerPath), spec.specifier)) : undefined;
  const resolved = ts.resolveModuleName(
    spec.specifier,
    importerPath,
    config.options,
    ctx.moduleHost,
  ).resolvedModule ?? (literalRelativeTarget && ctx.sourceFilesByPath.has(literalRelativeTarget)
    ? { resolvedFileName: literalRelativeTarget } : undefined);

  const localPackage = findWorkspacePackageBySpecifier(ctx.packagesByName, spec.specifier, importerPath);
  if (!resolved && localPackage) {
    const exportedPath = resolveWorkspacePackageExport(localPackage, spec.specifier, ctx.reader);
    const exportedFilePath = exportedPath && ctx.sourceFilesByPath.has(normalizeAbsolute(exportedPath)) && isInside(ctx.rootPath, exportedPath)
      ? toRelative(ctx.rootPath, exportedPath)
      : undefined;
    return {
      fileQN,
      filePath,
      importedNamespace: spec.specifier,
      importKind: spec.kind,
      classification: 'local_workspace_package',
      resolvedFilePath: exportedFilePath,
      targetFileQN: exportedFilePath ? `${ctx.repositoryName}:${exportedFilePath}` : undefined,
      targetWorkspacePackage: localPackage.name,
      targetPackageQN: localPackage.qualifiedName,
      diagnostic: exportedFilePath
        ? undefined
        : 'resolved to workspace package name without a concrete file',
    };
  }

  if (!resolved) {
    const diagnostic = `unresolved import '${spec.specifier}' from ${filePath}`;
    const classification = spec.specifier.startsWith('.') || spec.specifier.startsWith('/') || looksLikeAlias(config, spec.specifier) ? 'unresolved_alias' : 'external_npm_package';
    if (classification === 'external_npm_package') {
      const packageName = getPackageNameFromSpecifier(spec.specifier);
      return {
        fileQN,
        filePath,
        importedNamespace: spec.specifier,
        importKind: spec.kind,
        classification,
        externalPackageName: packageName,
        targetPackageQN: buildNpmPackageQN(packageName),
        diagnostic,
      };
    }

    return {
      fileQN,
      filePath,
      importedNamespace: spec.specifier,
      importKind: spec.kind,
      classification,
      diagnostic,
    };
  }

  const resolvedPath = normalizeAbsolute(resolved.resolvedFileName);
  if (isNodeModulePath(resolvedPath)) {
    const packageName = getPackageNameFromSpecifier(spec.specifier);
    return {
      fileQN,
      filePath,
      importedNamespace: spec.specifier,
      importKind: spec.kind,
      classification: 'external_npm_package',
      resolvedFilePath: toRelative(ctx.rootPath, resolvedPath),
      externalPackageName: packageName,
      targetPackageQN: buildNpmPackageQN(packageName),
    };
  }

  if (isInside(ctx.rootPath, resolvedPath)) {
    const resolvedFilePath = toRelative(ctx.rootPath, resolvedPath);
    const targetPackage = findOwningPackage(ctx.workspacePackages, resolvedPath);
    const isCrossWorkspacePackage = !!targetPackage &&
      targetPackage.kind !== 'root_package' &&
      targetPackage.qualifiedName !== sourcePackage?.qualifiedName;
    const barrelTarget = findSingleBarrelExportTarget(ctx, resolvedPath);
    const targetFilePath = barrelTarget ?? resolvedFilePath;

    return {
      fileQN,
      filePath,
      importedNamespace: spec.specifier,
      importKind: spec.kind,
      classification: isCrossWorkspacePackage ? 'local_workspace_package' : 'local_file',
      resolvedFilePath,
      targetFileQN: `${ctx.repositoryName}:${targetFilePath}`,
      targetWorkspacePackage: isCrossWorkspacePackage ? targetPackage.name : undefined,
      targetPackageQN: isCrossWorkspacePackage ? targetPackage.qualifiedName : undefined,
      isBarrel: !!barrelTarget,
      barrelTargetFilePath: barrelTarget,
    };
  }

  const packageName = getPackageNameFromSpecifier(spec.specifier);
  return {
    fileQN,
    filePath,
    importedNamespace: spec.specifier,
    importKind: spec.kind,
    classification: 'external_npm_package',
    resolvedFilePath: resolvedPath,
    externalPackageName: packageName,
    targetPackageQN: buildNpmPackageQN(packageName),
  };
}

function addGraphEdgesForResolvedImport(
  edges: PendingEdgeDto[],
  repositoryName: string,
  fileQN: string,
  sourcePackage: WorkspacePackageInfo | undefined,
  resolved: ResolvedImportDto,
  work: ExtractionWorkBudget,
): void {
  const commonProperties = {
    imported_namespace: resolved.importedNamespace,
    import_kind: resolved.importKind,
    classification: resolved.classification,
    ...(resolved.resolvedFilePath ? { resolved_file_path: resolved.resolvedFilePath } : {}),
    ...(resolved.isBarrel ? { via_barrel: true, barrel_target_file_path: resolved.barrelTargetFilePath } : {}),
  };

  if (resolved.targetFileQN) {
    edges.push(work.createEdge(()=>({
      sourceQN: fileQN,
      targetQN: resolved.targetFileQN!,
      type: 'IMPORTS',
      properties: commonProperties,
    })));
  }

  if (resolved.targetPackageQN && resolved.classification === 'local_workspace_package') {
    edges.push(work.createEdge(()=>({
      sourceQN: fileQN,
      targetQN: resolved.targetPackageQN!,
      type: 'IMPORTS',
      properties: commonProperties,
    })));

    if (sourcePackage && sourcePackage.qualifiedName !== resolved.targetPackageQN) {
      edges.push(work.createEdge(()=>({
        sourceQN: sourcePackage.qualifiedName,
        targetQN: resolved.targetPackageQN!,
        type: 'IMPORTS',
        properties: commonProperties,
      })));
    }
  }

  if (resolved.targetPackageQN && resolved.classification === 'external_npm_package') {
    const packageName = resolved.externalPackageName ?? resolved.importedNamespace;
    edges.push(work.createEdge(()=>({
      sourceQN: fileQN,
      targetQN: resolved.targetPackageQN!,
      type: 'REFERENCES_PACKAGE',
      properties: {
        package_type: 'npm',
        package_name: packageName,
        imported_namespace: resolved.importedNamespace,
      },
    })));

    if (sourcePackage) {
      edges.push(work.createEdge(()=>({
        sourceQN: sourcePackage.qualifiedName,
        targetQN: resolved.targetPackageQN!,
        type: 'REFERENCES_PACKAGE',
        properties: {
          package_type: 'npm',
          package_name: packageName,
        },
      })));
    } else {
      edges.push(work.createEdge(()=>({
        sourceQN: repositoryName,
        targetQN: resolved.targetPackageQN!,
        type: 'REFERENCES_PACKAGE',
        properties: {
          package_type: 'npm',
          package_name: packageName,
        },
      })));
    }
  }
}

function findSingleBarrelExportTarget(
  ctx: ResolverContext,
  resolvedPath: string,
): string | undefined {
  const basename = path.basename(resolvedPath).toLowerCase();
  if (basename !== 'index.ts' && basename !== 'public-api.ts') return undefined;

  const sourceFile = ctx.sourceFilesByPath.get(resolvedPath);
  if (!sourceFile) return undefined;

  for (const exportDecl of sourceFile.getExportDeclarations().slice(0, MAX_BARREL_EXPORTS)) {
    const specifier = exportDecl.getModuleSpecifierValue();
    if (!specifier) continue;

    const config = findConfigForFile(ctx.tsconfigs, ctx.fallbackConfig, resolvedPath);
    const resolved = ts.resolveModuleName(
      specifier,
      resolvedPath,
      config.options,
      ctx.moduleHost,
    ).resolvedModule;

    if (!resolved) continue;
    const targetPath = normalizeAbsolute(resolved.resolvedFileName);
    if (targetPath === resolvedPath || !isInside(ctx.rootPath, targetPath)) continue;
    return toRelative(ctx.rootPath, targetPath);
  }

  return undefined;
}

function findConfigForFile(
  configs: TsConfigInfo[],
  fallback: TsConfigInfo,
  filePath: string,
): TsConfigInfo {
  const normalized = normalizeAbsolute(filePath);
  return configs.find(config => isInside(config.dir, normalized)) ?? fallback;
}

function findOwningPackage(
  packages: WorkspacePackageInfo[],
  filePath: string,
): WorkspacePackageInfo | undefined {
  const normalized = normalizeAbsolute(filePath);
  let best: WorkspacePackageInfo | undefined;
  for (const pkg of packages) {
    if (!isInside(pkg.rootPath, normalized)) continue;
    if (!best || pkg.rootPath.length > best.rootPath.length) best = pkg;
  }
  return best;
}

function findWorkspacePackageBySpecifier(
  packagesByName: Map<string, WorkspacePackageInfo[]>,
  specifier: string,
  importerPath: string,
): WorkspacePackageInfo | undefined {
  const exact = packagesByName.get(specifier);
  if (exact) return nearestPackage(exact, importerPath);

  for (const [name, packages] of packagesByName) {
    if (specifier.startsWith(`${name}/`)) return nearestPackage(packages, importerPath);
  }
  return undefined;
}

/**
 * The same-named workspace package an importer means: the one containing it (a self import),
 * else the one sharing the longest directory prefix with it, else the first by directory.
 */
function nearestPackage(
  packages: WorkspacePackageInfo[],
  importerPath: string,
): WorkspacePackageInfo | undefined {
  if (packages.length <= 1) return packages[0];
  const importer = normalizeAbsolute(importerPath);
  let best: WorkspacePackageInfo | undefined;
  let bestScore = -1;
  for (const pkg of packages) {
    const score = isInside(pkg.rootPath, importer)
      ? Number.MAX_SAFE_INTEGER - (importer.length - pkg.rootPath.length)
      : sharedDirectoryDepth(pkg.rootPath, path.dirname(importer));
    if (score > bestScore) {
      best = pkg;
      bestScore = score;
    }
  }
  return best;
}

function sharedDirectoryDepth(left: string, right: string): number {
  const a = left.split('/');
  const b = right.split('/');
  let depth = 0;
  while (depth < a.length && depth < b.length && a[depth] === b[depth]) depth++;
  return depth;
}

function groupPackagesByName(packages: WorkspacePackageInfo[]): Map<string, WorkspacePackageInfo[]> {
  const byName = new Map<string, WorkspacePackageInfo[]>();
  for (const pkg of packages) {
    const group = byName.get(pkg.name);
    if (group) group.push(pkg);
    else byName.set(pkg.name, [pkg]);
  }
  return byName;
}

/**
 * Gives same-named workspace packages (a vendored copy of an app beside the original) distinct
 * qualified names that include their repo-relative directory; a package whose name is unique keeps
 * the name-only qualified name, so existing graphs are unaffected.
 */
function qualifyCollidingPackages(repositoryName: string, packages: WorkspacePackageInfo[]): void {
  for (const group of groupPackagesByName(packages).values()) {
    if (group.length < 2) continue;
    for (const pkg of group) {
      pkg.qualifiedName = buildWorkspacePackageQN(repositoryName, pkg.name, pkg.relativeRoot);
    }
  }
}

function resolveWorkspacePackageExport(
  pkg: WorkspacePackageInfo,
  specifier: string,
  reader: BoundedTextReader,
): string | undefined {
  if (!pkg.packageJsonPath) return undefined;

  const packageJsonPath = path.join(pkg.rootPath, path.basename(pkg.packageJsonPath));
  const packageJson = readJsonFile(packageJsonPath, reader);
  if (!packageJson || typeof packageJson !== 'object') return undefined;

  const subpath = specifier === pkg.name
    ? '.'
    : `.${specifier.slice(pkg.name.length)}`;

  const target = resolvePackageExportTarget(packageJson.exports, subpath) ??
    (subpath === '.' ? firstString(packageJson.module, packageJson.main, packageJson.types) : undefined);
  if (!target) return undefined;

  return resolveFileWithExtensions(path.join(pkg.rootPath, target), reader);
}

function resolvePackageExportTarget(exportsValue: unknown, subpath: string): string | undefined {
  if (typeof exportsValue === 'string' && subpath === '.') return exportsValue;
  if (!exportsValue || typeof exportsValue !== 'object') return undefined;

  const exportsObject = exportsValue as Record<string, unknown>;
  const candidate = exportsObject[subpath] ?? (subpath === '.' ? exportsObject['.'] : undefined);
  return resolveConditionalExport(candidate);
}

function resolveConditionalExport(value: unknown): string | undefined {
  if (typeof value === 'string') return value;
  if (!value || typeof value !== 'object') return undefined;

  const objectValue = value as Record<string, unknown>;
  return firstString(
    objectValue.import,
    objectValue.default,
    objectValue.types,
    objectValue.require,
  );
}

function firstString(...values: unknown[]): string | undefined {
  return values.find((value): value is string => typeof value === 'string');
}

function resolveFileWithExtensions(candidate: string, reader: BoundedTextReader): string | undefined {
  const normalized = normalizeAbsolute(candidate);
  if (reader.exists(normalized)) return normalized;

  for (const suffix of ['.ts', '.tsx', '.js', '.jsx', '.d.ts']) {
    const withSuffix = `${normalized}${suffix}`;
    if (reader.exists(withSuffix)) return withSuffix;
  }

  for (const indexName of ['index.ts', 'index.tsx', 'index.js', 'index.jsx']) {
    const indexPath = path.join(normalized, indexName);
    if (reader.exists(indexPath)) return normalizeAbsolute(indexPath);
  }

  return undefined;
}

function findNearestTsConfigForRoot(
  tsconfigs: TsConfigInfo[],
  packageRoot: string,
  rootPath: string,
): string | undefined {
  const exactConfig = tsconfigs.find(candidate => candidate.dir === packageRoot);
  if (exactConfig) return toRelative(rootPath, exactConfig.path);

  const config = tsconfigs
    .filter(candidate => isInside(packageRoot, candidate.path))
    .sort((a, b) => a.dir.length - b.dir.length)[0];
  return config ? toRelative(rootPath, config.path) : undefined;
}

function readPackageJsonWorkspacePatterns(packageJsonPath: string, reader: BoundedTextReader): string[] {
  const pkg = readJsonFile(packageJsonPath, reader);
  const workspaces = pkg?.workspaces;
  if (Array.isArray(workspaces)) return workspaces.filter((entry): entry is string => typeof entry === 'string');
  if (workspaces && typeof workspaces === 'object' && Array.isArray(workspaces.packages)) {
    return workspaces.packages.filter((entry: unknown): entry is string => typeof entry === 'string');
  }
  return [];
}

function readPnpmWorkspacePatterns(workspacePath: string, reader: BoundedTextReader): string[] {
  const text = reader.read(workspacePath);
  if (text === undefined) return [];
  const lines = text.split(/\r?\n/);
  const patterns: string[] = [];
  let inPackages = false;
  for (const line of lines) {
    const trimmed = line.trim();
    if (trimmed === 'packages:') {
      inPackages = true;
      continue;
    }
    if (!inPackages) continue;
    if (trimmed.length === 0 || trimmed.startsWith('#')) continue;
    if (!trimmed.startsWith('-')) break;
    const pattern = trimmed.slice(1).trim().replace(/^['"]|['"]$/g, '');
    if (pattern && !pattern.startsWith('!')) patterns.push(pattern);
  }
  return patterns;
}

function expandWorkspacePatterns(rootPath: string, patterns: string[], findManifests: ManifestFinder, reader: BoundedTextReader, scope?: DiscoveryScope): string[] {
  const roots = new Set<string>();
  for (const pattern of patterns) {
    const normalized = pattern.replace(/\\/g, '/').replace(/\/package\.json$/, '');
    if (normalized.includes('**')) {
      const prefix = normalized.slice(0, normalized.indexOf('**')).replace(/\/$/, '');
      for (const packageJson of findManifests(path.join(rootPath, prefix), 'package.json', MAX_WORKSPACE_PACKAGES)) {
        roots.add(normalizeAbsolute(path.dirname(packageJson)));
      }
      continue;
    }

    if (normalized.includes('*')) {
      const prefix = normalized.slice(0, normalized.indexOf('*')).replace(/\/$/, '');
      const parent = path.join(rootPath, prefix);
      const directories = scope ? scope.fileSystemEntries(parent).directories : reader.entries(parent).filter(entry => entry.isDirectory()).map(entry => entry.name);
      for (const directory of directories) roots.add(normalizeAbsolute(path.join(parent, directory)));
      continue;
    }

    roots.add(normalizeAbsolute(path.join(rootPath, normalized)));
  }
  return [...roots];
}

function findFilesByName(dir: string, fileName: string, limit: number, reader: BoundedTextReader): string[] {
  const results: string[] = [];
  if (!reader.directoryExists(dir)) return results;

  const walk = (current: string) => {
    if (results.length >= limit) return;
    for (const entry of reader.entries(current)) {
      if (results.length >= limit) return;
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) {
        if (!SKIP_DIRS.has(entry.name)) walk(full);
      } else if (entry.isFile() && entry.name === fileName) {
        results.push(normalizeAbsolute(full));
      }
    }
  };

  walk(dir);
  return results;
}

function readJsonFile(filePath: string, reader: BoundedTextReader): any {
  try {
    const text = reader.read(filePath);
    return text === undefined ? null : JSON.parse(text);
  } catch {
    return null;
  }
}

function looksLikeAlias(config: TsConfigInfo, specifier: string): boolean {
  if (specifier.startsWith('.') || specifier.startsWith('/')) return false;
  const pathKeys = Object.keys(config.paths ?? {});
  return pathKeys.some(key => {
    const prefix = key.replace(/\*.*$/, '');
    return prefix.length > 0 && specifier.startsWith(prefix);
  });
}

function getPackageNameFromSpecifier(specifier: string): string {
  if (specifier.startsWith('@')) {
    const [scope, name] = specifier.split('/');
    return name ? `${scope}/${name}` : specifier;
  }
  return specifier.split('/')[0] || specifier;
}

function buildWorkspacePackageQN(repositoryName: string, packageName: string, relativeRoot?: string): string {
  const qualifiedName = `typescript-package:${repositoryName}:${packageName}`;
  return relativeRoot === undefined ? qualifiedName : `${qualifiedName}#${relativeRoot || '.'}`;
}

function buildNpmPackageQN(packageName: string): string {
  return `npm:${packageName}`;
}

function toWorkspacePackageDto(pkg: WorkspacePackageInfo): WorkspacePackageDto {
  return {
    name: pkg.name,
    qualifiedName: pkg.qualifiedName,
    rootPath: pkg.relativeRoot,
    kind: pkg.kind,
    packageJsonPath: pkg.packageJsonPath,
    tsconfigPath: pkg.tsconfigPath,
  };
}

function dedupeEdges(edges: PendingEdgeDto[]): PendingEdgeDto[] {
  const seen = new Set<string>();
  const unique: PendingEdgeDto[] = [];
  for (const edge of edges) {
    const key = `${edge.sourceQN}\u0000${edge.targetQN}\u0000${edge.type}\u0000${JSON.stringify(edge.properties ?? {})}`;
    if (seen.has(key)) continue;
    seen.add(key);
    unique.push(edge);
  }
  return unique;
}

function shouldSkipRelativePath(filePath: string): boolean {
  return filePath.includes('node_modules/') ||
    filePath.endsWith('.d.ts') ||
    filePath.endsWith('.spec.ts') ||
    filePath.includes('.generated.');
}

function isNodeModulePath(filePath: string): boolean {
  return filePath.split(/[\\/]/).includes('node_modules');
}

function isInside(parent: string, candidate: string): boolean {
  const rel = path.relative(normalizeAbsolute(parent), normalizeAbsolute(candidate));
  return rel === '' || (!!rel && !rel.startsWith('..') && !path.isAbsolute(rel));
}

function normalizeAbsolute(filePath: string): string {
  return path.resolve(filePath).replace(/\\/g, '/');
}

function toRelative(rootPath: string, filePath: string): string {
  return path.relative(rootPath, filePath).replace(/\\/g, '/');
}

function flattenDiagnostic(diagnostic: ts.Diagnostic): string {
  return ts.flattenDiagnosticMessageText(diagnostic.messageText, ' ');
}

function moduleResolutionName(value: ts.ModuleResolutionKind | undefined): string | undefined {
  if (value === undefined) return undefined;
  return ts.ModuleResolutionKind[value];
}

function mergePackageKinds(left: string, right: string): string {
  if (left === right) return left;
  if (left === 'root_package') return right;
  if (right === 'root_package') return left;
  return `${left},${right}`;
}
