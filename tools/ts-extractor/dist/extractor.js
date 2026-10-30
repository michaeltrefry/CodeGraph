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
const boundedTextReader_1 = require("./boundedTextReader");
const extractionEdges_1 = require("./extractionEdges");
const semanticModel_1 = require("./semanticModel");
const extractionWork_1 = require("./extractionWork");
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
function extractProject(repositoryName, rootPath, tsconfigPath, log = () => { }, includedFiles, limits = {}, workControl = {}) {
    try {
        const work = new extractionWork_1.ExtractionWorkBudget(workControl);
        work.charge(0);
        (0, boundedTextReader_1.validateSourceScope)(includedFiles);
        return extractProjectCore(repositoryName, rootPath, tsconfigPath, log, includedFiles, limits, work);
    }
    catch (error) {
        const refusal = error;
        const reason = error instanceof boundedTextReader_1.SourceAdmissionError ? error.message : refusal.code === 'vite_library_analysis_incomplete' ? 'Vite library analysis work limit or cancellation' : refusal.code === 'extraction_work_incomplete' ? 'semantic analysis work limit or cancellation' : `analysis failure (${refusal.code ?? refusal.name ?? 'unknown'})`;
        const message = `sourceAdmission: extraction refused: ${reason.slice(0, 240)}`;
        log(message);
        return { complete: false, nodes: [], edges: [], workspacePackages: [], resolvedImports: [], unresolvedImports: [], unresolvedCalls: [], processedFiles: [], diagnostics: [message] };
    }
}
function extractProjectCore(repositoryName, rootPath, tsconfigPath, log, includedFiles, limits, work) {
    const nodes = [];
    const edges = [];
    const workspacePackages = [];
    const resolvedImports = [];
    const unresolvedImports = [];
    const unresolvedCalls = [];
    const diagnostics = [];
    const refused = () => ({ complete: false, nodes: [], edges: [], workspacePackages: [],
        resolvedImports: [], unresolvedImports: [], unresolvedCalls: [], processedFiles: [], diagnostics });
    // Create project with minimal compiler options and discover files manually.
    // ts-morph with moduleResolution:"bundler" and no include/files can hang, so we
    // scan the filesystem directly instead of relying on tsconfig file resolution.
    // noResolve: true prevents the compiler from pulling in referenced files
    // (e.g. node_modules/@tc/*) which can stall for minutes on large repos.
    const project = new ts_morph_1.Project({
        useInMemoryFileSystem: true,
        skipFileDependencyResolution: true,
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
    const reader = new boundedTextReader_1.BoundedTextReader(rootPath, scope ? file => scope.includesFile(file) : undefined, limits, diag);
    if (!reader.complete)
        throw new boundedTextReader_1.SourceAdmissionError('source reader initialization was refused');
    const typeRoots = checkoutRoots(rootPath);
    let t0 = Date.now();
    // Explicit discovery has already applied repository safety/exclusion policy. Standalone
    // walk defaults must not silently remove an admitted source before processed-file accounting.
    const tsFiles = scope
        ? scope.absoluteFiles(file => /\.[jt]sx?$/i.test(file))
        : findTsFiles(rootPath, reader);
    diag(`findTsFiles: ${tsFiles.length} files in ${Date.now() - t0}ms`);
    t0 = Date.now();
    let complete = true;
    for (const f of tsFiles) {
        try {
            const text = reader.read(f, true);
            if (text === undefined) {
                complete = false;
                continue;
            }
            // Reserve parse/source retention before the compiler allocates the next file. Count raw
            // compiler nodes immediately afterwards, before another file or ts-morph wrappers accumulate.
            work.reserveSource(text.length);
            const source = project.createSourceFile(f, text);
            const pending = [source.compilerNode];
            while (pending.length) {
                const node = pending.pop();
                work.retainAstNode();
                ts_morph_1.ts.forEachChild(node, child => { pending.push(child); });
            }
        }
        catch (error) {
            if (error.code === 'extraction_work_incomplete')
                throw error;
            complete = false;
            diag(`sourceRead: ${path.relative(rootPath, f)}: ${String(error)}`);
        }
    }
    if (scope)
        for (const file of scope.absoluteFiles(relative => /(?:^|\/)(?:package|[tj]sconfig[^/]*)\.json$/.test(relative))) {
            try {
                const text = reader.read(file, true);
                if (text === undefined) {
                    complete = false;
                    continue;
                }
                if (path.basename(file) === 'package.json')
                    JSON.parse(text);
                // TypeScript config errors (including extends) are surfaced by workspace resolution below.
            }
            catch (error) {
                complete = false;
                diag(`configRead: ${path.relative(rootPath, file).slice(0, 240)}: invalid configuration`);
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
    if (!reader.complete)
        return refused();
    const serviceName = !scope || scope.includesFile(path.join(rootPath, 'package.json')) ? readBackendServiceName(rootPath, reader) : null;
    diag(`serviceName: ${serviceName ?? '(none)'}`);
    t0 = Date.now();
    const parsedFiles = project.getSourceFiles().filter(source => !invalidFiles.has(source.getFilePath()));
    const sourceFiles = parsedFiles.filter(source => isTsSourceFile(source.getFilePath()) && !/\.generated\./i.test(source.getFilePath()));
    diag(`getSourceFiles: ${sourceFiles.length} files in ${Date.now() - t0}ms`);
    t0 = Date.now();
    const workspaceResolution = (0, workspaceResolver_1.analyzeWorkspaceImports)(repositoryName, rootPath, tsconfigPath, sourceFiles, log, scope, reader);
    if (!reader.complete)
        return refused();
    for (const node of workspaceResolution.nodes) {
        work.reserveOutput('node', node.qualifiedName.length + node.name.length + node.filePath.length);
        nodes.push(node);
    }
    for (const edge of workspaceResolution.edges) {
        work.reserveOutput('edge', edge.sourceQN.length + edge.targetQN.length);
        edges.push(edge);
    }
    for (const item of workspaceResolution.workspacePackages) {
        work.reserveOutput('metadata', item.name.length + item.qualifiedName.length + item.rootPath.length);
        workspacePackages.push(item);
    }
    for (const item of workspaceResolution.resolvedImports) {
        work.reserveOutput('metadata', item.fileQN.length + item.importedNamespace.length);
        resolvedImports.push(item);
    }
    for (const item of workspaceResolution.unresolvedImports) {
        work.reserveOutput('metadata', item.fileQN.length + item.importedNamespace.length);
        unresolvedImports.push(item);
    }
    for (const message of workspaceResolution.diagnostics) {
        work.text(message);
        diagnostics.push(message);
    }
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
        extractImports(sourceFile, fileQN, unresolvedImports, work);
        extractClasses(sourceFile, repositoryName, filePath, nodes, edges, typeRoots, work);
        extractInterfaces(sourceFile, filePath, nodes, work);
        extractEnums(sourceFile, filePath, nodes, work);
        const fileMs = Date.now() - fileT0;
        filesProcessed++;
        if (fileMs > 500)
            diag(`SLOW file (${fileMs}ms): ${filePath}`);
    }
    diag(`extractionLoop: ${filesProcessed} files in ${Date.now() - t0}ms`);
    const semantic = new semanticModel_1.SemanticModel(repositoryName, rootPath, sourceFiles, workspaceResolution.resolvedImports, diag, work);
    semantic.extract(nodes, edges, unresolvedCalls);
    (0, reactAnalysis_1.analyzeReact)(semantic, nodes, edges, diag);
    // Reconcile legacy Angular targets using declaration identities, never rendered type strings.
    semantic.reconcileAngular(nodes, edges);
    const angularCoverage = (0, angularAnalysis_1.analyzeAngular)(semantic, nodes, edges, scope, diag, reader);
    complete && (complete = angularCoverage.complete);
    if (!reader.complete)
        return refused();
    const routeCoverage = (0, routeAnalysis_1.analyzeRoutes)(semantic, nodes, edges, scope, diag, reader);
    complete && (complete = routeCoverage.complete);
    if (!reader.complete)
        return refused();
    const httpCoverage = (0, httpAnalysis_1.analyzeHttp)(semantic, nodes, edges, diag, serviceName);
    complete && (complete = httpCoverage.complete);
    (0, tauriAnalysis_1.analyzeTauri)(semantic, nodes, edges, diag);
    semantic.reconcilePackageExports(nodes);
    // Reserve grouping/key/evidence copies before canonicalization, including singleton facts.
    // This also bounds recursive canonical metadata depth before its implementation is invoked.
    const reserveProperties = (properties) => {
        const pending = [{ value: properties, depth: 0 }];
        while (pending.length) {
            const { value, depth } = pending.pop();
            work.checkDepth(depth, 64);
            if (typeof value === 'string') {
                work.charge(value.length * 6 + 2);
                continue;
            }
            if (Array.isArray(value)) {
                work.charge(value.length + 1);
                for (const item of value)
                    pending.push({ value: item, depth: depth + 1 });
                continue;
            }
            if (value !== null && typeof value === 'object') {
                const keys = Object.keys(value);
                work.charge(keys.length + 1);
                for (const key of keys) {
                    work.charge(key.length * 6 + 4);
                    pending.push({ value: value[key], depth: depth + 1 });
                }
            }
            else
                work.charge(16);
        }
    };
    const quotedUnits = (value) => {
        work.charge(value.length + 1);
        let units = 2;
        for (let index = 0; index < value.length; index++) {
            const code = value.charCodeAt(index);
            units += code < 32 ? 6 : code === 34 || code === 92 ? 2 : 1;
        }
        return units;
    };
    for (const edge of edges) {
        work.reserveOutput('metadata', quotedUnits(edge.sourceQN) + quotedUnits(edge.targetQN) + quotedUnits(edge.type) + 4);
        reserveProperties(edge.properties);
    }
    const canonicalEdges = (0, extractionEdges_1.canonicalExtractionEdges)(edges);
    work.charge(canonicalEdges.length + 1);
    for (let index = 0; index < canonicalEdges.length; index++)
        edges[index] = canonicalEdges[index];
    edges.length = canonicalEdges.length;
    for (const node of nodes) {
        work.reserveOutput('metadata', 32);
        node.properties.extraction_owner = 'typescript';
    }
    for (const edge of edges) {
        work.reserveOutput('metadata', 32);
        const keys = Object.keys(edge.properties ?? {});
        work.charge(keys.length * 8 + 1);
        edge.properties = { ...edge.properties, extraction_owner: 'typescript' };
    }
    const screenSelections = edges.filter(edge => edge.type === 'RENDERS').flatMap(edge => Array.isArray(edge.properties?.screen_selections) ? edge.properties.screen_selections :
        edge.properties?.screen_key !== undefined ? [edge.properties] : []);
    diag(`Link coverage: ${JSON.stringify({
        admitted_by_extension: Object.fromEntries(['.js', '.jsx', '.ts', '.tsx'].map(extension => [extension, tsFiles.filter(file => file.toLowerCase().endsWith(extension)).length])),
        processed_by_extension: Object.fromEntries(['.js', '.jsx', '.ts', '.tsx'].map(extension => [extension, processedFiles.filter(file => file.toLowerCase().endsWith(extension)).length])),
        components: nodes.filter(node => node.label === 'Component').length,
        pages: nodes.filter(node => node.properties.route_kind === 'ui').length,
        screens: new Set(screenSelections.map(selection => JSON.stringify([selection.screen_selector_owner, selection.screen_selector, selection.screen_key]))).size,
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
    if (diagnosticCounts.has('contextTraversal') || diagnosticCounts.has('symbolResolutionLimit') || diagnosticCounts.has('tauriTraversal') || diagnosticCounts.has('reactLazyTraversal'))
        complete = false;
    // Edge type summary
    const edgeTypes = {};
    for (const e of edges)
        edgeTypes[e.type] = (edgeTypes[e.type] || 0) + 1;
    diagnostics.push(`Edge types: ${JSON.stringify(edgeTypes)}`);
    complete && (complete = reader.complete);
    return { nodes, edges, workspacePackages, resolvedImports, unresolvedImports, unresolvedCalls,
        processedFiles: reader.complete ? processedFiles : [], diagnostics, complete };
}
// ── Imports ───────────────────────────────────────────────────────────────────
function extractImports(sourceFile, fileQN, unresolvedImports, work) {
    work.charge(sourceFile.compilerNode.statements.length + 1);
    for (const importDecl of sourceFile.getImportDeclarations()) {
        work.reserveOutput('metadata', fileQN.length + importDecl.getModuleSpecifier().getFullWidth());
        unresolvedImports.push({
            fileQN,
            importedNamespace: importDecl.getModuleSpecifierValue(),
        });
    }
}
// ── Classes ───────────────────────────────────────────────────────────────────
function extractClasses(sourceFile, repositoryName, filePath, nodes, edges, typeRoots, work) {
    work.charge(sourceFile.compilerNode.statements.length + 1);
    for (const cls of sourceFile.getClasses()) {
        extractClass(cls, repositoryName, filePath, nodes, edges, typeRoots, work);
    }
}
function extractClass(cls, repositoryName, filePath, nodes, edges, typeRoots, work) {
    work.reserveOutput('node', filePath.length + (cls.getNameNode()?.getFullWidth() ?? 16));
    work.charge(cls.compilerNode.members.length + cls.getFullWidth() + 1);
    const name = cls.getName() ?? (cls.isDefaultExport() ? 'default' : undefined);
    if (!name)
        return;
    const qn = buildQN(filePath, name);
    work.charge((cls.compilerNode.modifiers?.length ?? 0) + 1);
    const decoratorNames = cls.getDecorators().map(d => { work.charge(d.getFullWidth() + 1); return d.getName(); });
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
            work.charge(dec.getFullWidth() + 1);
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
    if (base) {
        work.reserveOutput('edge', qn.length + base.getFullWidth());
        edges.push({ sourceQN: qn, targetQN: base.getExpression().getText(), type: 'INHERITS' });
    }
    // IMPLEMENTS
    work.charge((cls.compilerNode.heritageClauses?.length ?? 0) + 1);
    for (const iface of cls.getImplements()) {
        work.reserveOutput('edge', qn.length + iface.getFullWidth());
        const ifaceName = iface.getExpression().getText();
        edges.push({ sourceQN: qn, targetQN: ifaceName, type: 'IMPLEMENTS' });
    }
    // Constructor INJECTS
    for (const ctor of cls.getConstructors()) {
        work.charge(ctor.compilerNode.parameters.length + 1);
        for (const param of ctor.getParameters()) {
            work.reserveOutput('edge', qn.length + param.getFullWidth());
            const typeName = renderTypeText(param.getType(), typeRoots, work);
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
    work.charge(cls.compilerNode.members.length + 1);
    for (const prop of cls.getProperties()) {
        const init = prop.getInitializer();
        if (!init || !ts_morph_1.Node.isCallExpression(init))
            continue;
        work.charge(init.getExpression().getFullWidth() + 1);
        if (init.getExpression().getText() !== 'inject')
            continue;
        const args = init.getArguments();
        if (args.length === 0)
            continue;
        work.reserveOutput('edge', qn.length + args[0].getFullWidth() + prop.getNameNode().getFullWidth());
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
        work.reserveOutput('node', filePath.length + qn.length + method.getNameNode().getFullWidth());
        work.reserveOutput('edge', qn.length * 2 + method.getNameNode().getFullWidth());
        work.charge(method.getFullWidth() + method.compilerNode.parameters.length + 1);
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
                return_type: renderTypeText(method.getReturnType(), typeRoots, work),
                parameter_count: method.getParameters().length,
                is_static: method.isStatic(),
            },
        });
        edges.push({ sourceQN: qn, targetQN: methodQN, type: 'DEFINES_METHOD' });
        // HTTP calls within the method body
    }
    // Arrow function properties (e.g. searchDomains$ = () => this.httpService.send(...))
    work.charge(cls.compilerNode.members.length + 1);
    for (const prop of cls.getProperties()) {
        const init = prop.getInitializer();
        if (!init)
            continue;
        // Skip inject() calls — already handled above
        if (ts_morph_1.Node.isCallExpression(init))
            work.charge(init.getExpression().getFullWidth() + 1);
        if (ts_morph_1.Node.isCallExpression(init) && init.getExpression().getText() === 'inject')
            continue;
        // Check if this property initializer contains any call expressions worth scanning
        work.charge(init.getFullWidth() + 1);
        let hasCalls = false;
        const pending = [init.compilerNode];
        while (pending.length) {
            work.charge();
            const node = pending.pop();
            if (node !== init.compilerNode && ts_morph_1.ts.isCallExpression(node)) {
                hasCalls = true;
                break;
            }
            ts_morph_1.ts.forEachChild(node, child => { pending.push(child); });
        }
        if (!hasCalls)
            continue;
        work.reserveOutput('node', filePath.length + qn.length + prop.getNameNode().getFullWidth());
        work.reserveOutput('edge', qn.length * 2 + prop.getNameNode().getFullWidth());
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
    work.charge(cls.compilerNode.members.length + 1);
    for (const prop of cls.getProperties()) {
        work.charge(prop.getFullWidth() + 1);
        const hasInput = !!prop.getDecorator('Input');
        const hasOutput = !!prop.getDecorator('Output');
        if (!hasInput && !hasOutput)
            continue;
        work.reserveOutput('node', filePath.length + qn.length + prop.getNameNode().getFullWidth());
        work.charge(prop.getFullWidth() + 1);
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
                type: renderTypeText(prop.getType(), typeRoots, work),
            },
        });
    }
}
// ── Interfaces ────────────────────────────────────────────────────────────────
function extractInterfaces(sourceFile, filePath, nodes, work) {
    work.charge(sourceFile.compilerNode.statements.length + 1);
    for (const iface of sourceFile.getInterfaces()) {
        work.reserveOutput('node', filePath.length + iface.getNameNode().getFullWidth());
        work.charge(iface.compilerNode.typeParameters?.length ?? 0);
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
function extractEnums(sourceFile, filePath, nodes, work) {
    work.charge(sourceFile.compilerNode.statements.length + 1);
    for (const enumDecl of sourceFile.getEnums()) {
        work.reserveOutput('node', filePath.length + enumDecl.getNameNode().getFullWidth());
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
function readBackendServiceName(rootPath, reader) {
    try {
        const pkgPath = path.join(rootPath, 'package.json');
        const text = reader.read(pkgPath);
        const pkg = text === undefined ? null : JSON.parse(text);
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
    return /\.(?:tsx?|jsx?)$/i.test(fileName) && !/\.d\.ts$|\.spec\.[jt]sx?$/i.test(fileName);
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
function renderTypeText(type, roots, work) {
    work.charge(256);
    const text = type.getText();
    work.charge(text.length * 6 + 1);
    return relativizeImportPaths(text, roots);
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
function findTsFiles(dir, reader) {
    const results = [];
    const walk = (d) => {
        for (const entry of reader.entries(d)) {
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
