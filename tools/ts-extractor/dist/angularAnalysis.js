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
exports.analyzeAngular = analyzeAngular;
const ts_morph_1 = require("ts-morph");
const boundedTextReader_1 = require("./boundedTextReader");
const path = __importStar(require("path"));
const semanticModel_1 = require("./semanticModel");
/** Derive Angular links exclusively from admitted declarations and imported framework APIs.
 * Provider tables remain scoped: overrides never leak into a sibling application's injector.
 */
function analyzeAngular(model, nodes, edges, scope, diagnostic = model.diagnostic, reader = new boundedTextReader_1.BoundedTextReader(model.root, scope ? file => scope.includesFile(file) : undefined, {}, diagnostic)) {
    let complete = true;
    const processedTemplates = new Set();
    const classes = model.files.flatMap(file => file.getClasses());
    const interfaces = model.files.flatMap(file => file.getInterfaces());
    const graphNodes = new Map(nodes.map(node => [node.qualifiedName, node]));
    const tokenQNs = new Map();
    const tokenIdentities = new Map();
    function qn(node) {
        const known = tokenQNs.get(node) ?? model.declarations.get(node);
        if (known)
            return known;
        if (ts_morph_1.Node.isPropertyDeclaration(node) || ts_morph_1.Node.isParameterDeclaration(node)) {
            const owner = classOf(node);
            return `${owner ? qn(owner) : model.fileQN(node)}.${node.getName()}`;
        }
        return model.fileQN(node);
    }
    // The semantic pass resolves declared types before Angular selects an injector binding.
    // Keep its edge identities so only superseded call evidence is retracted below.
    const callKey = (source, target) => JSON.stringify([source, target]);
    const provisionalCalls = new Map();
    for (const edge of edges)
        if (edge.type === 'CALLS' && !edge.properties?.call_kind) {
            const key = callKey(edge.sourceQN, edge.targetQN);
            const existing = provisionalCalls.get(key) ?? new Set();
            existing.add(edge);
            provisionalCalls.set(key, existing);
        }
    const redirectedCallSites = new Set();
    const redirectedCallKeys = new Set();
    const edgeKeys = new Set();
    function addEdge(sourceQN, targetQN, type, properties = {}) {
        const props = { framework: 'angular', ...properties };
        const key = `${type}|${sourceQN}|${targetQN}|${JSON.stringify(props)}`;
        if (!edgeKeys.has(key)) {
            edgeKeys.add(key);
            const direct = type === 'CALLS' ? edges.find(edge => edge.type === type && edge.sourceQN === sourceQN && edge.targetQN === targetQN && !edge.properties?.call_kind) : undefined;
            if (direct)
                direct.properties = { ...direct.properties, ...props };
            else
                edges.push({ sourceQN, targetQN, type, properties: props });
        }
    }
    function emit(node, qualifiedName, label, name, properties = {}) {
        const existing = graphNodes.get(qualifiedName);
        if (existing) {
            Object.assign(existing.properties, properties);
            return;
        }
        const value = { label, name, qualifiedName, filePath: model.relative(node), startLine: node.getStartLineNumber(), endLine: node.getEndLineNumber(), properties };
        nodes.push(value);
        graphNodes.set(qualifiedName, value);
    }
    function imported(node, packageName) {
        if (!node)
            return undefined;
        for (const imp of node.getSourceFile().getImportDeclarations()) {
            if (imp.getModuleSpecifierValue() !== packageName)
                continue;
            if (ts_morph_1.Node.isIdentifier(node)) {
                const item = imp.getNamedImports().find(item => (0, semanticModel_1.sameSymbol)(node, item.getAliasNode() ?? item.getNameNode()));
                if (item)
                    return item.getName();
            }
            if (ts_morph_1.Node.isPropertyAccessExpression(node) && (0, semanticModel_1.sameSymbol)(node.getExpression(), imp.getNamespaceImport()))
                return node.getName();
            if (ts_morph_1.Node.isQualifiedName(node) && (0, semanticModel_1.sameSymbol)(node.getLeft(), imp.getNamespaceImport()))
                return node.getRight().getText();
        }
        return undefined;
    }
    function decorator(node, name) {
        const dec = node.getDecorators().find(dec => imported(dec.getCallExpression()?.getExpression() ?? dec.getExpression(), '@angular/core') === name);
        const value = reference(dec?.getArguments()[0]);
        return value && ts_morph_1.Node.isObjectLiteralExpression(value) ? value : undefined;
    }
    function property(object, name) {
        const member = object?.getProperty(name);
        return member && ts_morph_1.Node.isPropertyAssignment(member) ? member.getInitializer() : undefined;
    }
    function classOf(node) { return ts_morph_1.Node.isClassDeclaration(node) ? node : node.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ClassDeclaration); }
    function reference(node, seen = new Set()) {
        if (!node || seen.has(node))
            return undefined;
        seen.add(node);
        if (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isNonNullExpression(node) || ts_morph_1.Node.isTypeAssertion(node))
            return reference(node.getExpression(), seen);
        if (ts_morph_1.Node.isPropertyAccessExpression(node) && node.getExpression().getKind() === ts_morph_1.SyntaxKind.ThisKeyword) {
            const owner = classOf(node);
            const field = owner ? classField(owner, node.getName()) : undefined;
            return field ? reference(field, seen) : undefined;
        }
        if (ts_morph_1.Node.isPropertyDeclaration(node) || ts_morph_1.Node.isParameterDeclaration(node) || ts_morph_1.Node.isPropertyAssignment(node)) {
            const initial = node.getInitializer();
            return initial ? reference(initial, seen) : node;
        }
        if (ts_morph_1.Node.isCallExpression(node) && imported(node.getExpression(), '@angular/core') === 'forwardRef') {
            const callback = node.getArguments()[0];
            if (callback && ts_morph_1.Node.isArrowFunction(callback)) {
                const body = callback.getBody();
                return reference(ts_morph_1.Node.isBlock(body) ? body.getStatements().find(ts_morph_1.Node.isReturnStatement)?.getExpression() : body, seen);
            }
        }
        const resolved = model.resolve(node);
        if (resolved && resolved !== node && (ts_morph_1.Node.isPropertyDeclaration(resolved) || ts_morph_1.Node.isPropertyAssignment(resolved) || ts_morph_1.Node.isVariableDeclaration(resolved)))
            return reference(resolved, seen);
        return resolved ?? node.getSymbol()?.getDeclarations().find(declaration => model.byPath.has(model.relative(declaration)) && (ts_morph_1.Node.isVariableDeclaration(declaration) || ts_morph_1.Node.isParameterDeclaration(declaration) || ts_morph_1.Node.isPropertyDeclaration(declaration)));
    }
    function tokenReference(node) {
        const resolved = reference(node);
        if (resolved)
            return resolved;
        if (!node)
            return undefined;
        let identity;
        if (ts_morph_1.Node.isStringLiteral(node))
            identity = `angular-token:literal:${node.getLiteralText()}`;
        for (const imp of node.getSourceFile().getImportDeclarations()) {
            const resolution = model.imports.find(item => item.filePath === model.relative(node) && item.importedNamespace === imp.getModuleSpecifierValue());
            if (resolution?.classification !== 'external_npm_package')
                continue;
            const name = imported(node, imp.getModuleSpecifierValue());
            if (name)
                identity = `angular-token:${imp.getModuleSpecifierValue()}:${name}`;
        }
        if (!identity)
            return undefined;
        const previous = tokenIdentities.get(identity);
        if (previous)
            return previous;
        tokenIdentities.set(identity, node);
        tokenQNs.set(node, identity);
        return node;
    }
    function classField(cls, name, seen = new Set()) {
        if (seen.has(cls))
            return undefined;
        seen.add(cls);
        const field = cls.getProperty(name) ?? cls.getConstructors().flatMap(ctor => ctor.getParameters()).find(parameter => parameter.getName() === name);
        if (field)
            return field;
        const parent = reference(cls.getExtends()?.getExpression());
        return parent && ts_morph_1.Node.isClassDeclaration(parent) ? classField(parent, name, seen) : undefined;
    }
    function values(node, seen = new Set()) {
        if (!node || seen.has(node))
            return [];
        seen.add(node);
        const value = reference(node);
        if (value && value !== node && !ts_morph_1.Node.isClassDeclaration(value) && !ts_morph_1.Node.isInterfaceDeclaration(value))
            return values(value, seen);
        if (ts_morph_1.Node.isVariableDeclaration(node) || ts_morph_1.Node.isPropertyDeclaration(node))
            return values(node.getInitializer(), seen);
        if (ts_morph_1.Node.isArrayLiteralExpression(node))
            return node.getElements().flatMap(item => values(ts_morph_1.Node.isSpreadElement(item) ? item.getExpression() : item, new Set(seen)));
        if (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isNonNullExpression(node) || ts_morph_1.Node.isTypeAssertion(node))
            return values(node.getExpression(), seen);
        return value ? [value] : [node];
    }
    function string(node, seen = new Set()) {
        if (!node || seen.has(node))
            return undefined;
        seen.add(node);
        if (ts_morph_1.Node.isStringLiteral(node) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(node))
            return node.getLiteralText();
        const value = reference(node);
        if (value && value !== node && (ts_morph_1.Node.isVariableDeclaration(value) || ts_morph_1.Node.isPropertyDeclaration(value)))
            return string(value.getInitializer(), seen);
        return undefined;
    }
    function typeInterface(node) {
        if (!node)
            return undefined;
        const value = reference(node);
        if (value && ts_morph_1.Node.isInterfaceDeclaration(value))
            return value;
        if (ts_morph_1.Node.isTypeReference(node) && ['InjectionToken', 'ProviderToken', 'Type'].includes(imported(node.getTypeName(), '@angular/core') ?? ''))
            return typeInterface(node.getTypeArguments()[0]);
        if (ts_morph_1.Node.isUnionTypeNode(node)) {
            const candidates = new Set(node.getTypeNodes().map(typeInterface).filter((value) => !!value));
            return candidates.size === 1 ? [...candidates][0] : undefined;
        }
        return undefined;
    }
    function tokenInterface(token, seen = new Set()) {
        if (!token || seen.has(token))
            return undefined;
        seen.add(token);
        if (ts_morph_1.Node.isInterfaceDeclaration(token))
            return token;
        if (ts_morph_1.Node.isVariableDeclaration(token) || ts_morph_1.Node.isPropertyDeclaration(token) || ts_morph_1.Node.isParameterDeclaration(token)) {
            const typed = typeInterface(token.getTypeNode());
            if (typed)
                return typed;
            const initial = token.getInitializer();
            if (initial && ts_morph_1.Node.isNewExpression(initial) && imported(initial.getExpression(), '@angular/core') === 'InjectionToken')
                return typeInterface(initial.getTypeArguments()[0]);
            const resolved = reference(initial);
            if (resolved && resolved !== token)
                return tokenInterface(resolved, seen);
        }
        return undefined;
    }
    const angular = classes.flatMap(declaration => {
        for (const kind of ['Component', 'NgModule', 'Directive', 'Injectable']) {
            if (declaration.getDecorators().some(dec => imported(dec.getCallExpression()?.getExpression() ?? dec.getExpression(), '@angular/core') === kind))
                return [{ declaration, metadata: decorator(declaration, kind), kind }];
        }
        return [];
    });
    const components = angular.filter(value => value.kind === 'Component');
    const componentSet = new Set(components.map(component => component.declaration));
    for (const cls of classes) {
        const node = graphNodes.get(qn(cls));
        if (!node)
            continue;
        const angularClass = angular.find(item => item.declaration === cls);
        node.properties.is_service = angularClass?.kind === 'Injectable';
        node.properties.is_ng_module = angularClass?.kind === 'NgModule';
        if (componentSet.has(cls)) {
            node.label = 'Component';
            node.properties.framework = 'angular';
            const selector = string(property(angularClass?.metadata, 'selector'));
            const templateUrl = string(property(angularClass?.metadata, 'templateUrl'));
            if (selector !== undefined)
                node.properties.selector = selector;
            if (templateUrl !== undefined)
                node.properties.templateUrl = templateUrl;
        }
        else if (node.properties.framework === 'angular' && node.label === 'Component') {
            node.label = 'Class';
            delete node.properties.framework;
            delete node.properties.selector;
            delete node.properties.templateUrl;
        }
    }
    const interfaceMembers = new Map();
    function members(iface, seen = new Set()) {
        if (interfaceMembers.has(iface))
            return interfaceMembers.get(iface);
        if (seen.has(iface))
            return new Map();
        seen.add(iface);
        const result = new Map();
        for (const parent of iface.getExtends()) {
            const value = reference(parent.getExpression());
            if (value && ts_morph_1.Node.isInterfaceDeclaration(value))
                for (const [name, method] of members(value, seen))
                    result.set(name, method);
        }
        for (const member of iface.getMembers()) {
            if (ts_morph_1.Node.isMethodSignature(member) || (ts_morph_1.Node.isPropertySignature(member) && member.getType().getCallSignatures().length > 0)) {
                result.set(member.getName(), member);
                const methodQN = `${qn(iface)}.${member.getName()}`;
                emit(member, methodQN, 'Method', member.getName(), { callable: true, is_interface_member: true, is_function_property: ts_morph_1.Node.isPropertySignature(member) });
                addEdge(qn(iface), methodQN, 'DEFINES_METHOD');
            }
        }
        interfaceMembers.set(iface, result);
        return result;
    }
    for (const iface of interfaces)
        members(iface);
    const implementers = new Map();
    function contracts(cls, seen = new Set()) {
        if (seen.has(cls))
            return new Set();
        seen.add(cls);
        const result = new Set();
        const add = (iface) => {
            if (result.has(iface))
                return;
            result.add(iface);
            for (const parent of iface.getExtends()) {
                const value = reference(parent.getExpression());
                if (value && ts_morph_1.Node.isInterfaceDeclaration(value))
                    add(value);
            }
        };
        for (const impl of cls.getImplements()) {
            const value = reference(impl.getExpression());
            if (value && ts_morph_1.Node.isInterfaceDeclaration(value))
                add(value);
        }
        const base = reference(cls.getExtends()?.getExpression());
        if (base && ts_morph_1.Node.isClassDeclaration(base))
            for (const item of contracts(base, seen))
                add(item);
        return result;
    }
    function implementationMethod(cls, name, seen = new Set()) {
        if (seen.has(cls))
            return undefined;
        seen.add(cls);
        const method = cls.getMethod(name);
        if (method)
            return qn(method);
        const prop = cls.getProperty(name)?.getInitializer();
        if (prop) {
            if (ts_morph_1.Node.isArrowFunction(prop) || ts_morph_1.Node.isFunctionExpression(prop))
                return model.callables.get(prop);
            const callable = reference(prop);
            if (callable && model.callables.has(callable))
                return model.callables.get(callable);
        }
        const base = reference(cls.getExtends()?.getExpression());
        return base && ts_morph_1.Node.isClassDeclaration(base) ? implementationMethod(base, name, seen) : undefined;
    }
    for (const cls of classes)
        for (const iface of contracts(cls)) {
            const existing = implementers.get(iface) ?? [];
            existing.push(cls);
            implementers.set(iface, existing);
            for (const [name, member] of members(iface)) {
                const target = implementationMethod(cls, name);
                const owner = member.getFirstAncestorByKind(ts_morph_1.SyntaxKind.InterfaceDeclaration);
                if (target)
                    addEdge(`${qn(owner)}.${name}`, target, 'CALLS', { call_kind: 'interface_dispatch', confidence_band: 'medium', candidate: true, interface_qn: qn(iface) });
            }
        }
    const scopes = new Map();
    function makeScope(owner) {
        let value = scopes.get(owner);
        if (!value) {
            value = { owner, qn: qn(owner), identity: qn(owner), bindings: new Map(), parents: [], uncertain: false };
            scopes.set(owner, value);
        }
        return value;
    }
    const moduleDeclarations = new Map();
    const moduleImports = new Map();
    const moduleExports = new Map();
    const scopeForClass = (cls) => makeScope(cls);
    for (const item of angular.filter(item => item.kind === 'NgModule' || item.kind === 'Component' || item.kind === 'Directive')) {
        const providerScope = makeScope(item.declaration);
        if (item.kind === 'NgModule')
            for (const value of values(property(item.metadata, 'declarations'))) {
                if (ts_morph_1.Node.isClassDeclaration(value)) {
                    const owners = moduleDeclarations.get(value) ?? [];
                    owners.push(item.declaration);
                    moduleDeclarations.set(value, owners);
                }
            }
        const importedValues = values(property(item.metadata, 'imports'));
        const imports = importedValues.flatMap(value => {
            if (ts_morph_1.Node.isClassDeclaration(value))
                return [value];
            if (ts_morph_1.Node.isObjectLiteralExpression(value)) {
                const module = reference(property(value, 'ngModule'));
                collectProviders(property(value, 'providers'), providerScope);
                return module && ts_morph_1.Node.isClassDeclaration(module) ? [module] : [];
            }
            return [];
        });
        moduleImports.set(item.declaration, imports);
        moduleExports.set(item.declaration, values(property(item.metadata, 'exports')).filter(ts_morph_1.Node.isClassDeclaration));
        for (const imported of imports)
            if (angular.some(item => item.declaration === imported && item.kind === 'NgModule'))
                providerScope.parents.push(makeScope(imported));
    }
    function collectProviders(node, providerScope, seen = new Set()) {
        if (!node || seen.has(node))
            return;
        seen.add(node);
        if (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isNonNullExpression(node) || ts_morph_1.Node.isTypeAssertion(node)) {
            collectProviders(node.getExpression(), providerScope, seen);
            return;
        }
        if (ts_morph_1.Node.isArrayLiteralExpression(node)) {
            for (const element of node.getElements())
                collectProviders(ts_morph_1.Node.isSpreadElement(element) ? element.getExpression() : element, providerScope, new Set(seen));
            return;
        }
        if (ts_morph_1.Node.isCallExpression(node)) {
            const api = imported(node.getExpression(), '@angular/core');
            if (api === 'makeEnvironmentProviders') {
                collectProviders(node.getArguments()[0], providerScope, seen);
                return;
            }
            if (api === 'importProvidersFrom') {
                for (const argument of node.getArguments())
                    for (const module of values(argument).filter(ts_morph_1.Node.isClassDeclaration))
                        providerScope.parents.push(makeScope(module));
                return;
            }
        }
        if (ts_morph_1.Node.isConditionalExpression(node)) {
            providerScope.uncertain = true;
            collectProviders(node.getWhenTrue(), providerScope, new Set(seen));
            collectProviders(node.getWhenFalse(), providerScope, new Set(seen));
            return;
        }
        const value = reference(node);
        if (value && ts_morph_1.Node.isVariableDeclaration(value)) {
            collectProviders(value.getInitializer(), providerScope, seen);
            return;
        }
        if (value && ts_morph_1.Node.isClassDeclaration(value)) {
            providerScope.bindings.set(value, [{ token: value, implementation: value, multi: false, scope: providerScope }]);
            return;
        }
        if (value && value !== node && (ts_morph_1.Node.isArrayLiteralExpression(value) || ts_morph_1.Node.isObjectLiteralExpression(value))) {
            collectProviders(value, providerScope, seen);
            return;
        }
        if (!ts_morph_1.Node.isObjectLiteralExpression(node)) {
            providerScope.uncertain = true;
            diagnostic(`angularProvider: unsupported provider in ${providerScope.qn}: ${node.getText().slice(0, 120)}`);
            return;
        }
        const token = tokenReference(property(node, 'provide'));
        const useClass = reference(property(node, 'useClass'));
        const alias = tokenReference(property(node, 'useExisting'));
        if (!token || (!alias && (!useClass || !ts_morph_1.Node.isClassDeclaration(useClass)))) {
            providerScope.uncertain = true;
            diagnostic(`angularProvider: unresolved provider in ${providerScope.qn}: ${node.getText().slice(0, 120)}`);
            return;
        }
        const multi = property(node, 'multi')?.getText() === 'true';
        const binding = { token, implementation: useClass && ts_morph_1.Node.isClassDeclaration(useClass) ? useClass : undefined, alias, multi, scope: providerScope };
        const previous = providerScope.bindings.get(token) ?? [];
        providerScope.bindings.set(token, multi || providerScope.uncertain ? [...previous, binding] : [binding]);
    }
    for (const item of angular) {
        const providerScope = makeScope(item.declaration);
        collectProviders(property(item.metadata, 'providers'), providerScope);
        collectProviders(property(item.metadata, 'viewProviders'), providerScope);
    }
    // Each top-level provider configuration gets its own injector table, even when two
    // bootstraps share a source file. REGISTERS remains owned by the source file slice.
    const configScopes = new Map();
    function configScope(owner) {
        let value = configScopes.get(owner);
        if (!value) {
            value = makeScope(owner);
            value.qn = model.fileQN(owner);
            configScopes.set(owner, value);
        }
        return value;
    }
    for (const file of model.files)
        for (const declaration of file.getVariableDeclarations()) {
            const initial = declaration.getInitializer();
            if (!initial || declaration.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ClassDeclaration))
                continue;
            if (ts_morph_1.Node.isObjectLiteralExpression(initial) && property(initial, 'providers'))
                collectProviders(property(initial, 'providers'), configScope(declaration));
            else if (ts_morph_1.Node.isArrayLiteralExpression(initial) && initial.getDescendantsOfKind(ts_morph_1.SyntaxKind.ObjectLiteralExpression).some(object => property(object, 'provide')))
                collectProviders(initial, configScope(declaration));
        }
    for (const file of model.files)
        for (const call of file.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
            if (imported(call.getExpression(), '@angular/platform-browser') !== 'bootstrapApplication')
                continue;
            const component = reference(call.getArguments()[0]);
            if (!component || !ts_morph_1.Node.isClassDeclaration(component))
                continue;
            const config = call.getArguments()[1];
            const resolved = reference(config);
            const declaration = config?.getSymbol()?.getDeclarations().find(ts_morph_1.Node.isVariableDeclaration) ?? resolved?.getFirstAncestorByKind(ts_morph_1.SyntaxKind.VariableDeclaration);
            const value = configScope(declaration ?? config ?? call);
            if (!declaration)
                value.identity = `${model.fileQN(call)}#bootstrap:${qn(component)}`;
            if (resolved && ts_morph_1.Node.isObjectLiteralExpression(resolved) && !declaration)
                collectProviders(property(resolved, 'providers'), value);
            else if (config && (!resolved || !ts_morph_1.Node.isObjectLiteralExpression(resolved))) {
                value.uncertain = true;
                diagnostic(`angularProvider: unsupported application config in ${model.relative(call)}`);
            }
            scopeForClass(component).parents.push(value);
        }
    const modules = angular.filter(item => item.kind === 'NgModule').map(item => item.declaration);
    function moduleReaches(root, target, seen = new Set()) {
        if (root === target)
            return true;
        if (seen.has(root))
            return false;
        seen.add(root);
        return (moduleImports.get(root) ?? []).some(child => modules.includes(child) && moduleReaches(child, target, seen));
    }
    const moduleRoots = modules.filter(module => !modules.some(other => other !== module && (moduleImports.get(other) ?? []).includes(module)));
    for (const [cls, owners] of moduleDeclarations) {
        const contexts = moduleRoots.filter(root => owners.some(owner => moduleReaches(root, owner)));
        for (const owner of contexts.length ? contexts : owners)
            scopeForClass(cls).parents.push(scopeForClass(owner));
    }
    const renderedParents = new Map();
    for (const providerScope of scopes.values())
        for (const bindings of providerScope.bindings.values())
            for (const binding of bindings) {
                if (binding.implementation && binding.implementation !== providerScope.owner)
                    scopeForClass(binding.implementation).parents.push(providerScope);
            }
    function lookup(token, providerScope, seen = new Set()) {
        if (seen.has(providerScope))
            return { bindings: [], certain: false };
        seen.add(providerScope);
        const local = providerScope.bindings.get(token);
        if (local)
            return { bindings: local, certain: !providerScope.uncertain && local.length === 1 && !local[0].multi };
        const parents = [...(renderedParents.get(providerScope) ?? providerScope.parents)];
        const branches = parents.map(parent => lookup(token, parent, new Set(seen))).filter(result => result.bindings.length);
        // NgModule imports are ordered in one injector: later imported providers win.
        if (ts_morph_1.Node.isClassDeclaration(providerScope.owner) && modules.includes(providerScope.owner) && branches.length)
            return branches[branches.length - 1];
        const bindings = [...new Set(branches.flatMap(result => result.bindings))];
        return { bindings, certain: !providerScope.uncertain && branches.length > 0 && branches.every(result => result.certain) && new Set(bindings.map(binding => binding.implementation ?? binding.alias)).size === 1 };
    }
    function aliasInjectors(binding, consumingScope) {
        if (!ts_morph_1.Node.isClassDeclaration(binding.scope.owner) || !modules.includes(binding.scope.owner))
            return [binding.scope];
        const declaringModule = binding.scope.owner;
        const contexts = new Set();
        const visited = new Set();
        const visit = (current) => {
            if (visited.has(current))
                return;
            visited.add(current);
            if (ts_morph_1.Node.isClassDeclaration(current.owner) && moduleRoots.includes(current.owner) && moduleReaches(current.owner, declaringModule))
                contexts.add(current);
            for (const parent of renderedParents.get(current) ?? current.parents)
                visit(parent);
        };
        visit(consumingScope);
        if (!contexts.size && consumingScope.owner === declaringModule)
            for (const root of moduleRoots)
                if (moduleReaches(root, declaringModule))
                    contexts.add(scopeForClass(root));
        return contexts.size ? [...contexts] : [binding.scope];
    }
    function resolveBinding(token, providerScope, seen = new Set()) {
        if (seen.has(token)) {
            diagnostic(`angularProvider: alias cycle at ${qn(token)}`);
            return { implementations: [], certain: false, token };
        }
        seen.add(token);
        const selected = lookup(token, providerScope);
        const implementations = new Set();
        let certain = selected.certain;
        for (const binding of selected.bindings) {
            if (binding.alias) {
                const contexts = aliasInjectors(binding, providerScope);
                const targets = contexts.map(context => resolveBinding(binding.alias, context, new Set(seen)));
                for (const target of targets)
                    target.implementations.forEach(value => implementations.add(value));
                certain = certain && targets.every(target => target.certain) && new Set(targets.flatMap(target => target.implementations)).size === 1;
            }
            else if (binding.implementation)
                implementations.add(binding.implementation);
        }
        if (!selected.bindings.length && ts_morph_1.Node.isClassDeclaration(token)) {
            implementations.add(token);
            certain = true;
        }
        return { implementations: [...implementations], certain, token };
    }
    for (const providerScope of scopes.values())
        for (const [token, bindings] of providerScope.bindings) {
            const selected = resolveBinding(token, providerScope);
            for (const target of selected.implementations)
                addEdge(providerScope.qn, qn(target), 'REGISTERS', {
                    provider_scope: providerScope.identity, token_qn: qn(token), provider_kind: bindings.some(binding => binding.alias) ? 'useExisting' : token === target ? 'class' : 'useClass',
                    confidence_band: selected.certain ? 'high' : 'medium', candidate: !selected.certain,
                });
        }
    const selectors = components.flatMap(component => (string(property(component.metadata, 'selector')) ?? '').split(',').map(selector => ({ selector: selector.trim(), component: component.declaration })).filter(item => item.selector));
    function available(owner, target) {
        const item = components.find(item => item.declaration === owner);
        const explicitImports = property(item?.metadata, 'imports');
        const modules = moduleDeclarations.get(owner) ?? [];
        if (!explicitImports && !modules.length)
            return true;
        const admitted = new Set();
        const visited = new Set();
        const exported = (cls) => {
            if (componentSet.has(cls)) {
                admitted.add(cls);
                return;
            }
            if (visited.has(cls))
                return;
            visited.add(cls);
            for (const declaration of moduleExports.get(cls) ?? [])
                exported(declaration);
        };
        if (explicitImports)
            values(explicitImports).filter(ts_morph_1.Node.isClassDeclaration).forEach(exported);
        for (const module of modules) {
            for (const [child, owners] of moduleDeclarations)
                if (owners.includes(module))
                    admitted.add(child);
            for (const imported of moduleImports.get(module) ?? [])
                exported(imported);
        }
        return admitted.has(target);
    }
    function htmlReference(owner, expression) {
        const names = expression.trim().replace(/;.*$/, '').split('.');
        if (!names.every(name => /^[\w$]+$/.test(name)))
            return [];
        if (names[0] === 'this')
            names.shift();
        let declaration = classField(owner, names[0]);
        if (!declaration) {
            const file = owner.getSourceFile();
            declaration = file.getClasses().find(cls => cls.getName() === names[0]) ?? file.getVariableDeclaration(names[0]);
            for (const imp of file.getImportDeclarations()) {
                const named = imp.getNamedImports().find(item => (item.getAliasNode()?.getText() ?? item.getName()) === names[0]);
                if (named) {
                    const target = model.module(imp, imp.getModuleSpecifierValue());
                    declaration = target ? model.exported(target, named.getName()) : undefined;
                }
                if (imp.getDefaultImport()?.getText() === names[0]) {
                    const target = model.module(imp, imp.getModuleSpecifierValue());
                    declaration = target ? model.exported(target, 'default') : undefined;
                }
                if (imp.getNamespaceImport()?.getText() === names[0] && names.length > 1) {
                    const target = model.module(imp, imp.getModuleSpecifierValue());
                    declaration = target ? model.exported(target, names[1]) : undefined;
                    names.splice(1, 1);
                }
            }
        }
        let value = reference(declaration);
        for (const name of names.slice(1)) {
            if (value && ts_morph_1.Node.isClassDeclaration(value))
                value = reference(value.getProperty(name));
            else if (value && ts_morph_1.Node.isObjectLiteralExpression(value))
                value = reference(property(value, name));
            else
                value = undefined;
        }
        return value && ts_morph_1.Node.isClassDeclaration(value) && componentSet.has(value) ? [value] : [];
    }
    function render(owner, target, templatePath, properties = {}) {
        addEdge(qn(owner), qn(target), 'RENDERS', { template_path: templatePath, confidence_band: 'high', ...properties });
        if (owner !== target) {
            const parents = renderedParents.get(scopeForClass(target)) ?? new Set();
            parents.add(scopeForClass(owner));
            renderedParents.set(scopeForClass(target), parents);
        }
    }
    for (const item of components) {
        const inline = string(property(item.metadata, 'template'));
        const templateUrl = string(property(item.metadata, 'templateUrl'));
        if ((property(item.metadata, 'templateUrl') && templateUrl === undefined) || (property(item.metadata, 'template') && inline === undefined)) {
            complete = false;
            diagnostic(`angularTemplateUnresolved: ${model.relative(item.declaration)}`);
            continue;
        }
        let html = inline;
        let templatePath = model.relative(item.declaration);
        if (templateUrl !== undefined) {
            const absolute = path.resolve(path.dirname(item.declaration.getSourceFile().getFilePath()), templateUrl);
            templatePath = path.relative(model.root, absolute).replace(/\\/g, '/');
            if (templatePath.startsWith('../') || path.isAbsolute(templatePath) || (scope && !scope.includesFile(absolute))) {
                complete = false;
                diagnostic(`angularTemplateScope: refused ${templatePath.slice(0, 240)}`);
                continue;
            }
            html = reader.read(absolute, true);
            if (html === undefined) {
                complete = false;
                diagnostic(`angularTemplateRead: refused ${templatePath}`);
                continue;
            }
            processedTemplates.add(templatePath);
        }
        if (html === undefined)
            continue;
        const clean = html.replace(/<!--[\s\S]*?-->/g, '').replace(/<(script|style)\b[^>]*>[\s\S]*?<\/\1\s*>/gi, '');
        for (const tag of clean.matchAll(/<([a-zA-Z][\w:.-]*)\b((?:"[^"]*"|'[^']*'|[^'">])*)>/g)) {
            const attributes = new Map();
            for (const attr of tag[2].matchAll(/([^\s=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/g))
                attributes.set(attr[1].replace(/^(?:\[\(|\[|\(|\*)|(?:\)\]|\]|\))$/g, ''), attr[2] ?? attr[3] ?? attr[4] ?? '');
            const matches = selectors.filter(({ selector, component }) => available(item.declaration, component) && matchesSelector(selector, tag[1], attributes));
            const targets = new Set(matches.map(match => match.component));
            for (const { selector, component } of matches)
                render(item.declaration, component, templatePath, { render_kind: 'selector', selector, ...(targets.size > 1 ? { confidence_band: 'medium', candidate: true } : {}) });
            const outlet = attributes.get('ngComponentOutlet');
            if (outlet) {
                const targets = htmlReference(item.declaration, outlet);
                if (!targets.length)
                    diagnostic(`angularDynamicComponent: unresolved outlet ${model.relative(item.declaration)}: ${outlet.slice(0, 120)}`);
                for (const target of targets)
                    render(item.declaration, target, templatePath, { render_kind: 'ngComponentOutlet' });
            }
        }
    }
    function fieldDeclaration(expression) {
        if (ts_morph_1.Node.isPropertyAccessExpression(expression) && expression.getExpression().getKind() === ts_morph_1.SyntaxKind.ThisKeyword) {
            const cls = classOf(expression);
            return cls ? classField(cls, expression.getName()) : undefined;
        }
        return expression.getSymbol()?.getDeclarations().find(declaration => model.byPath.has(model.relative(declaration)) &&
            (ts_morph_1.Node.isParameterDeclaration(declaration) || ts_morph_1.Node.isPropertyDeclaration(declaration) || ts_morph_1.Node.isVariableDeclaration(declaration)));
    }
    function typeNode(declaration) { return declaration && (ts_morph_1.Node.isPropertyDeclaration(declaration) || ts_morph_1.Node.isParameterDeclaration(declaration) || ts_morph_1.Node.isVariableDeclaration(declaration)) ? declaration.getTypeNode() : undefined; }
    function initialNode(declaration) { return declaration && (ts_morph_1.Node.isPropertyDeclaration(declaration) || ts_morph_1.Node.isParameterDeclaration(declaration) || ts_morph_1.Node.isVariableDeclaration(declaration)) ? declaration.getInitializer() : undefined; }
    function frameworkType(expression, packageName, expected, seen = new Set()) {
        if (seen.has(expression))
            return false;
        seen.add(expression);
        const declaration = fieldDeclaration(expression);
        const typed = typeNode(declaration);
        if (typed && ts_morph_1.Node.isTypeReference(typed) && imported(typed.getTypeName(), packageName) === expected)
            return true;
        const initial = initialNode(declaration);
        if (initial && ts_morph_1.Node.isCallExpression(initial) && imported(initial.getExpression(), '@angular/core') === 'inject' && imported(initial.getArguments()[0], packageName) === expected)
            return true;
        return !!initial && initial !== expression && frameworkType(initial, packageName, expected, seen);
    }
    function injectionCall(call) {
        if (!ts_morph_1.Node.isCallExpression(call))
            return false;
        if (imported(call.getExpression(), '@angular/core') === 'inject')
            return true;
        const expression = call.getExpression();
        return ts_morph_1.Node.isPropertyAccessExpression(expression) && expression.getName() === 'get' && frameworkType(expression.getExpression(), '@angular/core', 'Injector');
    }
    // Render context must be established before any child injection is resolved.
    for (const cls of classes)
        for (const call of cls.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
            if (classOf(call) !== cls)
                continue;
            const expression = call.getExpression();
            let dynamic;
            if (imported(expression, '@angular/core') === 'createComponent')
                dynamic = 'createComponent';
            if (ts_morph_1.Node.isPropertyAccessExpression(expression)) {
                if (expression.getName() === 'createComponent' && frameworkType(expression.getExpression(), '@angular/core', 'ViewContainerRef'))
                    dynamic = 'viewContainer.createComponent';
                if (expression.getName() === 'open' && frameworkType(expression.getExpression(), '@angular/material/dialog', 'MatDialog'))
                    dynamic = 'dialog.open';
                if (expression.getName() === 'openFromComponent' && frameworkType(expression.getExpression(), '@angular/material/snack-bar', 'MatSnackBar'))
                    dynamic = 'snackbar.openFromComponent';
            }
            if (dynamic) {
                const target = reference(call.getArguments()[0]);
                if (target && ts_morph_1.Node.isClassDeclaration(target) && componentSet.has(target))
                    render(cls, target, model.relative(cls), { render_kind: dynamic, owning_callable: model.owner(call) });
                else
                    diagnostic(`angularDynamicComponent: unresolved ${dynamic} in ${model.relative(cls)}`);
            }
        }
    function select(tokenExpression, context, contract) {
        const field = tokenExpression ? fieldDeclaration(tokenExpression) : undefined;
        const token = tokenReference(tokenExpression);
        const runtime = (!!field && ts_morph_1.Node.isPropertyDeclaration(field) && field.getDecorators().some(dec => imported(dec.getCallExpression()?.getExpression() ?? dec.getExpression(), '@angular/core') === 'Input')) || !!token && (ts_morph_1.Node.isParameterDeclaration(token) || ts_morph_1.Node.isPropertyDeclaration(token));
        const providerScope = scopeForContext(context);
        if (!runtime && token)
            return resolveBinding(token, providerScope);
        const iface = contract ?? tokenInterface(field ?? token);
        const implementations = new Set();
        const candidateTokens = new Set();
        const seenScopes = new Set();
        const visit = (current) => {
            if (seenScopes.has(current))
                return;
            seenScopes.add(current);
            for (const candidate of current.bindings.keys())
                if (iface && tokenInterface(candidate) === iface)
                    candidateTokens.add(candidate);
            current.parents.forEach(visit);
        };
        visit(providerScope);
        for (const candidate of candidateTokens)
            resolveBinding(candidate, providerScope).implementations.forEach(value => implementations.add(value));
        if (!implementations.size && iface && !providerScope.parents.length && !providerScope.bindings.size && !renderedParents.has(providerScope))
            (implementers.get(iface) ?? []).forEach(value => implementations.add(value));
        return { implementations: [...implementations], certain: false, token, runtime: true };
    }
    function receiver(expression, context, seen = new Set()) {
        if (seen.has(expression))
            return {};
        seen.add(expression);
        if (ts_morph_1.Node.isCallExpression(expression) && injectionCall(expression)) {
            const typed = typeInterface(expression.getTypeArguments()[0]);
            const contract = typed ?? tokenInterface(reference(expression.getArguments()[0]));
            return { contract, selection: select(expression.getArguments()[0], context, contract), injected: true };
        }
        const declaration = fieldDeclaration(expression);
        if (declaration) {
            const typed = typeInterface(typeNode(declaration));
            const initial = initialNode(declaration);
            if (initial) {
                const evidence = receiver(initial, context, seen);
                if (evidence.selection || evidence.contract)
                    return { ...evidence, contract: typed ?? evidence.contract };
            }
            if (ts_morph_1.Node.isParameterDeclaration(declaration)) {
                const inject = declaration.getDecorators().find(dec => imported(dec.getCallExpression()?.getExpression() ?? dec.getExpression(), '@angular/core') === 'Inject');
                if (inject)
                    return { contract: typed, selection: select(inject.getArguments()[0], context, typed), injected: true };
            }
            if (typed)
                return { contract: typed };
            const declaredType = reference(typeNode(declaration));
            if (declaredType && ts_morph_1.Node.isClassDeclaration(declaredType)) {
                const injected = isConstructorInjection(declaration, context);
                return { selection: injected ? resolveBinding(declaredType, scopeForContext(context)) : { implementations: [declaredType], certain: true }, injected };
            }
        }
        return {};
    }
    function isConstructorInjection(declaration, context) {
        if (!ts_morph_1.Node.isParameterDeclaration(declaration) || !ts_morph_1.Node.isConstructorDeclaration(declaration.getParent()) || !ts_morph_1.Node.isClassDeclaration(context))
            return false;
        const injector = scopes.get(context);
        return angular.some(item => item.declaration === context) || !!injector && (injector.bindings.size > 0 || injector.parents.length > 0);
    }
    function scopeForContext(context) {
        return configScopes.get(context) ?? makeScope(context);
    }
    function dispatch(call, context) {
        const expression = call.getExpression();
        if (!ts_morph_1.Node.isPropertyAccessExpression(expression))
            return;
        const evidence = receiver(expression.getExpression(), context);
        const member = evidence.contract ? members(evidence.contract).get(expression.getName()) : undefined;
        if (!member && (evidence.contract || !evidence.selection?.implementations.length))
            return;
        const targets = evidence.selection?.implementations ?? [];
        const targetQNs = targets.map(target => implementationMethod(target, expression.getName())).filter((target) => !!target);
        if (evidence.injected && targets.length) {
            const original = model.target(expression);
            if (original && !targetQNs.includes(original)) {
                const key = callKey(model.owner(call), original);
                if (provisionalCalls.has(key)) {
                    redirectedCallSites.add(call);
                    redirectedCallKeys.add(key);
                }
            }
        }
        const properties = {
            call_kind: evidence.contract ? 'interface_dispatch' : evidence.injected ? 'injected_dispatch' : 'typed_dispatch',
            ...(evidence.contract ? { interface_qn: qn(evidence.contract) } : {}),
            confidence_band: evidence.selection?.certain ? 'high' : 'medium', candidate: !evidence.selection?.certain,
            ...(evidence.selection?.token ? { token_qn: qn(evidence.selection.token) } : {}),
        };
        if (targets.length) {
            for (const targetQN of targetQNs)
                addEdge(model.owner(call), targetQN, 'CALLS', properties);
        }
        else if (member) {
            const owner = member.getFirstAncestorByKind(ts_morph_1.SyntaxKind.InterfaceDeclaration);
            addEdge(model.owner(call), `${qn(owner)}.${expression.getName()}`, 'CALLS', properties);
        }
    }
    for (const cls of classes) {
        const sites = [];
        for (const ctor of cls.getConstructors())
            for (const parameter of ctor.getParameters()) {
                const inject = parameter.getDecorators().find(dec => imported(dec.getCallExpression()?.getExpression() ?? dec.getExpression(), '@angular/core') === 'Inject');
                if (inject)
                    sites.push({ name: parameter.getName(), expression: inject.getArguments()[0], contract: typeInterface(parameter.getTypeNode()) });
                else if (isConstructorInjection(parameter, cls)) {
                    const token = reference(parameter.getTypeNode());
                    if (token && ts_morph_1.Node.isClassDeclaration(token))
                        sites.push({ name: parameter.getName(), expression: parameter.getTypeNode() });
                }
            }
        for (const field of cls.getProperties()) {
            const initial = field.getInitializer();
            if (initial && injectionCall(initial) && ts_morph_1.Node.isCallExpression(initial))
                sites.push({ name: field.getName(), expression: initial.getArguments()[0], contract: typeInterface(field.getTypeNode()) ?? typeInterface(initial.getTypeArguments()[0]) });
        }
        for (const call of cls.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
            if (classOf(call) !== cls || !injectionCall(call) || ts_morph_1.Node.isPropertyDeclaration(call.getParent()))
                continue;
            sites.push({ name: model.owner(call), expression: call.getArguments()[0], contract: typeInterface(call.getTypeArguments()[0]) });
        }
        const injectionNames = new Set(sites.map(site => site.name));
        for (let index = edges.length - 1; index >= 0; index--)
            if (edges[index].type === 'INJECTS' && edges[index].sourceQN === qn(cls) && injectionNames.has(String(edges[index].properties?.parameter_name)))
                edges.splice(index, 1);
        for (const site of sites) {
            const selected = select(site.expression, cls, site.contract);
            for (const target of selected.implementations)
                addEdge(qn(cls), qn(target), 'INJECTS', {
                    parameter_name: site.name, token_qn: selected.token ? qn(selected.token) : undefined, provider_scope: scopeForClass(cls).qn,
                    confidence_band: selected.certain ? 'high' : 'medium', candidate: !selected.certain, runtime_token: !!selected.runtime, ...(selected.runtime && site.expression ? { runtime_token_expression: site.expression.getText() } : {}),
                });
            if (!selected.implementations.length && site.contract)
                addEdge(qn(cls), qn(site.contract), 'INJECTS', { parameter_name: site.name, confidence_band: 'medium', candidate: true });
        }
        for (const call of cls.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
            if (classOf(call) !== cls)
                continue;
            dispatch(call, cls);
        }
    }
    for (const file of model.files)
        for (const call of file.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
            if (classOf(call))
                continue;
            dispatch(call, call.getAncestors().find(ancestor => configScopes.has(ancestor)) ?? file);
        }
    if (redirectedCallKeys.size) {
        const retainedKeys = new Set();
        for (const file of model.files)
            for (const call of file.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
                if (redirectedCallSites.has(call))
                    continue;
                const target = model.target(call.getExpression());
                if (!target)
                    continue;
                const key = callKey(model.owner(call), target);
                if (redirectedCallKeys.has(key))
                    retainedKeys.add(key);
            }
        const superseded = new Set();
        for (const key of redirectedCallKeys)
            if (!retainedKeys.has(key))
                for (const edge of provisionalCalls.get(key) ?? [])
                    superseded.add(edge);
        for (let index = edges.length - 1; index >= 0; index--)
            if (superseded.has(edges[index]))
                edges.splice(index, 1);
    }
    diagnostic(`angularLinks: ${JSON.stringify({ components: components.length, interfaceMembers: [...interfaceMembers.values()].reduce((sum, values) => sum + values.size, 0), providerScopes: scopes.size, processedTemplates: processedTemplates.size, renders: edges.filter(edge => edge.type === 'RENDERS' && edge.properties?.framework === 'angular').length, injections: edges.filter(edge => edge.type === 'INJECTS').length, registrations: edges.filter(edge => edge.type === 'REGISTERS' && edge.properties?.framework === 'angular').length, dispatchCalls: edges.filter(edge => edge.type === 'CALLS' && edge.properties?.framework === 'angular').length })}`);
    return { complete: complete && reader.complete, processedTemplates: [...processedTemplates].sort() };
}
function matchesSelector(selector, tag, attributes) {
    const negatives = [];
    const positive = selector.replace(/:not\(([^)]+)\)/g, (_, value) => { negatives.push(value); return ''; });
    if (negatives.some(value => matchesSelector(value, tag, attributes)))
        return false;
    const element = positive.match(/^[\w-]+/);
    if (element && element[0].toLowerCase() !== tag.toLowerCase())
        return false;
    for (const match of positive.matchAll(/\[([^\]=\s]+)(?:\s*=\s*["']?([^\]"']+)["']?)?\]/g))
        if (!attributes.has(match[1]) || (match[2] !== undefined && attributes.get(match[1]) !== match[2]))
            return false;
    const classNames = new Set((attributes.get('class') ?? '').split(/\s+/));
    for (const match of positive.matchAll(/\.([\w-]+)/g))
        if (!classNames.has(match[1]))
            return false;
    return !!positive && !positive.replace(/^[\w-]+/, '').replace(/\[[^\]]+\]/g, '').replace(/\.[\w-]+/g, '').trim();
}
