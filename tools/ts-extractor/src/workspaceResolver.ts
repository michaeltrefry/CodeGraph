import { Node, SourceFile, SyntaxKind } from 'ts-morph';
import * as fs from 'fs';
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
  projectName: string;
  rootPath: string;
  tsconfigs: TsConfigInfo[];
  fallbackConfig: TsConfigInfo;
  workspacePackages: WorkspacePackageInfo[];
  packageByName: Map<string, WorkspacePackageInfo>;
  sourceFilesByPath: Map<string, SourceFile>;
  moduleHost: ts.ModuleResolutionHost;
  diagnostics: string[];
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
  projectName: string,
  rootPath: string,
  requestedTsconfigPath: string,
  sourceFiles: SourceFile[],
  log: (msg: string) => void = () => {},
): WorkspaceResolutionResult {
  const diagnostics: string[] = [];
  const diag = (msg: string) => {
    diagnostics.push(msg);
    log(msg);
  };

  const normalizedRoot = normalizeAbsolute(rootPath);
  const tsconfigs = discoverTsConfigs(normalizedRoot, requestedTsconfigPath, diag);
  const fallbackConfig = pickFallbackConfig(tsconfigs, requestedTsconfigPath);
  const workspacePackages = discoverWorkspacePackages(
    projectName,
    normalizedRoot,
    tsconfigs,
    diag,
  );

  const sourceFilesByPath = new Map<string, SourceFile>();
  for (const sourceFile of sourceFiles) {
    sourceFilesByPath.set(normalizeAbsolute(sourceFile.getFilePath()), sourceFile);
  }

  const ctx: ResolverContext = {
    projectName,
    rootPath: normalizedRoot,
    tsconfigs,
    fallbackConfig,
    workspacePackages,
    packageByName: new Map(workspacePackages.map(pkg => [pkg.name, pkg])),
    sourceFilesByPath,
    moduleHost: ts.sys,
    diagnostics,
  };

  const nodes = buildWorkspacePackageNodes(workspacePackages);
  const edges = buildWorkspacePackageContainmentEdges(projectName, workspacePackages);
  const resolvedImports: ResolvedImportDto[] = [];
  const unresolvedImports: UnresolvedImportDto[] = [];

  let resolutionCount = 0;
  for (const sourceFile of sourceFiles) {
    const absPath = normalizeAbsolute(sourceFile.getFilePath());
    const relPath = toRelative(rootPath, absPath);
    if (shouldSkipRelativePath(relPath)) continue;

    const fileQN = `${projectName}:${relPath}`;
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
      addGraphEdgesForResolvedImport(edges, projectName, fileQN, sourcePackage, resolved);
      if (resolved.classification === 'unresolved_alias' ||
          resolved.classification === 'unsupported_dynamic_expression') {
        unresolvedImports.push({
          fileQN,
          importedNamespace: resolved.importedNamespace,
        });
      }
    }
  }

  nodes.push(...buildExternalPackageNodes(resolvedImports));

  diag(`workspaceDiscovery: ${workspacePackages.length} packages, ${tsconfigs.length} tsconfig files`);
  diag(`moduleResolution: ${resolvedImports.length} import/export/dynamic specifiers`);

  return {
    nodes,
    edges: dedupeEdges(edges),
    workspacePackages: workspacePackages.map(toWorkspacePackageDto),
    resolvedImports,
    unresolvedImports,
    diagnostics,
  };
}

