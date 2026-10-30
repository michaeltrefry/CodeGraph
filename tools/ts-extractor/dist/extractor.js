"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.extractProject = extractProject;
exports.relativizeImportPaths = relativizeImportPaths;
const ts_morph_1 = require("ts-morph");
const path = __importStar(require("path"));
const fs = __importStar(require("fs"));
const workspaceResolver_1 = require("./workspaceResolver");
const discoveryScope_1 = require("./discoveryScope");
const semanticModel_1 = require("./semanticModel");
const reactAnalysis_1 = require("./reactAnalysis");
const httpAnalysis_1 = require("./httpAnalysis");
const routeAnalysis_1 = require("./routeAnalysis");
const tauriAnalysis_1 = require("./tauriAnalysis");
const angularAnalysis_1 = require("./angularAnalysis");
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
function extractProject(repositoryName, rootPath, tsconfigPath, log = () => { }, includedFiles) {
    const nodes = [];
    const edges = [];
    const workspacePackages = [];
    const resolvedImports = [];
    const unresolvedImports = [];
    const unresolvedCalls = [];
    const diagnostics = [];
    // Create project with minimal compiler options and discover files manually.
    // ts-morph with moduleResolution:"bundler" and no include/files can hang, so we
    // scan the filesystem directly instead of relying on tsconfig file resolution.
    // noResolve: true prevents the compiler from pulling in referenced files
    // (e.g. node_modules/@tc/*) which can stall for minutes on large repos.
    const project = new ts_morph_1.Project({
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
    const diagnosticCounts = new Map();
    const diag = (msg) => {
        const category = msg.split(':', 1)[0];
        const count = diagnosticCounts.get(category) ?? 0;
        diagnosticCounts.set(category, count + 1);
        if (count < 25) {
            log(msg);
            diagnostics.push(msg);
        }
        else if (count === 25) {
            const notice = `${category}: further samples omitted`;
            log(notice);
            diagnostics.push(notice);
        }
    };
    // CodeGraph passes the files its discovery admits; the sidecar then never reads outside them.
    const scope = includedFiles ? new discoveryScope_1.DiscoveryScope(rootPath, includedFiles) : undefined;
    const typeRoots = checkoutRoots(rootPath);
    let t0 = Date.now();
    const tsFiles = scope
        ? scope.absoluteFiles(file => /\.[jt]sx?$/.test(file) && !file.split('/').some(segment => SKIP_DIRS.has(segment)))
        : findTsFiles(rootPath);
    diag(`findTsFiles: ${tsFiles.length} files in ${Date.now() - t0}ms`);
    t0 = Date.now();
    let complete = true;
    for (const f of tsFiles) {
        try {
            project.addSourceFileAtPath(f);
        }
        catch (error) {
            complete = false;
            diag(`sourceRead: ${path.relative(rootPath, f)}: ${String(error)}`);
        }
    }
    if (scope)
        for (const file of scope.absoluteFiles(relative => /(?:^|\/)(?:package|[tj]sconfig[^/]*)\.json$/.test(relative))) {
            try {
                const text = fs.readFileSync(file, 'utf8');
                if (path.basename(file) === 'package.json')
                    JSON.parse(text);
                // TypeScript config errors (including extends) are surfaced by workspace resolution below.
            }
            catch (error) {
                complete = false;
                diag(`configRead: ${path.relative(rootPath, file)}: ${String(error)}`);
            }
        }
    const invalidFiles = new Set();
    for (const source of project.getSourceFiles()) {
        const errors = project.getProgram().compilerObject.getSyntacticDiagnostics(source.compilerNode);
        if (errors.length) {
            complete = false;
            invalidFiles.add(source.getFilePath());
            for (const error of errors.slice(0, 5))
                diag(`sourceParse: ${path.relative(rootPath, source.getFilePath())}: ${typeof error.messageText === 'string' ? error.messageText : error.messageText.messageText}`);
        }
    }
    diag(`addSourceFiles: ${Date.now() - t0}ms`);
    const serviceName = !scope || scope.includesFile(path.join(rootPath, 'package.json')) ? readBackendServiceName(rootPath) : null;
    diag(`serviceName: ${serviceName ?? '(none)'}`);
    t0 = Date.now();
    const parsedFiles = project.getSourceFiles().filter(source => !invalidFiles.has(source.getFilePath()));
    const sourceFiles = parsedFiles.filter(source => isTsSourceFile(source.getFilePath()) && !source.getFilePath().includes('.generated.'));
    diag(`getSourceFiles: ${sourceFiles.length} files in ${Date.now() - t0}ms`);
    t0 = Date.now();
    const workspaceResolution = (0, workspaceResolver_1.analyzeWorkspaceImports)(repositoryName, rootPath, tsconfigPath, sourceFiles, log, scope);
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
        if (filePath.includes('node_modules') ||
            filePath.endsWith('.d.ts') ||
            filePath.endsWith('.spec.ts') ||
            filePath.includes('.generated.'))
            continue;
        const fileT0 = Date.now();
        const fileQN = `${repositoryName}:${filePath}`;
        extractImports(sourceFile, fileQN, unresolvedImports);
        extractClasses(sourceFile, repositoryName, filePath, nodes, edges, typeRoots);
        extractInterfaces(sourceFile, filePath, nodes);
        extractEnums(sourceFile, filePath, nodes);
        const fileMs = Date.now() - fileT0;
        filesProcessed++;
        if (fileMs > 500)
            diag(`SLOW file (${fileMs}ms): ${filePath}`);
    }
    diag(`extractionLoop: ${filesProcessed} files in ${Date.now() - t0}ms`);
    const semantic = new semanticModel_1.SemanticModel(repositoryName, rootPath, sourceFiles, workspaceResolution.resolvedImports, diag);
    semantic.extract(nodes, edges, unresolvedCalls);
    (0, reactAnalysis_1.analyzeReact)(semantic, nodes, edges, diag);
    // Reconcile legacy Angular targets using declaration identities, never rendered type strings.
    semantic.reconcileAngular(nodes, edges);
    const angularCoverage = (0, angularAnalysis_1.analyzeAngular)(semantic, nodes, edges, scope, diag);
    complete && (complete = angularCoverage.complete);
    const routeCoverage = (0, routeAnalysis_1.analyzeRoutes)(semantic, nodes, edges, scope, diag);
    complete && (complete = routeCoverage.complete);
    const httpCoverage = (0, httpAnalysis_1.analyzeHttp)(semantic, nodes, edges, diag, serviceName);
    complete && (complete = httpCoverage.complete);
    (0, tauriAnalysis_1.analyzeTauri)(semantic, nodes, edges, diag);
    for (const node of nodes)
        node.properties.extraction_owner = 'typescript';
    for (const edge of edges)
        edge.properties = { ...edge.properties, extraction_owner: 'typescript' };
    diag(`Link coverage: ${JSON.stringify({
        admitted_by_extension: Object.fromEntries(['.js', '.jsx', '.ts', '.tsx'].map(extension => [extension, tsFiles.filter(file => file.endsWith(extension)).length])),
        processed_by_extension: Object.fromEntries(['.js', '.jsx', '.ts', '.tsx'].map(extension => [extension, processedFiles.filter(file => file.endsWith(extension)).length])),
        components: nodes.filter(node => node.label === 'Component').length,
        pages: nodes.filter(node => node.properties.route_kind === 'ui').length,
        screens: nodes.filter(node => node.properties.screen_key !== undefined).length,
        renders: edges.filter(edge => edge.type === 'RENDERS').length,
        calls: edges.filter(edge => edge.type === 'CALLS').length,
        contexts: nodes.filter(node => node.properties.context_kind === 'context').length,
        http: httpCoverage,
        tauri_references: nodes.filter(node => node.properties.transport === 'tauri').length,
        providers: edges.filter(edge => edge.type === 'REGISTERS').length,
        interface_dispatch: edges.filter(edge => edge.properties?.dispatch === 'interface').length,
    })}`);
    if (workspaceResolution.diagnostics.some(message => (message.startsWith('tsconfig ') && !message.includes('No inputs were found')) ||
        message.includes('stopped after') || message.includes('limited from')))
        complete = false;
    if (diagnosticCounts.has('contextTraversal') || diagnosticCounts.has('symbolResolutionLimit') || diagnosticCounts.has('tauriTraversal'))
        complete = false;
    // Edge type summary
    const edgeTypes = {};
    for (const e of edges)
        edgeTypes[e.type] = (edgeTypes[e.type] || 0) + 1;
    diagnostics.push(`Edge types: ${JSON.stringify(edgeTypes)}`);
    return { nodes, edges, workspacePackages, resolvedImports, unresolvedImports, unresolvedCalls, processedFiles, diagnostics, complete };
}
// ── Imports ───────────────────────────────────────────────────────────────────
function extractImports(sourceFile, fileQN, unresolvedImports) {
    for (const importDecl of sourceFile.getImportDeclarations()) {
        unresolvedImports.push({
            fileQN,
            importedNamespace: importDecl.getModuleSpecifierValue(),
        });
    }
}
// ── Classes ───────────────────────────────────────────────────────────────────
function extractClasses(sourceFile, repositoryName, filePath, nodes, edges, typeRoots) {
    for (const cls of sourceFile.getClasses()) {
        extractClass(cls, repositoryName, filePath, nodes, edges, typeRoots);
    }
}
function extractClass(cls, repositoryName, filePath, nodes, edges, typeRoots) {
    const name = cls.getName() ?? (cls.isDefaultExport() ? 'default' : undefined);
    if (!name)
        return;
    const qn = buildQN(filePath, name);
    const decoratorNames = cls.getDecorators().map(d => d.getName());
    const isComponent = decoratorNames.includes('Component');
    const isInjectable = decoratorNames.includes('Injectable');
    const isNgModule = decoratorNames.includes('NgModule');
    const isPipe = decoratorNames.includes('Pipe');
    const properties = {
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
            if (args.length > 0 && ts_morph_1.Node.isObjectLiteralExpression(args[0])) {
                const obj = args[0];
                const selector = getStringProperty(obj, 'selector');
                const templateUrl = getStringProperty(obj, 'templateUrl');
                if (selector)
                    properties['selector'] = selector;
                if (templateUrl)
                    properties['templateUrl'] = templateUrl;
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
    if (base)
        edges.push({ sourceQN: qn, targetQN: base.getExpression().getText(), type: 'INHERITS' });
    // IMPLEMENTS
    for (const iface of cls.getImplements()) {
        const ifaceName = iface.getExpression().getText();
        edges.push({ sourceQN: qn, targetQN: ifaceName, type: 'IMPLEMENTS' });
    }
    // Constructor INJECTS
    for (const ctor of cls.getConstructors()) {
        for (const param of ctor.getParameters()) {
            const typeName = renderTypeText(param.getType(), typeRoots);
            if (isFrameworkType(typeName) || PRIMITIVE_TYPES.has(typeName))
                continue;
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
        if (!init || !ts_morph_1.Node.isCallExpression(init))
            continue;
        if (init.getExpression().getText() !== 'inject')
            continue;
        const args = init.getArguments();
        if (args.length === 0)
            continue;
        const typeName = args[0].getText();
        if (isFrameworkType(typeName) || PRIMITIVE_TYPES.has(typeName))
            continue;
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
        if (!init)
            continue;
        // Skip inject() calls — already handled above
        if (ts_morph_1.Node.isCallExpression(init) && init.getExpression().getText() === 'inject')
            continue;
        // Check if this property initializer contains any call expressions worth scanning
        const hasCalls = init.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression).length > 0;
        if (!hasCalls)
            continue;
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
        if (!hasInput && !hasOutput)
            continue;
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
function extractInterfaces(sourceFile, filePath, nodes) {
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
function extractEnums(sourceFile, filePath, nodes) {
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
function readBackendServiceName(rootPath) {
    try {
        const pkgPath = path.join(rootPath, 'package.json');
        const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf-8'));
        // Support both tc.api.name and tc.environments.bff.name
        return pkg?.tc?.api?.name ?? pkg?.tc?.environments?.bff?.name ?? null;
    }
    catch {
        return null;
    }
}
// ── Helpers ───────────────────────────────────────────────────────────────────
function buildQN(filePath, name) {
    // src/app/wallet/wallet.component.ts → app.wallet.wallet.component.WalletComponent
    const withoutExt = filePath.replace(/\.(?:tsx?|jsx?)$/, '').replace(/\\/g, '/');
    const parts = withoutExt.split('/').filter(p => p !== 'src' && p !== '');
    return [...parts, name].join('.');
}
function getStringProperty(obj, propName) {
    const prop = obj.getProperty(propName);
    if (!prop || !ts_morph_1.Node.isPropertyAssignment(prop))
        return null;
    const init = prop.getInitializer();
    if (!init)
        return null;
    if (ts_morph_1.Node.isStringLiteral(init) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(init)) {
        return init.getLiteralValue();
    }
    return null;
}
/// Resolve a property value that may be a string literal or a static class reference (Api.name).
/// Avoids getSymbol()/type checker to prevent expensive resolution on large projects.
/// Pre-scan all classes for static readonly string properties to build a lookup cache.
/// Called once at the start of extraction to avoid repeated file traversals.
function isFrameworkType(typeName) {
    return FRAMEWORK_TYPE_PREFIXES.some(prefix => typeName.startsWith(prefix));
}
const SKIP_DIRS = new Set(['node_modules', 'dist', 'cypress', 'e2e', '.git', '.angular']);
function isTsSourceFile(fileName) {
    return /\.(?:tsx?|jsx?)$/.test(fileName) && !/\.d\.ts$|\.spec\.[jt]sx?$/.test(fileName);
}
// ── Type text ─────────────────────────────────────────────────────────────────
/** The checkout root as given and as resolved through symlinks (e.g. /tmp vs /private/tmp). */
function checkoutRoots(rootPath) {
    const roots = new Set([path.resolve(rootPath)]);
    try {
        roots.add(fs.realpathSync(rootPath));
    }
    catch {
        // A missing root fails later with a clearer error.
    }
    return [...roots].map(normalizeAbsolutePath);
}
/** Forward slashes and a lower-case drive letter, so Windows paths compare reliably. */
function normalizeAbsolutePath(filePath) {
    return filePath.replace(/\\/g, '/').replace(/^([A-Za-z]):\//, (_match, drive) => `${drive.toLowerCase()}:/`);
}
/**
 * Type text as the checker renders it, with every `import("<absolute path>")` rewritten to a
 * checkout-independent module path: the package path after the last `node_modules/` for
 * dependencies (pnpm store paths included), else the path relative to the checkout root
 * (`../shared/src/index` for a sibling outside it). The text becomes INJECTS targets (stub
 * qualified names) and node properties, which must be identical for every checkout.
 */
function renderTypeText(type, roots) {
    return relativizeImportPaths(type.getText(), roots);
}
function relativizeImportPaths(text, roots) {
    const normalizedRoots = roots.map(normalizeAbsolutePath);
    return text.replace(/import\("([^"]*)"\)/g, (_match, modulePath) => `import("${toCheckoutIndependentModulePath(modulePath, normalizedRoots)}")`);
}
function toCheckoutIndependentModulePath(modulePath, roots) {
    const normalized = normalizeAbsolutePath(modulePath);
    if (!path.posix.isAbsolute(normalized) && !/^[a-z]:\//.test(normalized))
        return normalized;
    const nodeModules = normalized.lastIndexOf('/node_modules/');
    if (nodeModules >= 0)
        return normalized.slice(nodeModules + '/node_modules/'.length);
    // Relative to the root it is closest to (the given root or its realpath); a path outside
    // the checkout keeps its directories, so unrelated `index`/`types` modules never collide.
    const candidates = roots.map(root => path.posix.relative(root, normalized)).filter(relative => relative);
    if (candidates.length === 0)
        return path.posix.basename(normalized);
    return candidates.reduce((best, relative) => relative.split('/').length < best.split('/').length ? relative : best);
}
function findTsFiles(dir) {
    const results = [];
    const walk = (d) => {
        for (const entry of fs.readdirSync(d, { withFileTypes: true })) {
            if (entry.isDirectory()) {
                if (!SKIP_DIRS.has(entry.name))
                    walk(path.join(d, entry.name));
            }
            else if (isTsSourceFile(entry.name)) {
                results.push(path.join(d, entry.name));
            }
        }
    };
    walk(dir);
    return results;
}
