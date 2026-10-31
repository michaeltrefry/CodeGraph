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
exports.analyzeRoutes = analyzeRoutes;
const ts_morph_1 = require("ts-morph");
const path = __importStar(require("path"));
const boundedTextReader_1 = require("./boundedTextReader");
/** Browser routes are distinct from backend endpoints. Resolve modules through the admitted model,
 * parse configuration as source, and give every synthetic fact a stable source slice owner. */
function analyzeRoutes(model, nodes, edges, scope, diagnostic = model.diagnostic, reader = new boundedTextReader_1.BoundedTextReader(model.root, scope ? file => scope.includesFile(file) : undefined, {}, diagnostic)) {
    return new RouteAnalysis(model, nodes, edges, scope, diagnostic, reader).run();
}
class RouteAnalysis {
    constructor(model, nodes, edges, scope, diagnostic, reader) {
        this.model = model;
        this.nodes = nodes;
        this.edges = edges;
        this.scope = scope;
        this.diagnostic = diagnostic;
        this.reader = reader;
        this.complete = true;
        this.emitted = new Map();
        this.edgeKeys = new Set();
        this.jsxCache = new Map();
        this.appRoots = new Map();
        this.markerDirectories = new Set();
        this.parsedConfigs = new ts_morph_1.Project({ useInMemoryFileSystem: true });
        this.inputs = scope ? scope.absoluteFiles().map(file => path.relative(model.root, file).replace(/\\/g, '/')) : model.files.map(file => model.relative(file));
        if (!scope) {
            const knownInputs = new Set(this.inputs);
            const visitedDirectories = new Set();
            for (const file of model.files) {
                model.work.charge(model.relative(file).length + 1);
                let directory = path.posix.dirname(model.relative(file));
                while (!visitedDirectories.has(directory)) {
                    model.work.charge(directory.length + 1);
                    visitedDirectories.add(directory);
                    for (const name of ['package.json', 'angular.json', 'tsconfig.json', 'next.config.ts', 'next.config.js', 'next.config.mjs', 'next.config.cjs']) {
                        model.work.charge(directory.length + name.length + 2);
                        const input = directory === '.' ? name : `${directory}/${name}`;
                        if (!knownInputs.has(input) && reader.exists(path.join(model.root, input))) {
                            knownInputs.add(input);
                            this.inputs.push(input);
                        }
                    }
                    if (directory === '.')
                        break;
                    directory = path.posix.dirname(directory);
                }
            }
        }
        for (const input of this.inputs) {
            model.work.charge(input.length + 1);
            if (/^(package\.json|angular\.json|tsconfig[^/]*\.json|next\.config\.[^/]+)$/.test(path.posix.basename(input)))
                this.markerDirectories.add(path.posix.dirname(input));
        }
        for (const node of nodes)
            if (node.properties.route_kind === 'ui')
                this.emitted.set(node.qualifiedName, node);
    }
    run() { this.angular(); this.react(); this.next(); return { complete: this.complete && this.reader.complete }; }
    relative(node) { return this.model.relative(node); }
    report(node, reason, incomplete = false) { if (incomplete)
        this.complete = false; this.diagnostic(`routeUnresolved: ${this.relative(node)}:${node.getStartLineNumber()} ${reason}`); }
    appRoot(file) {
        this.model.work.charge(file.length + 1);
        const cached = this.appRoots.get(file);
        if (cached !== undefined)
            return cached;
        let directory = path.posix.dirname(file);
        while (directory !== '.') {
            this.model.work.charge(directory.length + 1);
            if (this.markerDirectories.has(directory)) {
                this.appRoots.set(file, directory);
                return directory;
            }
            directory = path.posix.dirname(directory);
        }
        this.appRoots.set(file, '.');
        return '.';
    }
    read(file) {
        const absolute = path.join(this.model.root, file);
        if (this.scope && !this.scope.includesFile(absolute))
            return undefined;
        const source = this.model.byPath.get(file);
        if (source)
            return source.getFullText();
        return this.reader.read(absolute, true);
    }
    value(node, seen = new Set()) {
        this.model.work.charge(seen.size + 1);
        if (!node || seen.has(node) || seen.size > 80)
            return undefined;
        seen.add(node);
        if (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isSatisfiesExpression(node) || ts_morph_1.Node.isTypeAssertion(node))
            return this.value(node.getExpression(), seen);
        if (ts_morph_1.Node.isVariableDeclaration(node) || ts_morph_1.Node.isPropertyAssignment(node) || ts_morph_1.Node.isPropertyDeclaration(node))
            return this.value(node.getInitializer(), seen);
        if (ts_morph_1.Node.isIdentifier(node) || ts_morph_1.Node.isPropertyAccessExpression(node)) {
            const resolved = this.model.resolve(node);
            return resolved && resolved !== node && this.model.byPath.has(this.relative(resolved)) ? this.value(resolved, seen) : undefined;
        }
        return node;
    }
    text(node) {
        const value = this.value(node);
        if (!value || !(ts_morph_1.Node.isStringLiteral(value) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(value)))
            return undefined;
        this.model.work.charge(value.getFullWidth() + 1);
        return value.getLiteralText();
    }
    prop(node, name, seen = new Set()) {
        this.model.work.charge(seen.size + name.length + 1);
        const object = this.value(node);
        if (!object || seen.has(object) || !ts_morph_1.Node.isObjectLiteralExpression(object))
            return undefined;
        seen.add(object);
        let found;
        this.model.work.charge(object.compilerNode.properties.length + 1);
        for (const prop of object.getProperties()) {
            this.model.work.charge(prop.getFullWidth() + 1);
            if (ts_morph_1.Node.isSpreadAssignment(prop)) {
                const candidate = this.prop(prop.getExpression(), name, this.history(seen));
                if (candidate)
                    found = candidate;
            }
            if (ts_morph_1.Node.isPropertyAssignment(prop) && prop.getName().replace(/^['"]|['"]$/g, '') === name)
                found = prop.getInitializer();
            if (ts_morph_1.Node.isShorthandPropertyAssignment(prop) && prop.getName() === name)
                found = prop.getNameNode();
        }
        return found;
    }
    array(node, seen = new Set(), strict = false) {
        this.model.work.charge(seen.size + 1);
        const value = this.value(node);
        if (!value || seen.has(value) || seen.size > 80 || !ts_morph_1.Node.isArrayLiteralExpression(value)) {
            if (strict && node)
                this.report(node, 'route array/spread is dynamic or cyclic', true);
            return [];
        }
        seen.add(value);
        this.model.work.charge(value.compilerNode.elements.length * 8 + 16);
        const result = [];
        for (const item of value.getElements()) {
            this.model.work.charge(8);
            if (ts_morph_1.Node.isSpreadElement(item)) {
                const expanded = this.array(item.getExpression(), this.history(seen), strict);
                // Charge before copying an expanded shared/spread array, not after allocating its output.
                this.model.work.charge(expanded.length * 8 + 16);
                for (const child of expanded)
                    result.push(child);
            }
            else
                result.push(item);
        }
        return result;
    }
    history(seen) { this.model.work.charge(seen.size + 1); return new Set(seen); }
    layouts(ancestors, handlers) {
        this.model.work.charge(ancestors.length + handlers.length + 1);
        return [...ancestors, ...handlers];
    }
    joinPath(prefix, segment) {
        this.model.work.charge((prefix.length + segment.length) * 2 + 8);
        return joinPath(prefix, segment);
    }
    imported(node, packageName, name) {
        this.model.work.charge(name.length + 1);
        return this.model.importedApi(node, packageName) === name;
    }
    edge(source, target, type, owner, extra = {}) {
        this.model.work.charge();
        if (source === target)
            return;
        // Traversal may discover an edge while visiting a descendant page or route literal. Persist
        // outgoing facts with their actual source slice so signature changes replace that source's
        // edges, including shared layout chains whose endpoints both outlive the visited page.
        const declaration = this.model.byQN.get(source);
        const sourceFile = declaration ? this.relative(declaration) : this.emitted.get(source)?.filePath ?? this.relative(owner);
        // Reserve worst-case JSON escaping before allocating the deduplication key.
        this.model.work.charge((source.length + target.length + sourceFile.length) * 6 + type.length + 128);
        this.properties(extra);
        const properties = { extraction_source_file: sourceFile, route_kind: 'ui', ...extra };
        const key = JSON.stringify([source, target, type, properties]);
        if (this.edgeKeys.has(key))
            return;
        this.edgeKeys.add(key);
        this.edges.push(this.model.work.createEdge(() => ({ sourceQN: source, targetQN: target, type, properties })));
    }
    properties(extra) {
        for (const key in extra) {
            const value = extra[key];
            this.model.work.charge(key.length + (typeof value === 'string' ? value.length * 6 : 1) + 8);
        }
    }
    route(owner, framework, fullPath, extra = {}, appRoot = this.appRoot(this.relative(owner))) {
        this.model.work.charge(this.model.repository.length + appRoot.length + framework.length + fullPath.length * 2 + this.relative(owner).length + 192);
        this.properties(extra);
        const qualifiedName = `ui-route:${this.model.repository}:${appRoot}:${framework}:${fullPath}`;
        if (!this.emitted.has(qualifiedName)) {
            const sourceOwner = this.model.owner(owner);
            this.model.work.charge(sourceOwner.length * 6 + 1);
            const node = this.model.work.createNode(() => ({ label: 'Route', name: fullPath, qualifiedName, filePath: this.relative(owner),
                startLine: owner.getStartLineNumber(), endLine: owner.getEndLineNumber(), properties: {
                    route_kind: 'ui', framework, full_path: fullPath, app_root: appRoot,
                    extraction_source_file: this.relative(owner), source_owner: sourceOwner, ...extra
                } }));
            this.nodes.push(node);
            this.emitted.set(qualifiedName, node);
        }
        else
            Object.assign(this.emitted.get(qualifiedName).properties, extra);
        return qualifiedName;
    }
    angularRegistration(call) {
        if (!ts_morph_1.Node.isCallExpression(call))
            return false;
        const expression = call.getExpression();
        return this.imported(expression, '@angular/router', 'provideRouter') ||
            (ts_morph_1.Node.isPropertyAccessExpression(expression) && ['forRoot', 'forChild'].includes(expression.getName()) && this.imported(expression.getExpression(), '@angular/router', 'RouterModule'));
    }
    angular() {
        const registrations = this.model.files.flatMap(file => {
            this.model.work.charge(file.getFullWidth() + 1);
            return file.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression).filter(call => this.angularRegistration(call));
        });
        const consumed = new Set();
        const rootsInModule = (module, seen = new Set()) => {
            this.model.work.charge(seen.size + 1);
            if (!module || seen.has(module) || seen.size > 60)
                return [];
            seen.add(module);
            const value = this.value(module);
            if (value && ts_morph_1.Node.isArrayLiteralExpression(value))
                return [value];
            if (!ts_morph_1.Node.isClassDeclaration(module))
                return [];
            const roots = [];
            for (const decorator of module.getDecorators()) {
                const call = decorator.getCallExpression();
                if (!call || !this.imported(call.getExpression(), '@angular/core', 'NgModule'))
                    continue;
                const metadata = call.getArguments()[0];
                if (!metadata)
                    continue;
                for (const imp of this.array(this.prop(metadata, 'imports'))) {
                    if (ts_morph_1.Node.isCallExpression(imp) && this.angularRegistration(imp)) {
                        const root = this.value(imp.getArguments()[0]);
                        if (root)
                            roots.push(root);
                    }
                    else {
                        const nested = rootsInModule(this.model.resolve(imp), this.history(seen));
                        this.model.work.charge(nested.length + 1);
                        for (const root of nested)
                            roots.push(root);
                    }
                }
            }
            return roots;
        };
        const lazy = (node) => {
            const literal = this.text(node);
            if (literal !== undefined) {
                const [specifier, name = 'default'] = literal.split('#');
                const file = this.model.module(node, specifier);
                const target = file ? this.model.exported(file, name) : undefined;
                if (!target)
                    this.report(node, `unresolved legacy lazy module ${literal}`, true);
                return { target, arrays: rootsInModule(target) };
            }
            const loader = this.value(node) ?? node;
            this.model.work.charge(loader.getFullWidth() + 1);
            const imports = [loader, ...loader.getDescendants()].filter(ts_morph_1.Node.isCallExpression).filter(call => call.getExpression().getKind() === ts_morph_1.SyntaxKind.ImportKeyword);
            const load = imports.length === 1 ? imports[0] : undefined;
            const specifier = load ? this.text(load.getArguments()[0]) : undefined;
            const file = specifier && load ? this.model.module(load, specifier) : undefined;
            let exportName = 'default';
            const then = loader.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression).find(call => ts_morph_1.Node.isPropertyAccessExpression(call.getExpression()) && call.getExpression().getText().endsWith('.then'));
            // Both .then(m => m.Named) and async () => (await import(...)).Named are static.
            const callback = then?.getArguments()[0] ?? (ts_morph_1.Node.isArrowFunction(loader) || ts_morph_1.Node.isFunctionExpression(loader) ? loader : undefined);
            if (callback && (ts_morph_1.Node.isArrowFunction(callback) || ts_morph_1.Node.isFunctionExpression(callback))) {
                const body = callback.getBody();
                let returned = ts_morph_1.Node.isBlock(body) ? body.getDescendantsOfKind(ts_morph_1.SyntaxKind.ReturnStatement)[0]?.getExpression() : body;
                while (returned && (ts_morph_1.Node.isParenthesizedExpression(returned) || ts_morph_1.Node.isAwaitExpression(returned)))
                    returned = returned.getExpression();
                if (returned && ts_morph_1.Node.isPropertyAccessExpression(returned))
                    exportName = returned.getName();
                else if (returned && ts_morph_1.Node.isIdentifier(returned)) {
                    const parameter = callback.getParameters()[0]?.getNameNode();
                    if (parameter && ts_morph_1.Node.isObjectBindingPattern(parameter))
                        exportName = parameter.getElements().find(item => item.getName() === returned.getText())?.getPropertyNameNode()?.getText() ?? returned.getText();
                }
                else if (returned && ts_morph_1.Node.isElementAccessExpression(returned))
                    exportName = this.text(returned.getArgumentExpression()) ?? 'default';
            }
            const target = file ? this.model.exported(file, exportName) ?? (exportName === 'default' ? this.value(file.getExportAssignments()[0]?.getExpression()) : undefined) : undefined;
            if (!target)
                this.report(node, `unresolved lazy export ${specifier ?? '(dynamic)'}#${exportName}`, node.getParentIfKind(ts_morph_1.SyntaxKind.PropertyAssignment)?.getName() === 'loadChildren');
            return { target, arrays: rootsInModule(target) };
        };
        const discover = (array, seen = new Set()) => {
            this.model.work.charge(seen.size + 1);
            const value = this.value(array);
            if (!value || seen.has(value) || seen.size > 80)
                return;
            seen.add(value);
            for (const item of this.array(value, new Set(), true)) {
                const child = this.value(this.prop(item, 'children'));
                if (child) {
                    consumed.add(child);
                    discover(child, this.history(seen));
                }
                const load = this.prop(item, 'loadChildren');
                if (load)
                    for (const nested of lazy(load).arrays) {
                        consumed.add(nested);
                        discover(nested, this.history(seen));
                    }
            }
        };
        for (const registration of registrations) {
            const array = registration.getArguments()[0];
            if (array)
                discover(array);
        }
        const walk = (array, prefix, parent, app, seen = new Set(), layouts = []) => {
            this.model.work.charge(seen.size + 1);
            const value = this.value(array);
            if (!value || seen.has(value) || seen.size > 80) {
                this.report(array, 'Angular route cycle or traversal limit', true);
                return;
            }
            if (!ts_morph_1.Node.isArrayLiteralExpression(value)) {
                this.report(array, 'Angular route array is not statically enumerable', true);
                return;
            }
            seen.add(value);
            for (const raw of this.array(value, new Set(), true)) {
                const item = this.value(raw);
                if (!item || !ts_morph_1.Node.isObjectLiteralExpression(item))
                    continue;
                const pathNode = this.prop(item, 'path');
                const segment = pathNode ? this.text(pathNode) : '';
                if (segment === undefined) {
                    this.report(item, 'dynamic Angular route path', true);
                    continue;
                }
                const fullPath = this.joinPath(prefix, segment);
                const redirectNode = this.prop(item, 'redirectTo');
                const route = this.route(item, 'angular', fullPath, { ...(redirectNode ? { redirect_to: this.text(redirectNode) ?? '(dynamic)' } : {}), ...(segment === '' ? { empty_path: true } : {}) }, app);
                if (parent)
                    this.edge(parent, route, 'RENDERS', item);
                const component = this.model.target(this.prop(item, 'component'));
                const handlers = component ? [component] : [];
                const lazyChildren = [];
                for (const key of ['loadComponent', 'loadChildren']) {
                    const load = this.prop(item, key);
                    if (!load)
                        continue;
                    const resolved = lazy(load);
                    const target = resolved.target && this.model.declarations.get(resolved.target);
                    if (target)
                        this.edge(route, target, 'HANDLES', item, { lazy: true });
                    if (key === 'loadComponent' && target)
                        handlers.push(target);
                    this.model.work.charge(resolved.arrays.length + 1);
                    for (const child of resolved.arrays)
                        lazyChildren.push(child);
                }
                const children = this.prop(item, 'children');
                for (const handler of handlers) {
                    if (handler === component)
                        this.edge(route, handler, 'HANDLES', item);
                    if (children || lazyChildren.length)
                        this.edge(route, handler, 'RENDERS', item, { layout: true });
                    for (const layout of layouts)
                        this.edge(layout, handler, 'RENDERS', item, { angular_route_outlet: true });
                }
                const ancestors = this.layouts(layouts, handlers);
                if (children)
                    walk(children, fullPath, route, app, this.history(seen), ancestors);
                for (const child of lazyChildren)
                    walk(child, fullPath, route, app, this.history(seen), ancestors);
            }
        };
        for (const registration of registrations) {
            const array = this.value(registration.getArguments()[0]);
            const expression = registration.getExpression();
            const root = this.imported(expression, '@angular/router', 'provideRouter') || ts_morph_1.Node.isPropertyAccessExpression(expression) && expression.getName() === 'forRoot';
            if (array && (root || !consumed.has(array)))
                walk(array, '/', undefined, this.appRoot(this.relative(registration)));
            else if (!array)
                this.report(registration, 'Angular route registration is not statically enumerable', true);
        }
    }
    jsxName(node) {
        return ts_morph_1.Node.isJsxElement(node) ? node.getOpeningElement().getTagNameNode() : ts_morph_1.Node.isJsxSelfClosingElement(node) || ts_morph_1.Node.isJsxOpeningElement(node) ? node.getTagNameNode() : undefined;
    }
    routerTag(node, name) {
        const tag = this.jsxName(node);
        return !!tag && ['react-router', 'react-router-dom'].some(pkg => this.imported(tag, pkg, name));
    }
    jsxProp(node, name) {
        const opening = ts_morph_1.Node.isJsxElement(node) ? node.getOpeningElement() : node;
        if (!ts_morph_1.Node.isJsxOpeningElement(opening) && !ts_morph_1.Node.isJsxSelfClosingElement(opening))
            return undefined;
        const attr = opening.getAttribute(name);
        if (!attr || !ts_morph_1.Node.isJsxAttribute(attr))
            return undefined;
        const init = attr.getInitializer();
        return init && ts_morph_1.Node.isJsxExpression(init) ? init.getExpression() : init;
    }
    jsxFacts(expression) {
        const value = this.value(expression);
        if (!value)
            return { targets: [] };
        const cached = this.jsxCache.get(value);
        if (cached)
            return cached;
        const targets = new Set();
        let navigate;
        this.model.work.charge(8);
        const pending = [value];
        while (pending.length) {
            this.model.work.charge();
            const child = pending.pop();
            if (ts_morph_1.Node.isJsxElement(child) || ts_morph_1.Node.isJsxSelfClosingElement(child)) {
                const target = this.model.target(this.jsxName(child));
                if (target) {
                    this.model.work.charge(target.length + 1);
                    targets.add(target);
                }
                if (!navigate && this.routerTag(child, 'Navigate'))
                    navigate = child;
            }
            // Preserve source order, charging each reference before materializing/copying children.
            const children = [];
            child.forEachChild(item => { this.model.work.charge(8); children.push(item); });
            for (let index = children.length - 1; index >= 0; index--) {
                this.model.work.charge(8);
                pending.push(children[index]);
            }
        }
        this.model.work.charge(targets.size + 1);
        const result = { targets: [...targets], navigate };
        this.jsxCache.set(value, result);
        return result;
    }
    renderTargets(expression) {
        const targets = this.jsxFacts(expression).targets;
        this.model.work.charge(targets.length + 1);
        return [...targets];
    }
    react() {
        const routerApi = (node, name) => ['react-router', 'react-router-dom'].some(pkg => this.imported(node, pkg, name));
        for (const file of this.model.files) {
            this.model.work.charge(file.getFullWidth() + 1);
            const tags = file.getDescendants().filter(node => (ts_morph_1.Node.isJsxElement(node) || ts_morph_1.Node.isJsxSelfClosingElement(node)) && this.routerTag(node, 'Route'));
            const walk = (node, prefix, parent, layouts = [], mount = prefix) => {
                this.model.work.charge(layouts.length + 1);
                const segmentNode = this.jsxProp(node, 'path');
                const segment = segmentNode ? this.text(segmentNode) : '';
                if (segment === undefined) {
                    this.report(node, 'dynamic React Router path', true);
                    return;
                }
                const fullPath = segment.startsWith('/') ? this.joinPath(mount, segment.slice(1)) : this.joinPath(prefix, segment);
                const element = this.jsxProp(node, 'element');
                const navigate = this.jsxFacts(element).navigate;
                const targets = this.renderTargets(element);
                const component = this.model.target(this.jsxProp(node, 'Component'));
                if (component)
                    targets.push(component);
                const opening = ts_morph_1.Node.isJsxElement(node) ? node.getOpeningElement() : node;
                const index = (ts_morph_1.Node.isJsxOpeningElement(opening) || ts_morph_1.Node.isJsxSelfClosingElement(opening)) && !!opening.getAttribute('index');
                const route = this.route(node, 'react-router', fullPath, { ...(index ? { index: true } : {}), ...(navigate ? { redirect_to: this.text(this.jsxProp(navigate, 'to')) ?? '(dynamic)' } : {}) });
                if (parent)
                    this.edge(parent, route, 'RENDERS', node);
                for (const target of targets) {
                    this.edge(route, target, 'HANDLES', node);
                    for (const layout of layouts)
                        this.edge(layout, target, 'RENDERS', node);
                }
                if (ts_morph_1.Node.isJsxElement(node)) {
                    this.model.work.charge(node.getFullWidth() + 1);
                    const nested = node.getDescendants().filter(item => (ts_morph_1.Node.isJsxElement(item) || ts_morph_1.Node.isJsxSelfClosingElement(item)) && this.routerTag(item, 'Route') && item.getAncestors().find(ancestor => this.routerTag(ancestor, 'Route')) === node);
                    for (const child of nested)
                        walk(child, fullPath, route, this.layouts(layouts, targets), mount);
                }
            };
            for (const tag of tags) {
                if (tag.getAncestors().some(ancestor => this.routerTag(ancestor, 'Route')))
                    continue;
                const router = tag.getAncestors().find(ancestor => ['BrowserRouter', 'HashRouter', 'MemoryRouter'].some(name => this.routerTag(ancestor, name)));
                const routerCall = tag.getAncestors().find(ts_morph_1.Node.isCallExpression);
                const factory = routerCall?.getAncestors().find(node => ts_morph_1.Node.isCallExpression(node) && ['createBrowserRouter', 'createHashRouter', 'createMemoryRouter'].some(name => routerApi(node.getExpression(), name)));
                const options = factory && ts_morph_1.Node.isCallExpression(factory) ? factory.getArguments()[1] : undefined;
                const baseNode = router ? this.jsxProp(router, 'basename') : options && this.prop(options, 'basename');
                const base = baseNode ? this.text(baseNode) : '/';
                if (base === undefined)
                    this.report(tag, 'dynamic React Router basename', true);
                else
                    walk(tag, base);
            }
            this.model.work.charge(file.getFullWidth() + 1);
            for (const call of file.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
                if (!['createBrowserRouter', 'createHashRouter', 'createMemoryRouter', 'useRoutes'].some(name => routerApi(call.getExpression(), name)))
                    continue;
                const argument = call.getArguments()[0];
                // JSX declarations are composed by the same route walker above, including their factory basename.
                if (argument && ts_morph_1.Node.isCallExpression(argument) && routerApi(argument.getExpression(), 'createRoutesFromElements'))
                    continue;
                const baseNode = call.getArguments()[1] && this.prop(call.getArguments()[1], 'basename');
                const base = baseNode ? this.text(baseNode) : '/';
                if (base === undefined) {
                    this.report(call, 'dynamic React Router basename', true);
                    continue;
                }
                const walkObject = (array, prefix, parent, layouts = [], seen = new Set()) => {
                    this.model.work.charge(seen.size + 1);
                    if (!array)
                        return;
                    const value = this.value(array);
                    if (!value || seen.has(value) || seen.size > 80) {
                        this.report(array, 'React Router object array is dynamic or cyclic', true);
                        return;
                    }
                    seen.add(value);
                    for (const item of this.array(value, new Set(), true)) {
                        const pathNode = this.prop(item, 'path');
                        const segment = pathNode ? this.text(pathNode) : '';
                        if (segment === undefined) {
                            this.report(item, 'dynamic React Router object path', true);
                            continue;
                        }
                        const fullPath = segment.startsWith('/') ? this.joinPath(base, segment.slice(1)) : this.joinPath(prefix, segment);
                        const element = this.prop(item, 'element');
                        const targets = this.renderTargets(element);
                        const target = this.model.target(this.prop(item, 'Component'));
                        if (target)
                            targets.push(target);
                        const navigate = this.jsxFacts(element).navigate;
                        const route = this.route(item, 'react-router', fullPath, { ...(this.prop(item, 'index')?.getText() === 'true' ? { index: true } : {}), ...(navigate ? { redirect_to: this.text(this.jsxProp(navigate, 'to')) ?? '(dynamic)' } : {}) });
                        if (parent)
                            this.edge(parent, route, 'RENDERS', item);
                        for (const target of targets) {
                            this.edge(route, target, 'HANDLES', item);
                            for (const layout of layouts)
                                this.edge(layout, target, 'RENDERS', item);
                        }
                        walkObject(this.prop(item, 'children'), fullPath, route, this.layouts(layouts, targets), this.history(seen));
                    }
                };
                walkObject(call.getArguments()[0], base);
            }
        }
    }
    next() {
        const configs = this.inputs.filter(file => /(?:^|\/)next\.config\.(?:ts|js|mjs|cjs)$/.test(file));
        const configByRoot = new Map();
        const sources = [];
        let auxiliaryUnits = 0;
        for (const config of configs) {
            const text = this.read(config);
            if (text === undefined)
                continue;
            const admitted = this.model.byPath.get(config);
            const source = admitted ?? this.model.work.parse(text, () => this.parsedConfigs.createSourceFile(`/${config}`, text, { overwrite: true }), source => source.compilerNode, (node, visit) => ts_morph_1.ts.forEachChild(node, child => { visit(child); }));
            this.model.work.charge();
            sources.push({ config, source, admitted: !!admitted });
            if (!admitted)
                auxiliaryUnits += source.getFullWidth() + config.length + 1;
        }
        // Adding a file resets ts-morph's Program. Validate only after the retained set is final.
        this.model.work.charge(auxiliaryUnits + 1);
        const syntaxProgram = auxiliaryUnits ? this.parsedConfigs.getProgram().compilerObject : undefined;
        for (const { config, source, admitted } of sources) {
            if (!admitted) {
                this.model.work.charge(source.getFullWidth() + 1);
                const errors = syntaxProgram.getSyntacticDiagnostics(source.compilerNode);
                if (errors.length) {
                    this.complete = false;
                    configByRoot.set(path.posix.dirname(config), { base: undefined, file: config });
                    for (const error of errors.slice(0, 5)) {
                        const message = typeof error.messageText === 'string' ? error.messageText : error.messageText.messageText;
                        this.model.work.charge(config.length + message.length + 1);
                        this.diagnostic(`routeParse: ${config}: ${message}`);
                    }
                    continue;
                }
            }
            let expression = source.getExportAssignments()[0]?.getExpression();
            if (!expression)
                expression = source.getDescendantsOfKind(ts_morph_1.SyntaxKind.BinaryExpression).find(node => node.getLeft().getText() === 'module.exports')?.getRight();
            const variables = new Map();
            this.model.work.charge(source.compilerNode.statements.length + 1);
            for (const declaration of source.getVariableDeclarations()) {
                this.model.work.charge(declaration.getNameNode().getFullWidth() + 1);
                const name = declaration.getName();
                if (!variables.has(name))
                    variables.set(name, declaration);
            }
            const localValue = (initial) => {
                const seen = new Set();
                let node = initial;
                while (node) {
                    this.model.work.charge();
                    this.model.work.checkDepth(seen.size, 128);
                    if (seen.has(node))
                        return undefined;
                    seen.add(node);
                    if (ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isSatisfiesExpression(node) || ts_morph_1.Node.isParenthesizedExpression(node)) {
                        node = node.getExpression();
                        continue;
                    }
                    if (ts_morph_1.Node.isIdentifier(node)) {
                        this.model.work.charge(node.getFullWidth() + 1);
                        const declaration = variables.get(node.getText());
                        node = declaration && ts_morph_1.Node.isVariableDeclaration(declaration) ? declaration.getInitializer() : undefined;
                        continue;
                    }
                    return node;
                }
                return undefined;
            };
            const object = localValue(expression);
            let base;
            if (object && ts_morph_1.Node.isObjectLiteralExpression(object)) {
                const property = object.getProperty('basePath');
                const value = property && ts_morph_1.Node.isPropertyAssignment(property) ? localValue(property.getInitializer()) : undefined;
                if (!property && !object.getProperties().some(ts_morph_1.Node.isSpreadAssignment))
                    base = '/';
                else if (value && (ts_morph_1.Node.isStringLiteral(value) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(value)))
                    base = value.getLiteralText();
            }
            if (base === undefined) {
                this.complete = false;
                this.diagnostic(`routeUnresolved: ${config} dynamic/unsupported Next.js configuration; basePath unknown`);
            }
            configByRoot.set(path.posix.dirname(config), { base, file: config });
        }
        const nextRoots = new Set(configByRoot.keys());
        for (const input of this.inputs.filter(file => path.posix.basename(file) === 'package.json')) {
            const text = this.read(input);
            if (!text)
                continue;
            this.model.work.charge(text.length + 1);
            try {
                const pkg = JSON.parse(text);
                if (pkg.dependencies?.next || pkg.devDependencies?.next)
                    nextRoots.add(path.posix.dirname(input));
            }
            catch { /* Workspace discovery owns malformed manifests. */ }
        }
        for (const file of this.model.files)
            if (this.model.importDeclarations(file).some(imp => /^next(?:\/|$)/.test(imp.getModuleSpecifierValue())))
                nextRoots.add(this.appRoot(this.relative(file)));
        const rootAtDirectory = new Map();
        const nearestRoot = (relative) => {
            this.model.work.charge(relative.length + 1);
            let directory = path.posix.dirname(relative);
            const trail = [];
            let found;
            while (true) {
                this.model.work.charge(directory.length + 1);
                if (rootAtDirectory.has(directory)) {
                    found = rootAtDirectory.get(directory);
                    break;
                }
                trail.push(directory);
                if (nextRoots.has(directory)) {
                    found = directory;
                    break;
                }
                if (directory === '.')
                    break;
                directory = path.posix.dirname(directory);
            }
            for (const item of trail) {
                this.model.work.charge(item.length + 1);
                rootAtDirectory.set(item, found);
            }
            return found;
        };
        for (const file of this.model.files) {
            const relative = this.relative(file);
            if (!/(?:^|\/)page\.(?:jsx?|tsx?)$/.test(relative))
                continue;
            const appRoot = nearestRoot(relative);
            if (appRoot === undefined)
                continue;
            const within = appRoot === '.' ? relative : relative.slice(appRoot.length + 1);
            const match = /^(?:src\/)?app\/(.*)page\.(?:jsx?|tsx?)$/.exec(within);
            if (!match)
                continue;
            const configuration = configByRoot.get(appRoot);
            if (configuration && configuration.base === undefined)
                continue;
            this.model.work.charge(match[1].length + 1);
            const segments = match[1].split('/').filter(Boolean);
            const urlSegments = segments.filter(segment => !/^\(.*\)$/.test(segment) && !segment.startsWith('@'));
            this.model.work.charge(match[1].length + urlSegments.length + 1);
            const fullPath = this.joinPath(configuration?.base ?? '/', urlSegments.join('/'));
            const page = this.model.exported(file, 'default');
            const pageQN = page && this.model.declarations.get(page);
            if (!pageQN) {
                this.report(file, 'Next.js page default export unresolved');
                continue;
            }
            const route = this.route(file, 'nextjs', fullPath, { ...(configuration ? { configuration_file: configuration.file } : {}) }, appRoot);
            this.edge(route, pageQN, 'HANDLES', file);
            const appDirectory = relative.slice(0, relative.length - match[1].length - path.posix.basename(relative).length);
            let previous;
            for (let depth = 0; depth <= segments.length; depth++) {
                this.model.work.charge(appDirectory.length + match[1].length + depth + 1);
                const directory = `${appDirectory}${segments.slice(0, depth).join('/')}`.replace(/\/$/, '');
                this.model.work.charge((directory.length + 32) * 4);
                const layoutFile = ['tsx', 'ts', 'jsx', 'js'].map(extension => this.model.byPath.get(`${directory}/layout.${extension}`)).find(Boolean);
                const layout = layoutFile && this.model.exported(layoutFile, 'default');
                const layoutQN = layout && this.model.declarations.get(layout);
                if (!layoutQN)
                    continue;
                this.edge(route, layoutQN, 'RENDERS', file, { layout: true });
                if (previous)
                    this.edge(previous, layoutQN, 'RENDERS', file, { layout: true });
                previous = layoutQN;
            }
            if (previous)
                this.edge(previous, pageQN, 'RENDERS', file);
        }
    }
}
function joinPath(prefix, segment) {
    if (segment.startsWith('/'))
        return `/${segment.split('/').filter(Boolean).join('/')}`;
    return `/${[...prefix.split('/'), ...segment.split('/')].filter(Boolean).join('/')}`;
}
