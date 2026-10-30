import {
  Project,
  SourceFile,
  ClassDeclaration,
  Node,
  SyntaxKind,
  ObjectLiteralExpression,
  Type,
} from 'ts-morph';
import * as path from 'path';
import * as fs from 'fs';
import { analyzeWorkspaceImports } from './workspaceResolver';
import { DiscoveryScope } from './discoveryScope';
import { canonicalExtractionEdges } from './extractionEdges';
import { SemanticModel } from './semanticModel';
import { analyzeReact } from './reactAnalysis';
import { analyzeHttp } from './httpAnalysis';
import { analyzeRoutes } from './routeAnalysis';
import { analyzeTauri } from './tauriAnalysis';
import { analyzeAngular } from './angularAnalysis';
import type {
  GraphNodeDto,
  PendingEdgeDto,
  UnresolvedImportDto,
  UnresolvedCallDto,
  ExtractProjectResponse,
  ResolvedImportDto,
  WorkspacePackageDto,
} from './protocol';
// Angular / framework types that should not produce INJECTS edges
const FRAMEWORK_TYPE_PREFIXES = [
  'ActivatedRoute', 'Router', 'FormBuilder', 'ChangeDetectorRef',
  'ElementRef', 'Renderer2', 'ViewContainerRef', 'HttpClient',
  'NgZone', 'ComponentFactoryResolver', 'ApplicationRef',
  'Injector', 'TranslateService', 'MatDialog', 'MatSnackBar',
];

const PRIMITIVE_TYPES = new Set([
  'string', 'number', 'boolean', 'any', 'void',
  'null', 'undefined', 'never', 'unknown', 'object',
]);