function discoverTsConfigs(
  rootPath: string,
  requestedTsconfigPath: string,
  diag: (msg: string) => void,
): TsConfigInfo[] {
  const discovered = findFilesByName(rootPath, 'tsconfig.json', MAX_TSCONFIGS + 1);
  const normalizedRequested = normalizeAbsolute(requestedTsconfigPath);
  if (!discovered.includes(normalizedRequested) && fs.existsSync(normalizedRequested)) {
    discovered.unshift(normalizedRequested);
  }

  if (discovered.length > MAX_TSCONFIGS) {
    diag(`tsconfigDiscovery: limited from ${discovered.length} to ${MAX_TSCONFIGS} files`);
  }

  const configs: TsConfigInfo[] = [];
  for (const configPath of discovered.slice(0, MAX_TSCONFIGS)) {
    const parsed = parseTsConfig(configPath, rootPath, diag);
    if (parsed) configs.push(parsed);
  }

  if (configs.length === 0) {
    configs.push({
      path: normalizedRequested,
      dir: path.dirname(normalizedRequested),
      relativePath: toRelative(rootPath, normalizedRequested),
      options: {
        allowJs: false,
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
): TsConfigInfo | null {
  const host: ts.ParseConfigFileHost = {
    useCaseSensitiveFileNames: ts.sys.useCaseSensitiveFileNames,
    getCurrentDirectory: ts.sys.getCurrentDirectory,
    fileExists: ts.sys.fileExists,
    readFile: ts.sys.readFile,
    readDirectory: () => [],
    onUnRecoverableConfigFileDiagnostic: diagnostic =>
      diag(`tsconfig ${toRelative(rootPath, configPath)}: ${flattenDiagnostic(diagnostic)}`),
  };

  const parsed = ts.getParsedCommandLineOfConfigFile(
    configPath,
    {
      noResolve: true,
      skipLibCheck: true,
      allowJs: false,
      types: [],
    },
    host,
  );

  if (!parsed) return null;
  for (const error of parsed.errors) {
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

function pickFallbackConfig(configs: TsConfigInfo[], requestedTsconfigPath: string): TsConfigInfo {
  const requested = normalizeAbsolute(requestedTsconfigPath);
  return configs.find(config => config.path === requested) ?? configs[0];
}

function discoverWorkspacePackages(
  projectName: string,
  rootPath: string,
  tsconfigs: TsConfigInfo[],
  diag: (msg: string) => void,
): WorkspacePackageInfo[] {
  const packagesByRoot = new Map<string, WorkspacePackageInfo>();
  const packageJsonPaths = findFilesByName(rootPath, 'package.json', MAX_WORKSPACE_PACKAGES + 1);
  if (packageJsonPaths.length > MAX_WORKSPACE_PACKAGES) {
    diag(`workspaceDiscovery: limited package.json scan from ${packageJsonPaths.length} to ${MAX_WORKSPACE_PACKAGES}`);
  }

  const rootPackageJson = path.join(rootPath, 'package.json');
  const workspacePatterns = [
    ...readPackageJsonWorkspacePatterns(rootPackageJson),
    ...readPnpmWorkspacePatterns(path.join(rootPath, 'pnpm-workspace.yaml')),
  ];
  const workspaceRoots = expandWorkspacePatterns(rootPath, workspacePatterns);

  for (const packageJsonPath of packageJsonPaths.slice(0, MAX_WORKSPACE_PACKAGES)) {
    const pkg = readJsonFile(packageJsonPath);
    const packageName = typeof pkg?.name === 'string' ? pkg.name : null;
    const packageRoot = normalizeAbsolute(path.dirname(packageJsonPath));
    const isRoot = packageRoot === rootPath;
    const matchesWorkspace = workspaceRoots.some(root => root === packageRoot);
    if (!packageName && !matchesWorkspace && !isRoot) continue;

    const inferredName = packageName ?? path.basename(packageRoot);
    addWorkspacePackage(packagesByRoot, {
      name: inferredName,
      qualifiedName: buildWorkspacePackageQN(projectName, inferredName),
      rootPath: packageRoot,
      relativeRoot: toRelative(rootPath, packageRoot),
      kind: isRoot ? 'root_package' : 'workspace_package',
      packageJsonPath: toRelative(rootPath, packageJsonPath),
      tsconfigPath: findNearestTsConfigForRoot(tsconfigs, packageRoot, rootPath),
    });
  }

  addAngularWorkspacePackages(projectName, rootPath, packagesByRoot);

  for (const config of tsconfigs) {
    for (const reference of config.references) {
      const referencedPath = normalizeAbsolute(path.join(rootPath, reference));
      const referencedRoot = path.basename(referencedPath).startsWith('tsconfig.')
        ? normalizeAbsolute(path.dirname(referencedPath))
        : referencedPath;
      if (!isInside(rootPath, referencedRoot)) continue;
      addWorkspacePackage(packagesByRoot, {
        name: path.basename(referencedRoot),
        qualifiedName: buildWorkspacePackageQN(projectName, path.basename(referencedRoot)),
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
  projectName: string,
  rootPath: string,
  packagesByRoot: Map<string, WorkspacePackageInfo>,
): void {
  for (const workspaceFile of ['angular.json', 'workspace.json']) {
    const workspacePath = path.join(rootPath, workspaceFile);
    const workspace = readJsonFile(workspacePath);
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
        qualifiedName: buildWorkspacePackageQN(projectName, name),
        rootPath: root,
        relativeRoot: toRelative(rootPath, root),
        kind: projectType === 'library' ? 'angular_library' : 'angular_application',
        tsconfigPath: undefined,
      });
    }
  }
}

function buildWorkspacePackageNodes(packages: WorkspacePackageInfo[]): GraphNodeDto[] {
  return packages.map(pkg => ({
    label: 'Module',
    name: pkg.name,
    qualifiedName: pkg.qualifiedName,
    filePath: pkg.relativeRoot,
    startLine: 0,
    endLine: 0,
    properties: {
      module_kind: 'typescript_workspace_package',
      workspace_kind: pkg.kind,
      root_path: pkg.relativeRoot,
      ...(pkg.packageJsonPath ? { package_json_path: pkg.packageJsonPath } : {}),
      ...(pkg.tsconfigPath ? { tsconfig_path: pkg.tsconfigPath } : {}),
    },
  }));
}

function buildWorkspacePackageContainmentEdges(
  projectName: string,
  packages: WorkspacePackageInfo[],
): PendingEdgeDto[] {
  return packages.map(pkg => ({
    sourceQN: projectName,
    targetQN: pkg.qualifiedName,
    type: 'CONTAINS_PROJECT',
    properties: {
      workspace_kind: pkg.kind,
      root_path: pkg.relativeRoot,
    },
  }));
}

function buildExternalPackageNodes(resolvedImports: ResolvedImportDto[]): GraphNodeDto[] {
  const packages = new Map<string, string>();
  for (const resolved of resolvedImports) {
    if (resolved.classification !== 'external_npm_package' ||
        !resolved.targetPackageQN ||
        !resolved.externalPackageName) {
      continue;
    }

    packages.set(resolved.targetPackageQN, resolved.externalPackageName);
  }

  return [...packages.entries()].map(([qualifiedName, name]) => ({
    label: 'NuGetPackage',
    name,
    qualifiedName,
    filePath: '',
    startLine: 0,
    endLine: 0,
    properties: {
      package_type: 'npm',
    },
  }));
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
  const resolved = ts.resolveModuleName(
    spec.specifier,
    importerPath,
    config.options,
    ctx.moduleHost,
  ).resolvedModule;

  const localPackage = findWorkspacePackageBySpecifier(ctx.packageByName, spec.specifier);
  if (!resolved && localPackage) {
    const exportedPath = resolveWorkspacePackageExport(localPackage, spec.specifier);
    const exportedFilePath = exportedPath && isInside(ctx.rootPath, exportedPath)
      ? toRelative(ctx.rootPath, exportedPath)
      : undefined;
    return {
      fileQN,
      filePath,
      importedNamespace: spec.specifier,
      importKind: spec.kind,
      classification: 'local_workspace_package',
      resolvedFilePath: exportedFilePath,
      targetFileQN: exportedFilePath ? `${ctx.projectName}:${exportedFilePath}` : undefined,
      targetWorkspacePackage: localPackage.name,
      targetPackageQN: localPackage.qualifiedName,
      diagnostic: exportedFilePath
        ? undefined
        : 'resolved to workspace package name without a concrete file',
    };
  }

  if (!resolved) {
    const diagnostic = `unresolved import '${spec.specifier}' from ${filePath}`;
    const classification = looksLikeAlias(config, spec.specifier) ? 'unresolved_alias' : 'external_npm_package';
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
      targetFileQN: `${ctx.projectName}:${targetFilePath}`,
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
  projectName: string,
  fileQN: string,
  sourcePackage: WorkspacePackageInfo | undefined,
  resolved: ResolvedImportDto,
): void {
  const commonProperties = {
    imported_namespace: resolved.importedNamespace,
    import_kind: resolved.importKind,
    classification: resolved.classification,
    ...(resolved.resolvedFilePath ? { resolved_file_path: resolved.resolvedFilePath } : {}),
    ...(resolved.isBarrel ? { via_barrel: true, barrel_target_file_path: resolved.barrelTargetFilePath } : {}),
  };

  if (resolved.targetFileQN) {
    edges.push({
      sourceQN: fileQN,
      targetQN: resolved.targetFileQN,
      type: 'IMPORTS',
      properties: commonProperties,
    });
  }

  if (resolved.targetPackageQN && resolved.classification === 'local_workspace_package') {
    edges.push({
      sourceQN: fileQN,
      targetQN: resolved.targetPackageQN,
      type: 'IMPORTS',
      properties: commonProperties,
    });

    if (sourcePackage && sourcePackage.qualifiedName !== resolved.targetPackageQN) {
      edges.push({
        sourceQN: sourcePackage.qualifiedName,
        targetQN: resolved.targetPackageQN,
        type: 'IMPORTS',
        properties: commonProperties,
      });
    }
  }

  if (resolved.targetPackageQN && resolved.classification === 'external_npm_package') {
    const packageName = resolved.externalPackageName ?? resolved.importedNamespace;
    edges.push({
      sourceQN: fileQN,
      targetQN: resolved.targetPackageQN,
      type: 'REFERENCES_PACKAGE',
      properties: {
        package_type: 'npm',
        package_name: packageName,
        imported_namespace: resolved.importedNamespace,
      },
    });

    if (sourcePackage) {
      edges.push({
        sourceQN: sourcePackage.qualifiedName,
        targetQN: resolved.targetPackageQN,
        type: 'REFERENCES_PACKAGE',
        properties: {
          package_type: 'npm',
          package_name: packageName,
        },
      });
    } else {
      edges.push({
        sourceQN: projectName,
        targetQN: resolved.targetPackageQN,
        type: 'REFERENCES_PACKAGE',
        properties: {
          package_type: 'npm',
          package_name: packageName,
        },
      });
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
  packageByName: Map<string, WorkspacePackageInfo>,
  specifier: string,
): WorkspacePackageInfo | undefined {
  const exact = packageByName.get(specifier);
  if (exact) return exact;

  for (const [name, pkg] of packageByName) {
    if (specifier.startsWith(`${name}/`)) return pkg;
  }
  return undefined;
}

function resolveWorkspacePackageExport(
  pkg: WorkspacePackageInfo,
  specifier: string,
): string | undefined {
  if (!pkg.packageJsonPath) return undefined;

  const packageJsonPath = path.join(pkg.rootPath, path.basename(pkg.packageJsonPath));
  const packageJson = readJsonFile(packageJsonPath);
  if (!packageJson || typeof packageJson !== 'object') return undefined;

  const subpath = specifier === pkg.name
    ? '.'
    : `.${specifier.slice(pkg.name.length)}`;

  const target = resolvePackageExportTarget(packageJson.exports, subpath) ??
    (subpath === '.' ? firstString(packageJson.module, packageJson.main, packageJson.types) : undefined);
  if (!target) return undefined;

  return resolveFileWithExtensions(path.join(pkg.rootPath, target));
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

function resolveFileWithExtensions(candidate: string): string | undefined {
  const normalized = normalizeAbsolute(candidate);
  if (fs.existsSync(normalized) && fs.statSync(normalized).isFile()) return normalized;

  for (const suffix of ['.ts', '.tsx', '.js', '.jsx', '.d.ts']) {
    const withSuffix = `${normalized}${suffix}`;
    if (fs.existsSync(withSuffix) && fs.statSync(withSuffix).isFile()) return withSuffix;
  }

  for (const indexName of ['index.ts', 'index.tsx', 'index.js', 'index.jsx']) {
    const indexPath = path.join(normalized, indexName);
    if (fs.existsSync(indexPath) && fs.statSync(indexPath).isFile()) return normalizeAbsolute(indexPath);
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

function readPackageJsonWorkspacePatterns(packageJsonPath: string): string[] {
  const pkg = readJsonFile(packageJsonPath);
  const workspaces = pkg?.workspaces;
  if (Array.isArray(workspaces)) return workspaces.filter((entry): entry is string => typeof entry === 'string');
  if (workspaces && typeof workspaces === 'object' && Array.isArray(workspaces.packages)) {
    return workspaces.packages.filter((entry: unknown): entry is string => typeof entry === 'string');
  }
  return [];
}

function readPnpmWorkspacePatterns(workspacePath: string): string[] {
  if (!fs.existsSync(workspacePath)) return [];
  const lines = fs.readFileSync(workspacePath, 'utf-8').split(/\r?\n/);
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

function expandWorkspacePatterns(rootPath: string, patterns: string[]): string[] {
  const roots = new Set<string>();
  for (const pattern of patterns) {
    const normalized = pattern.replace(/\\/g, '/').replace(/\/package\.json$/, '');
    if (normalized.includes('**')) {
      const prefix = normalized.slice(0, normalized.indexOf('**')).replace(/\/$/, '');
      for (const packageJson of findFilesByName(path.join(rootPath, prefix), 'package.json', MAX_WORKSPACE_PACKAGES)) {
        roots.add(normalizeAbsolute(path.dirname(packageJson)));
      }
      continue;
    }

    if (normalized.includes('*')) {
      const prefix = normalized.slice(0, normalized.indexOf('*')).replace(/\/$/, '');
      const parent = path.join(rootPath, prefix);
      if (!fs.existsSync(parent)) continue;
      for (const entry of fs.readdirSync(parent, { withFileTypes: true })) {
        if (entry.isDirectory()) roots.add(normalizeAbsolute(path.join(parent, entry.name)));
      }
      continue;
    }

    roots.add(normalizeAbsolute(path.join(rootPath, normalized)));
  }
  return [...roots];
}

function findFilesByName(dir: string, fileName: string, limit: number): string[] {
  const results: string[] = [];
  if (!fs.existsSync(dir)) return results;

  const walk = (current: string) => {
    if (results.length >= limit) return;
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
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

function readJsonFile(filePath: string): any {
  try {
    if (!fs.existsSync(filePath)) return null;
    return JSON.parse(fs.readFileSync(filePath, 'utf-8'));
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

function buildWorkspacePackageQN(projectName: string, packageName: string): string {
  return `typescript-package:${projectName}:${packageName}`;
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
