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
const ts_morph_1 = require("ts-morph");
const path = __importStar(require("path"));
const fs = __importStar(require("fs"));
const HTTP_METHODS = new Set(['get', 'post', 'put', 'delete', 'patch', 'head']);
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
function extractProject(projectName, rootPath, tsconfigPath, log = () => { }) {
    const nodes = [];
    const edges = [];
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
            allowJs: false,
            skipLibCheck: true,
            noResolve: true,
            experimentalDecorators: true,
            target: 2 /* ES2015 */,
        },
        skipAddingFilesFromTsConfig: true,
    });
    const diag = (msg) => { log(msg); diagnostics.push(msg); };
    let t0 = Date.now();
    const tsFiles = findTsFiles(rootPath);
    diag(`findTsFiles: ${tsFiles.length} files in ${Date.now() - t0}ms`);
    t0 = Date.now();
    for (const f of tsFiles)
        project.addSourceFileAtPath(f);
    diag(`addSourceFiles: ${Date.now() - t0}ms`);
    t0 = Date.now();
    buildStaticStringCache(project, rootPath);
    diag(`buildStaticStringCache: ${Date.now() - t0}ms`);
    t0 = Date.now();
    const contractMap = buildApiContractMap(project, rootPath);
    diag(`buildApiContractMap: ${Date.now() - t0}ms`);
    const serviceName = readBackendServiceName(rootPath);
    diag(`contractMap: ${contractMap.size} entries, serviceName: ${serviceName ?? '(none)'}`);
    diag(`staticStringCache: ${_staticStringCache.size} entries`);
    t0 = Date.now();
    const sourceFiles = project.getSourceFiles();
    diag(`getSourceFiles: ${sourceFiles.length} files in ${Date.now() - t0}ms`);
    let filesProcessed = 0;
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
        const fileQN = `${projectName}:${filePath}`;
        extractImports(sourceFile, fileQN, unresolvedImports);
        extractClasses(sourceFile, projectName, filePath, nodes, edges, contractMap, serviceName);
        extractInterfaces(sourceFile, filePath, nodes);
        extractEnums(sourceFile, filePath, nodes);
        extractAngularRoutes(sourceFile, projectName, filePath, nodes, edges);
        const fileMs = Date.now() - fileT0;
        filesProcessed++;
        if (fileMs > 500)
            diag(`SLOW file (${fileMs}ms): ${filePath}`);
    }
    diag(`extractionLoop: ${filesProcessed} files in ${Date.now() - t0}ms`);
    // Edge type summary
    const edgeTypes = {};
    for (const e of edges)
        edgeTypes[e.type] = (edgeTypes[e.type] || 0) + 1;
    diagnostics.push(`Edge types: ${JSON.stringify(edgeTypes)}`);
    return { nodes, edges, unresolvedImports, unresolvedCalls, diagnostics };
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
function extractClasses(sourceFile, projectName, filePath, nodes, edges, contractMap, serviceName) {
    for (const cls of sourceFile.getClasses()) {
        extractClass(cls, projectName, filePath, nodes, edges, contractMap, serviceName);
    }
}
function extractClass(cls, projectName, filePath, nodes, edges, contractMap, serviceName) {
    const name = cls.getName();
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
    const baseClass = cls.getBaseClass();
    if (baseClass) {
        const baseName = baseClass.getName() ?? 'unknown';
        edges.push({ sourceQN: qn, targetQN: baseName, type: 'INHERITS' });
    }
    // IMPLEMENTS
    for (const iface of cls.getImplements()) {
        const ifaceName = iface.getExpression().getText();
        edges.push({ sourceQN: qn, targetQN: ifaceName, type: 'IMPLEMENTS' });
    }
    // Constructor INJECTS
    for (const ctor of cls.getConstructors()) {
        for (const param of ctor.getParameters()) {
            const typeName = param.getType().getText();
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
                return_type: method.getReturnType().getText(),
                parameter_count: method.getParameters().length,
                is_static: method.isStatic(),
            },
        });
        edges.push({ sourceQN: qn, targetQN: methodQN, type: 'DEFINES_METHOD' });
        // HTTP calls within the method body
        extractHttpCalls(method, methodQN, edges);
        extractApiContractCalls(method, methodQN, contractMap, serviceName, edges);
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
        extractHttpCalls(init, propQN, edges);
        extractApiContractCalls(init, propQN, contractMap, serviceName, edges);
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
                type: prop.getType().getText(),
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
function extractAngularRoutes(sourceFile, projectName, filePath, nodes, edges) {
    for (const varDecl of sourceFile.getVariableDeclarations()) {
        const typeText = varDecl.getTypeNode()?.getText() ?? '';
        if (!typeText.includes('Routes') && !typeText.includes('Route[]'))
            continue;
        const initializer = varDecl.getInitializer();
        if (!initializer || !ts_morph_1.Node.isArrayLiteralExpression(initializer))
            continue;
        for (const element of initializer.getElements()) {
            if (!ts_morph_1.Node.isObjectLiteralExpression(element))
                continue;
            const routePath = getStringProperty(element, 'path');
            const componentProp = element.getProperty('component');
            if (routePath === null || !componentProp)
                continue;
            let componentName = '';
            if (ts_morph_1.Node.isPropertyAssignment(componentProp)) {
                const init = componentProp.getInitializer();
                if (init)
                    componentName = init.getText();
            }
            if (!componentName)
                continue;
            const routeQN = `route:${projectName}:GET:${routePath || '/'}`;
            nodes.push({
                label: 'Route',
                name: `GET /${routePath}`,
                qualifiedName: routeQN,
                filePath,
                startLine: element.getStartLineNumber(),
                endLine: element.getEndLineNumber(),
                properties: {
                    http_method: 'GET',
                    route_template: routePath,
                    component: componentName,
                },
            });
            edges.push({ sourceQN: routeQN, targetQN: componentName, type: 'HANDLES' });
        }
    }
}
// ── HTTP calls ────────────────────────────────────────────────────────────────
function extractHttpCalls(node, callerQN, edges) {
    for (const call of node.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
        const expr = call.getExpression();
        if (!ts_morph_1.Node.isPropertyAccessExpression(expr))
            continue;
        const methodName = expr.getName();
        if (!HTTP_METHODS.has(methodName))
            continue;
        // Check resolved type first; fall back to property name when types don't resolve
        // (e.g. node_modules not installed in cached repos)
        const receiverType = expr.getExpression().getType().getText();
        const receiverName = expr.getExpression().getText().split('.').pop() || '';
        const isHttpClient = receiverType.includes('HttpClient')
            || /^_?http(Client)?$/i.test(receiverName);
        if (!isHttpClient)
            continue;
        const args = call.getArguments();
        if (args.length === 0)
            continue;
        const firstArg = args[0];
        let urlPattern = null;
        if (ts_morph_1.Node.isStringLiteral(firstArg) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(firstArg)) {
            urlPattern = firstArg.getLiteralValue();
        }
        else if (ts_morph_1.Node.isTemplateExpression(firstArg)) {
            urlPattern = firstArg.getHead().getLiteralText();
            for (const span of firstArg.getTemplateSpans()) {
                urlPattern += '{param}' + span.getLiteral().getLiteralText();
            }
        }
        if (!urlPattern)
            continue;
        edges.push({
            sourceQN: callerQN,
            targetQN: `route:*:${methodName.toUpperCase()}:${urlPattern}`,
            type: 'HTTP_CALLS',
            properties: {
                http_method: methodName.toUpperCase(),
                url_pattern: urlPattern,
                confidence_band: 'high',
            },
        });
    }
}
// ── @ApiContract / HttpService.send() detection ──────────────────────────────
function buildApiContractMap(project, rootPath) {
    const map = new Map();
    for (const sourceFile of project.getSourceFiles()) {
        const absPath = sourceFile.getFilePath();
        const filePath = path.relative(rootPath, absPath).replace(/\\/g, '/');
        if (filePath.includes('node_modules') || filePath.endsWith('.d.ts'))
            continue;
        for (const cls of sourceFile.getClasses()) {
            const decorator = cls.getDecorator('ApiContract');
            if (!decorator)
                continue;
            const args = decorator.getArguments();
            if (args.length === 0 || !ts_morph_1.Node.isObjectLiteralExpression(args[0]))
                continue;
            const obj = args[0];
            const endpoint = resolveStringValue(obj, 'endpoint', project);
            if (!endpoint)
                continue;
            let httpMethod = 'POST';
            const methodProp = obj.getProperty('httpMethod');
            if (methodProp && ts_morph_1.Node.isPropertyAssignment(methodProp)) {
                const init = methodProp.getInitializer();
                if (init) {
                    const text = init.getText();
                    // Handle HttpMethods.get, HttpMethods.post, etc.
                    if (text.includes('.')) {
                        httpMethod = text.split('.').pop().toUpperCase();
                    }
                    else if (ts_morph_1.Node.isStringLiteral(init)) {
                        httpMethod = init.getLiteralValue().toUpperCase();
                    }
                }
            }
            const className = cls.getName();
            if (className) {
                map.set(className, { endpoint, httpMethod });
            }
        }
    }
    return map;
}
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
function extractApiContractCalls(node, callerQN, contractMap, serviceName, edges) {
    if (contractMap.size === 0)
        return;
    for (const call of node.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
        const expr = call.getExpression();
        if (!ts_morph_1.Node.isPropertyAccessExpression(expr))
            continue;
        if (expr.getName() !== 'send')
            continue;
        // Resolve request class name from `new XxxRequest(...)` argument
        let requestClassName = null;
        const args = call.getArguments();
        for (const arg of args) {
            // Direct: send(new SearchDomainsRequest(...))
            if (ts_morph_1.Node.isNewExpression(arg)) {
                requestClassName = arg.getExpression().getText();
                break;
            }
            // Nested: send might be inside a wrapper, check descendants
            const nested = arg.getDescendantsOfKind(ts_morph_1.SyntaxKind.NewExpression);
            if (nested.length > 0) {
                requestClassName = nested[0].getExpression().getText();
                break;
            }
        }
        // Fallback: try type arguments send<RequestType, ResponseType>(...)
        if (!requestClassName) {
            const typeArgs = call.getTypeArguments();
            if (typeArgs.length > 0) {
                requestClassName = typeArgs[0].getText();
            }
        }
        if (!requestClassName)
            continue;
        const contract = contractMap.get(requestClassName);
        if (!contract)
            continue;
        const targetProject = serviceName ? `TC.${serviceName}` : '*';
        edges.push({
            sourceQN: callerQN,
            targetQN: `route:${targetProject}:${contract.httpMethod}:${contract.endpoint}`,
            type: 'HTTP_CALLS',
            properties: {
                http_method: contract.httpMethod,
                url_pattern: contract.endpoint,
                request_dto: requestClassName,
                confidence_band: 'high',
                ...(serviceName ? { service_name: serviceName } : {}),
            },
        });
    }
}
// ── Helpers ───────────────────────────────────────────────────────────────────
function buildQN(filePath, name) {
    // src/app/wallet/wallet.component.ts → app.wallet.wallet.component.WalletComponent
    const withoutExt = filePath.replace(/\.ts$/, '').replace(/\\/g, '/');
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
function resolveStringValue(obj, propName, project) {
    const prop = obj.getProperty(propName);
    if (!prop || !ts_morph_1.Node.isPropertyAssignment(prop))
        return null;
    const init = prop.getInitializer();
    if (!init)
        return null;
    // Direct string literal: endpoint: 'PlaceBid'
    if (ts_morph_1.Node.isStringLiteral(init) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(init)) {
        return init.getLiteralValue();
    }
    // Property access: endpoint: Api.placeBid → resolve by finding the class manually
    if (ts_morph_1.Node.isPropertyAccessExpression(init)) {
        const className = init.getExpression().getText();
        const memberName = init.getName();
        // Look up the static property in the project's source files
        const resolved = _staticStringCache.get(`${className}.${memberName}`);
        if (resolved !== undefined)
            return resolved;
        // Fallback: use the member name (Api.placeBid → 'placeBid')
        return memberName;
    }
    return null;
}
/// Pre-scan all classes for static readonly string properties to build a lookup cache.
/// Called once at the start of extraction to avoid repeated file traversals.
const _staticStringCache = new Map();
function buildStaticStringCache(project, rootPath) {
    _staticStringCache.clear();
    for (const sourceFile of project.getSourceFiles()) {
        const absPath = sourceFile.getFilePath();
        const filePath = path.relative(rootPath, absPath).replace(/\\/g, '/');
        if (filePath.includes('node_modules') || filePath.endsWith('.d.ts'))
            continue;
        for (const cls of sourceFile.getClasses()) {
            const className = cls.getName();
            if (!className)
                continue;
            for (const prop of cls.getStaticProperties()) {
                if (!ts_morph_1.Node.isPropertyDeclaration(prop))
                    continue;
                const init = prop.getInitializer();
                if (init && (ts_morph_1.Node.isStringLiteral(init) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(init))) {
                    _staticStringCache.set(`${className}.${prop.getName()}`, init.getLiteralValue());
                }
            }
        }
    }
}
function isFrameworkType(typeName) {
    return FRAMEWORK_TYPE_PREFIXES.some(prefix => typeName.startsWith(prefix));
}
const SKIP_DIRS = new Set(['node_modules', 'dist', 'cypress', 'e2e', '.git', '.angular']);
function findTsFiles(dir) {
    const results = [];
    const walk = (d) => {
        for (const entry of fs.readdirSync(d, { withFileTypes: true })) {
            if (entry.isDirectory()) {
                if (!SKIP_DIRS.has(entry.name))
                    walk(path.join(d, entry.name));
            }
            else if (entry.name.endsWith('.ts') &&
                !entry.name.endsWith('.d.ts') &&
                !entry.name.endsWith('.spec.ts')) {
                results.push(path.join(d, entry.name));
            }
        }
    };
    walk(dir);
    return results;
}