export function extractProject(
  repositoryName: string,
  rootPath: string,
  tsconfigPath: string,
  log: (msg: string) => void = () => {},
  includedFiles?: readonly string[],
): ExtractProjectResponse {
  const nodes: GraphNodeDto[] = [];
  const edges: PendingEdgeDto[] = [];
  const workspacePackages: WorkspacePackageDto[] = [];
  const resolvedImports: ResolvedImportDto[] = [];
  const unresolvedImports: UnresolvedImportDto[] = [];
  const unresolvedCalls: UnresolvedCallDto[] = [];

  const diagnostics: string[] = [];

  // Create project with minimal compiler options and discover files manually.
  // ts-morph with moduleResolution:"bundler" and no include/files can hang, so we
  // scan the filesystem directly instead of relying on tsconfig file resolution.
  // noResolve: true prevents the compiler from pulling in referenced files
  // (e.g. node_modules/@tc/*) which can stall for minutes on large repos.
  const project = new Project({
    compilerOptions: {
      allowJs: true,
      jsx: 1 /* Preserve */,
      types: [],
      skipLibCheck: true,
      noResolve: true,
      experimentalDecorators: true,
      target: 2 /* ES2015 */,
    },
    skipAddingFilesFromTsConfig: true,
  });
  const diagnosticCounts = new Map<string, number>();
  const diag = (msg: string) => {
    const category = msg.split(':', 1)[0];
    const count = diagnosticCounts.get(category) ?? 0;
    diagnosticCounts.set(category, count + 1);
    if (count < 25) { log(msg); diagnostics.push(msg); }
    else if (count === 25) { const notice = `${category}: further samples omitted`; log(notice); diagnostics.push(notice); }
  };

  // CodeGraph passes the files its discovery admits; the sidecar then never reads outside them.
  const scope = includedFiles ? new DiscoveryScope(rootPath, includedFiles) : undefined;
  const typeRoots = checkoutRoots(rootPath);

  let t0 = Date.now();
  // Explicit discovery has already applied repository safety/exclusion policy. Standalone
  // walk defaults must not silently remove an admitted source before processed-file accounting.
  const tsFiles = scope
    ? scope.absoluteFiles(file => /\.[jt]sx?$/i.test(file))
    : findTsFiles(rootPath);
  diag(`findTsFiles: ${tsFiles.length} files in ${Date.now() - t0}ms`);

  t0 = Date.now();
  let complete = true;
  for (const f of tsFiles) {
    try { project.addSourceFileAtPath(f); }
    catch (error) { complete = false; diag(`sourceRead: ${path.relative(rootPath, f)}: ${String(error)}`); }
  }
  if (scope) for (const file of scope.absoluteFiles(relative => /(?:^|\/)(?:package|[tj]sconfig[^/]*)\.json$/.test(relative))) {
    try {
      const text = fs.readFileSync(file, 'utf8');
      if (path.basename(file) === 'package.json') JSON.parse(text);
      // TypeScript config errors (including extends) are surfaced by workspace resolution below.
    } catch (error) { complete = false; diag(`configRead: ${path.relative(rootPath, file)}: ${String(error)}`); }
  }
  const invalidFiles = new Set<string>();
  for (const source of project.getSourceFiles()) {
    const errors = project.getProgram().compilerObject.getSyntacticDiagnostics(source.compilerNode);
    if (errors.length) {
      complete = false;
      invalidFiles.add(source.getFilePath());
      for (const error of errors.slice(0, 5)) diag(`sourceParse: ${path.relative(rootPath, source.getFilePath())}: ${typeof error.messageText === 'string' ? error.messageText : error.messageText.messageText}`);
    }
  }
  diag(`addSourceFiles: ${Date.now() - t0}ms`);

  const serviceName = !scope || scope.includesFile(path.join(rootPath, 'package.json')) ? readBackendServiceName(rootPath) : null;
  diag(`serviceName: ${serviceName ?? '(none)'}`);

  t0 = Date.now();
  const parsedFiles = project.getSourceFiles().filter(source => !invalidFiles.has(source.getFilePath()));
  const sourceFiles = parsedFiles.filter(source => isTsSourceFile(source.getFilePath()) && !/\.generated\./i.test(source.getFilePath()));
  diag(`getSourceFiles: ${sourceFiles.length} files in ${Date.now() - t0}ms`);

  t0 = Date.now();
  const workspaceResolution = analyzeWorkspaceImports(
    repositoryName,
    rootPath,
    tsconfigPath,
    sourceFiles,
    log,
    scope,
  );
  nodes.push(...workspaceResolution.nodes);
  edges.push(...workspaceResolution.edges);
  workspacePackages.push(...workspaceResolution.workspacePackages);
  resolvedImports.push(...workspaceResolution.resolvedImports);
  unresolvedImports.push(...workspaceResolution.unresolvedImports);
  diagnostics.push(...workspaceResolution.diagnostics);
  diag(`workspaceResolution: ${workspaceResolution.resolvedImports.length} imports in ${Date.now() - t0}ms`);


  let filesProcessed = 0;
  // Admitted declaration/spec/generated files are intentionally empty semantic slices, but
  // only successful read+parse permits their hash to advance or fallback to be suppressed.
  const processedFiles = parsedFiles.map(source => path.relative(rootPath, source.getFilePath()).replace(/\\/g, '/'));
  for (const sourceFile of sourceFiles) {
    const absPath = sourceFile.getFilePath();
    const filePath = path.relative(rootPath, absPath).replace(/\\/g, '/');

    // Skip generated, test, and definition files
    if (
      filePath.includes('node_modules') ||
      filePath.endsWith('.d.ts') ||
      filePath.endsWith('.spec.ts') ||
      filePath.includes('.generated.')
    ) continue;

    const fileT0 = Date.now();
    const fileQN = `${repositoryName}:${filePath}`;

    extractImports(sourceFile, fileQN, unresolvedImports);
    extractClasses(sourceFile, repositoryName, filePath, nodes, edges, typeRoots);
    extractInterfaces(sourceFile, filePath, nodes);
    extractEnums(sourceFile, filePath, nodes);

    const fileMs = Date.now() - fileT0;
    filesProcessed++;
    if (fileMs > 500) diag(`SLOW file (${fileMs}ms): ${filePath}`);
  }
  diag(`extractionLoop: ${filesProcessed} files in ${Date.now() - t0}ms`);

  const semantic = new SemanticModel(repositoryName, rootPath, sourceFiles, workspaceResolution.resolvedImports, diag);
  semantic.extract(nodes, edges, unresolvedCalls);
  analyzeReact(semantic, nodes, edges, diag);
  // Reconcile legacy Angular targets using declaration identities, never rendered type strings.
  semantic.reconcileAngular(nodes, edges);
  const angularCoverage = analyzeAngular(semantic, nodes, edges, scope, diag);
  complete &&= angularCoverage.complete;
  const routeCoverage = analyzeRoutes(semantic, nodes, edges, scope, diag);
  complete &&= routeCoverage.complete;
  const httpCoverage = analyzeHttp(semantic, nodes, edges, diag, serviceName);
  complete &&= httpCoverage.complete;
  analyzeTauri(semantic, nodes, edges, diag);
  semantic.reconcilePackageExports(nodes);
  const canonicalEdges = canonicalExtractionEdges(edges);
  edges.splice(0, edges.length, ...canonicalEdges);
  for (const node of nodes) node.properties.extraction_owner = 'typescript';
  for (const edge of edges) edge.properties = { ...edge.properties, extraction_owner: 'typescript' };

  const screenSelections = edges.filter(edge => edge.type === 'RENDERS').flatMap(edge =>
    Array.isArray(edge.properties?.screen_selections) ? edge.properties!.screen_selections as Record<string, unknown>[] :
      edge.properties?.screen_key !== undefined ? [edge.properties] : []);
  diag(`Link coverage: ${JSON.stringify({
    admitted_by_extension: Object.fromEntries(['.js', '.jsx', '.ts', '.tsx'].map(extension => [extension, tsFiles.filter(file => file.toLowerCase().endsWith(extension)).length])),
    processed_by_extension: Object.fromEntries(['.js', '.jsx', '.ts', '.tsx'].map(extension => [extension, processedFiles.filter(file => file.toLowerCase().endsWith(extension)).length])),
    components: nodes.filter(node => node.label === 'Component').length,
    pages: nodes.filter(node => node.properties.route_kind === 'ui').length,
    screens: new Set(screenSelections.map(selection =>
      JSON.stringify([selection.screen_selector_owner, selection.screen_selector, selection.screen_key]))).size,
    renders: edges.filter(edge => edge.type === 'RENDERS').length,
    calls: edges.filter(edge => edge.type === 'CALLS').length,
    contexts: nodes.filter(node => node.properties.context_kind === 'context').length,
    http: httpCoverage,
    tauri_references: nodes.filter(node => node.properties.transport === 'tauri').length,
    providers: edges.filter(edge => edge.type === 'REGISTERS').length,
    interface_dispatch: edges.filter(edge => edge.properties?.dispatch === 'interface').length,
  })}`);

  if (workspaceResolution.diagnostics.some(message =>
    (message.startsWith('tsconfig ') && !message.includes('No inputs were found')) ||
    message.includes('stopped after') || message.includes('limited from'))) complete = false;

  if (diagnosticCounts.has('contextTraversal') || diagnosticCounts.has('symbolResolutionLimit') || diagnosticCounts.has('tauriTraversal') || diagnosticCounts.has('reactLazyTraversal')) complete = false;

  // Edge type summary
  const edgeTypes: Record<string, number> = {};
  for (const e of edges) edgeTypes[e.type] = (edgeTypes[e.type] || 0) + 1;
  diagnostics.push(`Edge types: ${JSON.stringify(edgeTypes)}`);

  return { nodes, edges, workspacePackages, resolvedImports, unresolvedImports, unresolvedCalls, processedFiles, diagnostics, complete };
}

// ── Imports ───────────────────────────────────────────────────────────────────

function extractImports(
  sourceFile: SourceFile,
  fileQN: string,
  unresolvedImports: UnresolvedImportDto[],
): void {
  for (const importDecl of sourceFile.getImportDeclarations()) {
    unresolvedImports.push({
      fileQN,
      importedNamespace: importDecl.getModuleSpecifierValue(),
    });
  }
}

// ── Classes ───────────────────────────────────────────────────────────────────

function extractClasses(
  sourceFile: SourceFile,
  repositoryName: string,
  filePath: string,
  nodes: GraphNodeDto[],
  edges: PendingEdgeDto[],
  typeRoots: string[],
): void {
  for (const cls of sourceFile.getClasses()) {
    extractClass(cls, repositoryName, filePath, nodes, edges, typeRoots);
  }
}

function extractClass(
  cls: ClassDeclaration,
  repositoryName: string,
  filePath: string,
  nodes: GraphNodeDto[],
  edges: PendingEdgeDto[],
  typeRoots: string[],
): void {
  const name = cls.getName() ?? (cls.isDefaultExport() ? 'default' : undefined);
  if (!name) return;

  const qn = buildQN(filePath, name);
  const decoratorNames = cls.getDecorators().map(d => d.getName());
  const isComponent = decoratorNames.includes('Component');
  const isInjectable = decoratorNames.includes('Injectable');
  const isNgModule = decoratorNames.includes('NgModule');
  const isPipe = decoratorNames.includes('Pipe');

  const properties: Record<string, unknown> = {
    is_abstract: cls.isAbstract(),
    is_service: isInjectable,
    is_ng_module: isNgModule,
    is_pipe: isPipe,
  };

  let label = 'Class';

  if (isComponent) {
    label = 'Component';
    properties.framework = 'angular';
    const dec = cls.getDecorator('Component');
    if (dec) {
      const args = dec.getArguments();
      if (args.length > 0 && Node.isObjectLiteralExpression(args[0])) {
        const obj = args[0] as ObjectLiteralExpression;
        const selector = getStringProperty(obj, 'selector');
        const templateUrl = getStringProperty(obj, 'templateUrl');
        if (selector) properties['selector'] = selector;
        if (templateUrl) properties['templateUrl'] = templateUrl;
      }
    }
  }

  nodes.push({
    label,
    name,
    qualifiedName: qn,
    filePath,
    startLine: cls.getStartLineNumber(),
    endLine: cls.getEndLineNumber(),
    properties,
  });

  // INHERITS
  const base = cls.getExtends();
  if (base) edges.push({ sourceQN: qn, targetQN: base.getExpression().getText(), type: 'INHERITS' });

  // IMPLEMENTS
  for (const iface of cls.getImplements()) {
    const ifaceName = iface.getExpression().getText();
    edges.push({ sourceQN: qn, targetQN: ifaceName, type: 'IMPLEMENTS' });
  }

  // Constructor INJECTS
  for (const ctor of cls.getConstructors()) {
    for (const param of ctor.getParameters()) {
      const typeName = renderTypeText(param.getType(), typeRoots);
      if (isFrameworkType(typeName) || PRIMITIVE_TYPES.has(typeName)) continue;
      edges.push({
        sourceQN: qn,
        targetQN: typeName,
        type: 'INJECTS',
        properties: { parameter_name: param.getName() },
      });
    }
  }

  // inject() function INJECTS (modern Angular pattern)
  for (const prop of cls.getProperties()) {
    const init = prop.getInitializer();
    if (!init || !Node.isCallExpression(init)) continue;
    if (init.getExpression().getText() !== 'inject') continue;
    const args = init.getArguments();
    if (args.length === 0) continue;
    const typeName = args[0].getText();
    if (isFrameworkType(typeName) || PRIMITIVE_TYPES.has(typeName)) continue;
    edges.push({
      sourceQN: qn,
      targetQN: typeName,
      type: 'INJECTS',
      properties: { parameter_name: prop.getName() },
    });
  }

  // Methods
  for (const method of cls.getMethods()) {
    const methodName = method.getName();
    const methodQN = `${qn}.${methodName}`;

    nodes.push({
      label: 'Method',
      name: methodName,
      qualifiedName: methodQN,
      filePath,
      startLine: method.getStartLineNumber(),
      endLine: method.getEndLineNumber(),
      properties: {
        is_async: method.isAsync(),
        return_type: renderTypeText(method.getReturnType(), typeRoots),
        parameter_count: method.getParameters().length,
        is_static: method.isStatic(),
      },
    });

    edges.push({ sourceQN: qn, targetQN: methodQN, type: 'DEFINES_METHOD' });

    // HTTP calls within the method body
  }

  // Arrow function properties (e.g. searchDomains$ = () => this.httpService.send(...))
  for (const prop of cls.getProperties()) {
    const init = prop.getInitializer();
    if (!init) continue;
    // Skip inject() calls — already handled above
    if (Node.isCallExpression(init) && init.getExpression().getText() === 'inject') continue;

    // Check if this property initializer contains any call expressions worth scanning
    const hasCalls = init.getDescendantsOfKind(SyntaxKind.CallExpression).length > 0;
    if (!hasCalls) continue;

    const propName = prop.getName();
    const propQN = `${qn}.${propName}`;

    // Create a Method node so edge sources can resolve
    nodes.push({
      label: 'Method',
      name: propName,
      qualifiedName: propQN,
      filePath,
      startLine: prop.getStartLineNumber(),
      endLine: prop.getEndLineNumber(),
      properties: {
        is_async: false,
        is_arrow_property: true,
        is_static: prop.isStatic(),
      },
    });

    edges.push({ sourceQN: qn, targetQN: propQN, type: 'DEFINES_METHOD' });

  }

  // @Input / @Output properties
  for (const prop of cls.getProperties()) {
    const hasInput = !!prop.getDecorator('Input');
    const hasOutput = !!prop.getDecorator('Output');
    if (!hasInput && !hasOutput) continue;

    nodes.push({
      label: 'Property',
      name: prop.getName(),
      qualifiedName: `${qn}.${prop.getName()}`,
      filePath,
      startLine: prop.getStartLineNumber(),
      endLine: prop.getEndLineNumber(),
      properties: {
        is_input: hasInput,
        is_output: hasOutput,
        type: renderTypeText(prop.getType(), typeRoots),
      },
    });
  }
}

// ── Interfaces ────────────────────────────────────────────────────────────────

function extractInterfaces(
  sourceFile: SourceFile,
  filePath: string,
  nodes: GraphNodeDto[],
): void {
  for (const iface of sourceFile.getInterfaces()) {
    const name = iface.getName();
    nodes.push({
      label: 'Interface',
      name,
      qualifiedName: buildQN(filePath, name),
      filePath,
      startLine: iface.getStartLineNumber(),
      endLine: iface.getEndLineNumber(),
      properties: { is_generic: iface.getTypeParameters().length > 0 },
    });
  }
}

// ── Enums ─────────────────────────────────────────────────────────────────────

function extractEnums(
  sourceFile: SourceFile,
  filePath: string,
  nodes: GraphNodeDto[],
): void {
  for (const enumDecl of sourceFile.getEnums()) {
    const name = enumDecl.getName();
    nodes.push({
      label: 'Enum',
      name,
      qualifiedName: buildQN(filePath, name),
      filePath,
      startLine: enumDecl.getStartLineNumber(),
      endLine: enumDecl.getEndLineNumber(),
      properties: {},
    });
  }
}

// ── Angular Routes ────────────────────────────────────────────────────────────
// ── HTTP calls ────────────────────────────────────────────────────────────────
// ── @ApiContract / HttpService.send() detection ──────────────────────────────

function readBackendServiceName(rootPath: string): string | null {
  try {
    const pkgPath = path.join(rootPath, 'package.json');
    const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf-8'));
    // Support both tc.api.name and tc.environments.bff.name
    return pkg?.tc?.api?.name ?? pkg?.tc?.environments?.bff?.name ?? null;
  } catch {
    return null;
  }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

function buildQN(filePath: string, name: string): string {
  // src/app/wallet/wallet.component.ts → app.wallet.wallet.component.WalletComponent
  const withoutExt = filePath.replace(/\.(?:tsx?|jsx?)$/, '').replace(/\\/g, '/');
  const parts = withoutExt.split('/').filter(p => p !== 'src' && p !== '');
  return [...parts, name].join('.');
}

function getStringProperty(obj: ObjectLiteralExpression, propName: string): string | null {
  const prop = obj.getProperty(propName);
  if (!prop || !Node.isPropertyAssignment(prop)) return null;
  const init = prop.getInitializer();
  if (!init) return null;
  if (Node.isStringLiteral(init) || Node.isNoSubstitutionTemplateLiteral(init)) {
    return init.getLiteralValue() as string;
  }
  return null;
}

/// Resolve a property value that may be a string literal or a static class reference (Api.name).
/// Avoids getSymbol()/type checker to prevent expensive resolution on large projects.
/// Pre-scan all classes for static readonly string properties to build a lookup cache.
/// Called once at the start of extraction to avoid repeated file traversals.

function isFrameworkType(typeName: string): boolean {
  return FRAMEWORK_TYPE_PREFIXES.some(prefix => typeName.startsWith(prefix));
}

const SKIP_DIRS = new Set(['node_modules', 'dist', 'cypress', 'e2e', '.git', '.angular']);

function isTsSourceFile(fileName: string): boolean {
  return /\.(?:tsx?|jsx?)$/i.test(fileName) && !/\.d\.ts$|\.spec\.[jt]sx?$/i.test(fileName);
}

// ── Type text ─────────────────────────────────────────────────────────────────

/** The checkout root as given and as resolved through symlinks (e.g. /tmp vs /private/tmp). */
function checkoutRoots(rootPath: string): string[] {
  const roots = new Set<string>([path.resolve(rootPath)]);
  try {
    roots.add(fs.realpathSync(rootPath));
  } catch {
    // A missing root fails later with a clearer error.
  }
  return [...roots].map(normalizeAbsolutePath);
}

/** Forward slashes and a lower-case drive letter, so Windows paths compare reliably. */
function normalizeAbsolutePath(filePath: string): string {
  return filePath.replace(/\\/g, '/').replace(/^([A-Za-z]):\//, (_match, drive: string) => `${drive.toLowerCase()}:/`);
}

/**
 * Type text as the checker renders it, with every `import("<absolute path>")` rewritten to a
 * checkout-independent module path: the package path after the last `node_modules/` for
 * dependencies (pnpm store paths included), else the path relative to the checkout root
 * (`../shared/src/index` for a sibling outside it). The text becomes INJECTS targets (stub
 * qualified names) and node properties, which must be identical for every checkout.
 */
function renderTypeText(type: Type, roots: string[]): string {
  return relativizeImportPaths(type.getText(), roots);
}

export function relativizeImportPaths(text: string, roots: readonly string[]): string {
  const normalizedRoots = roots.map(normalizeAbsolutePath);
  return text.replace(/import\("([^"]*)"\)/g, (_match, modulePath: string) =>
    `import("${toCheckoutIndependentModulePath(modulePath, normalizedRoots)}")`);
}

function toCheckoutIndependentModulePath(modulePath: string, roots: readonly string[]): string {
  const normalized = normalizeAbsolutePath(modulePath);
  if (!path.posix.isAbsolute(normalized) && !/^[a-z]:\//.test(normalized)) return normalized;

  const nodeModules = normalized.lastIndexOf('/node_modules/');
  if (nodeModules >= 0) return normalized.slice(nodeModules + '/node_modules/'.length);

  // Relative to the root it is closest to (the given root or its realpath); a path outside
  // the checkout keeps its directories, so unrelated `index`/`types` modules never collide.
  const candidates = roots.map(root => path.posix.relative(root, normalized)).filter(relative => relative);
  if (candidates.length === 0) return path.posix.basename(normalized);
  return candidates.reduce((best, relative) =>
    relative.split('/').length < best.split('/').length ? relative : best);
}

function findTsFiles(dir: string): string[] {
  const results: string[] = [];
  const walk = (d: string) => {
    for (const entry of fs.readdirSync(d, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        if (!SKIP_DIRS.has(entry.name)) walk(path.join(d, entry.name));
      } else if (isTsSourceFile(entry.name)) {
        results.push(path.join(d, entry.name));
      }
    }
  };
  walk(dir);
  return results;
}
