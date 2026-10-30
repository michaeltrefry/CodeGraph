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
exports.SemanticModel = void 0;
exports.declarationQN = declarationQN;
exports.sameSymbol = sameSymbol;
exports.isCallable = isCallable;
const ts_morph_1 = require("ts-morph");
const path = __importStar(require("path"));
function declarationQN(file, name) {
    return [...file.replace(/\.(?:tsx?|jsx?)$/, '').replace(/\\/g, '/').split('/').filter(p => p !== 'src' && p !== ''), name].join('.');
}
function sameSymbol(reference, declaration) {
    const symbol = reference.getSymbol();
    return !!symbol && !!declaration && symbol.compilerSymbol === declaration.getSymbol()?.compilerSymbol;
}
function isCallable(node) {
    return ts_morph_1.Node.isFunctionDeclaration(node) || ts_morph_1.Node.isFunctionExpression(node) || ts_morph_1.Node.isArrowFunction(node) ||
        ts_morph_1.Node.isMethodDeclaration(node) || ts_morph_1.Node.isGetAccessorDeclaration(node) || ts_morph_1.Node.isSetAccessorDeclaration(node) || ts_morph_1.Node.isConstructorDeclaration(node);
}
/** Shared, source-scoped symbol seam for subsequent HTTP, routes and desktop passes.
 * All resolution is bounded to the already admitted ASTs. Package dependencies remain imports,
 * not guessed same-name declarations. Explicit exports/aliases are followed without node_modules.
 */
class SemanticModel {
    constructor(repository, root, files, imports, diagnostic) {
        this.repository = repository;
        this.root = root;
        this.files = files;
        this.imports = imports;
        this.diagnostic = diagnostic;
        this.declarations = new Map();
        this.callables = new Map();
        this.byPath = new Map();
        this.byQN = new Map();
        for (const file of files)
            this.byPath.set(this.relative(file), file);
        for (const file of files) {
            file.forEachDescendant(node => {
                if (ts_morph_1.Node.isClassDeclaration(node) || ts_morph_1.Node.isInterfaceDeclaration(node) || ts_morph_1.Node.isEnumDeclaration(node) || ts_morph_1.Node.isVariableDeclaration(node)) {
                    this.identify(node, this.qn(node));
                }
                if (isCallable(node)) {
                    const qn = this.qn(node);
                    this.identify(node, qn);
                    this.callables.set(node, qn);
                }
            });
        }
    }
    relative(node) { return path.relative(this.root, node.getSourceFile().getFilePath()).replace(/\\/g, '/'); }
    fileQN(node) { return `${this.repository}:${this.relative(node)}`; }
    identify(node, qn) { this.declarations.set(node, qn); this.byQN.set(qn, node); }
    qn(node) {
        const owner = node.getAncestors().find(ancestor => isCallable(ancestor) || ts_morph_1.Node.isClassDeclaration(ancestor));
        let name = node.getName?.();
        const parent = node.getParent();
        if (parent && (ts_morph_1.Node.isVariableDeclaration(parent) || ts_morph_1.Node.isPropertyAssignment(parent) || ts_morph_1.Node.isPropertyDeclaration(parent)) &&
            (ts_morph_1.Node.isArrowFunction(node) || ts_morph_1.Node.isFunctionExpression(node)))
            name = parent.getName();
        if (!name && parent && ts_morph_1.Node.isExportAssignment(parent))
            name = 'default';
        if (!name && ts_morph_1.Node.isClassDeclaration(node) && node.isDefaultExport())
            name = 'default';
        // React wrappers own the enclosed render callable under the exported variable's identity.
        if (!name && parent && ts_morph_1.Node.isCallExpression(parent)) {
            let wrapper = parent;
            while (ts_morph_1.Node.isCallExpression(wrapper) && this.reactApi(wrapper)) {
                const next = wrapper.getParent();
                if (next && ts_morph_1.Node.isVariableDeclaration(next)) {
                    name = next.getName();
                    break;
                }
                if (!next)
                    break;
                wrapper = next;
            }
        }
        if (!name) {
            // AST-relative path survives unrelated edits elsewhere in the file; duplicate callbacks in
            // the same owner remain distinct without relying on line numbers or global counters.
            let current = node;
            const segments = [];
            while (current.getParent() && current.getParent() !== owner && !ts_morph_1.Node.isSourceFile(current.getParent())) {
                const parentNode = current.getParentOrThrow();
                segments.unshift(parentNode.getChildren().indexOf(current));
                current = parentNode;
            }
            name = ts_morph_1.Node.isConstructorDeclaration(node) ? 'constructor' : `$callback${segments.join('_')}`;
        }
        const containers = [];
        for (const ancestor of node.getAncestors()) {
            if (ancestor === owner)
                break;
            if ((ts_morph_1.Node.isVariableDeclaration(ancestor) || ts_morph_1.Node.isPropertyAssignment(ancestor) || ts_morph_1.Node.isPropertyDeclaration(ancestor)) &&
                ts_morph_1.Node.isObjectLiteralExpression(ancestor.getInitializer()))
                containers.unshift(ancestor.getName());
        }
        const scopes = [];
        if (owner)
            for (const ancestor of node.getAncestors()) {
                if (ancestor === owner)
                    break;
                if (!ts_morph_1.Node.isBlock(ancestor) || ancestor.getParent() === owner)
                    continue;
                let current = ancestor;
                const indices = [];
                while (current.getParent() && current !== owner) {
                    const parentNode = current.getParentOrThrow();
                    indices.unshift(parentNode.getChildren().indexOf(current));
                    current = parentNode;
                }
                scopes.unshift(`$scope${indices.join('_')}`);
            }
        const lexicalName = [...scopes, ...containers, name].join('.');
        if (owner)
            return `${this.declarations.get(owner) ?? this.qn(owner)}.${lexicalName}`;
        return ts_morph_1.Node.isClassDeclaration(node) || ts_morph_1.Node.isInterfaceDeclaration(node) || ts_morph_1.Node.isEnumDeclaration(node)
            ? declarationQN(this.relative(node), lexicalName)
            : `${this.fileQN(node)}#${lexicalName}`;
    }
    owner(node) {
        for (const ancestor of node.getAncestors()) {
            const callable = this.callables.get(ancestor);
            if (callable)
                return callable;
        }
        return this.fileQN(node);
    }
    module(from, specifier) {
        const resolved = this.imports.find(item => item.filePath === this.relative(from) && item.importedNamespace === specifier);
        return resolved?.resolvedFilePath ? this.byPath.get(resolved.resolvedFilePath) : undefined;
    }
    exported(file, name, seen = new Set()) {
        const key = `${this.relative(file)}:${name}`;
        if (seen.has(key))
            return undefined;
        seen.add(key);
        if (name === 'default') {
            const assignment = file.getExportAssignments()[0];
            if (assignment)
                return this.resolve(assignment.getExpression(), seen);
            const declaration = [...file.getFunctions(), ...file.getClasses()].find(item => item.isDefaultExport());
            if (declaration)
                return declaration;
        }
        for (const declaration of [...file.getFunctions(), ...file.getClasses(), ...file.getInterfaces(), ...file.getVariableDeclarations(), ...file.getEnums()]) {
            if (declaration.getName() === name && declaration.isExported())
                return declaration;
        }
        const candidates = new Set();
        for (const exp of file.getExportDeclarations()) {
            const target = exp.getModuleSpecifierValue() ? this.module(file, exp.getModuleSpecifierValue()) : file;
            if (!target)
                continue;
            const named = exp.getNamedExports();
            if (named.length) {
                const item = named.find(item => (item.getAliasNode()?.getText() ?? item.getName()) === name);
                if (item) {
                    const value = target === file ? this.resolve(item.getNameNode(), new Set(seen)) : this.exported(target, item.getName(), new Set(seen));
                    if (value)
                        candidates.add(value);
                }
            }
            else if (name !== 'default') {
                const value = this.exported(target, name, new Set(seen));
                if (value)
                    candidates.add(value);
            }
        }
        if (candidates.size > 1)
            this.diagnostic(`symbolAmbiguity: ${key} has ${candidates.size} export candidates`);
        return candidates.size === 1 ? [...candidates][0] : undefined;
    }
    resolve(node, seen = new Set()) {
        if (!node)
            return undefined;
        if (seen.size > 100) {
            this.diagnostic(`symbolResolutionLimit: ${this.relative(node)}`);
            return undefined;
        }
        const key = `${node.getSourceFile().getFilePath()}:${node.getPos()}:${node.getKind()}`;
        if (seen.has(key))
            return undefined;
        seen.add(key);
        if (this.declarations.has(node)) {
            if (ts_morph_1.Node.isVariableDeclaration(node) || ts_morph_1.Node.isPropertyDeclaration(node)) {
                const init = node.getInitializer();
                if (init)
                    return this.resolve(init, seen) ?? node;
            }
            return node;
        }
        if (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isNonNullExpression(node) || ts_morph_1.Node.isTypeAssertion(node))
            return this.resolve(node.getExpression(), seen);
        if (ts_morph_1.Node.isCallExpression(node)) {
            if (['memo', 'forwardRef', 'useCallback'].includes(this.reactApi(node) ?? ''))
                return this.resolve(node.getArguments()[0], seen);
            if (this.reactApi(node) === 'lazy') {
                const load = node.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression).find(call => call.getExpression().getKind() === ts_morph_1.SyntaxKind.ImportKeyword);
                const argument = load?.getArguments()[0];
                if (argument && ts_morph_1.Node.isStringLiteral(argument)) {
                    const file = this.module(node, argument.getLiteralText());
                    // lazy(() => import(...).then(m => ({ default: m.Named })))
                    const named = node.getDescendantsOfKind(ts_morph_1.SyntaxKind.PropertyAssignment).find(prop => prop.getName() === 'default');
                    const expression = named?.getInitializer();
                    const exportName = expression && ts_morph_1.Node.isPropertyAccessExpression(expression) ? expression.getName() : 'default';
                    return file ? this.exported(file, exportName) : undefined;
                }
            }
            // A known local/imported factory or custom hook can return a callable/object. Follow
            // only statically unique returns and keep alternatives explicitly unresolved.
            const callee = this.resolve(node.getExpression(), new Set(seen));
            if (callee && (ts_morph_1.Node.isFunctionDeclaration(callee) || ts_morph_1.Node.isFunctionExpression(callee) || ts_morph_1.Node.isArrowFunction(callee) || ts_morph_1.Node.isMethodDeclaration(callee))) {
                const body = callee.getBody();
                if (body) {
                    const expressions = ts_morph_1.Node.isBlock(body)
                        ? body.getDescendantsOfKind(ts_morph_1.SyntaxKind.ReturnStatement)
                            .filter(statement => this.owner(statement) === this.callables.get(callee))
                            .flatMap(statement => statement.getExpression() ? [statement.getExpression()] : [])
                        : [body];
                    const resolvedReturns = expressions.map(expression => this.resolve(expression, new Set(seen)));
                    const returns = new Set(resolvedReturns.filter((value) => !!value));
                    if (returns.size === 1 && resolvedReturns.every(value => !!value))
                        return [...returns][0];
                    if (returns.size > 1 || returns.size && resolvedReturns.some(value => !value))
                        this.diagnostic(`functionReturnAmbiguity: ${this.declarations.get(callee)} has ${returns.size} known return candidates and ${resolvedReturns.filter(value => !value).length} unknown returns`);
                }
            }
            return undefined;
        }
        const symbol = node.getSymbol();
        const declarations = symbol?.getDeclarations() ?? [];
        for (const declaration of declarations) {
            if (!this.byPath.has(this.relative(declaration)))
                continue;
            if (ts_morph_1.Node.isImportSpecifier(declaration)) {
                const imp = declaration.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration);
                const target = this.module(node, imp.getModuleSpecifierValue());
                if (target)
                    return this.exported(target, declaration.getName(), seen);
            }
            else if (ts_morph_1.Node.isImportClause(declaration)) {
                const imp = declaration.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration);
                const target = this.module(node, imp.getModuleSpecifierValue());
                if (target)
                    return this.exported(target, 'default', seen);
            }
            else if (this.declarations.has(declaration))
                return this.resolve(declaration, seen);
            else if (ts_morph_1.Node.isPropertyDeclaration(declaration) || ts_morph_1.Node.isPropertyAssignment(declaration))
                return this.resolve(declaration.getInitializer(), seen);
            else if (ts_morph_1.Node.isParameterDeclaration(declaration)) {
                const type = declaration.getTypeNode();
                if (type)
                    return this.resolve(type, seen);
            }
        }
        if (ts_morph_1.Node.isPropertyAccessExpression(node)) {
            const receiver = node.getExpression();
            if (ts_morph_1.Node.isIdentifier(receiver)) {
                for (const imp of node.getSourceFile().getImportDeclarations()) {
                    if (!sameSymbol(receiver, imp.getNamespaceImport()))
                        continue;
                    const file = this.module(node, imp.getModuleSpecifierValue());
                    if (file)
                        return this.exported(file, node.getName(), seen);
                }
            }
            const target = this.resolve(receiver, seen);
            if (target && ts_morph_1.Node.isClassDeclaration(target))
                return target.getMethod(node.getName()) ?? target.getProperty(node.getName());
            if (target && ts_morph_1.Node.isObjectLiteralExpression(target)) {
                const prop = target.getProperty(node.getName());
                if (prop && ts_morph_1.Node.isPropertyAssignment(prop))
                    return this.resolve(prop.getInitializer(), seen);
                if (prop && ts_morph_1.Node.isShorthandPropertyAssignment(prop))
                    return this.resolve(prop.getValueSymbol()?.getDeclarations()[0], seen);
                if (prop && ts_morph_1.Node.isMethodDeclaration(prop))
                    return prop;
            }
        }
        if (ts_morph_1.Node.isTypeReference(node))
            return this.resolve(node.getTypeName(), seen);
        if (ts_morph_1.Node.isObjectLiteralExpression(node))
            return node;
        return isCallable(node) ? node : undefined;
    }
    target(node) {
        const resolved = this.resolve(node);
        return resolved ? this.declarations.get(resolved) : undefined;
    }
    /** Recognize React APIs only through an actual react import, including named aliases. */
    reactApi(call) {
        const expression = call.getExpression();
        for (const imp of call.getSourceFile().getImportDeclarations()) {
            if (imp.getModuleSpecifierValue() !== 'react')
                continue;
            if (ts_morph_1.Node.isIdentifier(expression)) {
                const specifier = imp.getNamedImports().find(item => (item.getAliasNode()?.getText() ?? item.getName()) === expression.getText());
                if (specifier && sameSymbol(expression, specifier.getAliasNode() ?? specifier.getNameNode()))
                    return specifier.getName();
            }
            if (ts_morph_1.Node.isPropertyAccessExpression(expression) &&
                (sameSymbol(expression.getExpression(), imp.getNamespaceImport()) || sameSymbol(expression.getExpression(), imp.getDefaultImport())))
                return expression.getName();
        }
        return undefined;
    }
    extract(nodes, edges, unresolved) {
        const existing = new Map(nodes.map(node => [node.qualifiedName, node]));
        const callableQNs = new Set(this.callables.values());
        for (const [callable, qn] of this.callables) {
            const jsx = callable.getDescendants().some(node => ts_morph_1.Node.isJsxElement(node) || ts_morph_1.Node.isJsxSelfClosingElement(node) || ts_morph_1.Node.isJsxFragment(node));
            const name = qn.split(/[.#]/).pop();
            const component = jsx && (/^[A-Z]/.test(name) || name === 'default');
            const node = existing.get(qn);
            if (node) {
                if (component) {
                    node.label = 'Component';
                    node.properties.framework = 'react';
                }
                continue;
            }
            const emitted = { label: component ? 'Component' : ts_morph_1.Node.isMethodDeclaration(callable) ? 'Method' : 'Function', name,
                qualifiedName: qn, filePath: this.relative(callable), startLine: callable.getStartLineNumber(), endLine: callable.getEndLineNumber(),
                properties: { callable: true, ...(component ? { framework: 'react' } : {}), is_hook: /^use[A-Z]/.test(name) } };
            nodes.push(emitted);
            existing.set(qn, emitted);
            const owner = callable.getAncestors().find(node => this.callables.has(node) || ts_morph_1.Node.isClassDeclaration(node));
            edges.push({ sourceQN: owner ? this.declarations.get(owner) : this.fileQN(callable), targetQN: qn,
                type: owner && ts_morph_1.Node.isClassDeclaration(owner) ? 'DEFINES_METHOD' : 'DEFINES' });
        }
        for (const file of this.files) {
            for (const attribute of file.getDescendantsOfKind(ts_morph_1.SyntaxKind.JsxAttribute)) {
                if (!/^on[A-Z]/.test(attribute.getNameNode().getText()))
                    continue;
                const init = attribute.getInitializer();
                const target = init && ts_morph_1.Node.isJsxExpression(init) ? this.target(init.getExpression()) : undefined;
                if (target && callableQNs.has(target))
                    edges.push({ sourceQN: this.owner(attribute), targetQN: target, type: 'CALLS', properties: { call_kind: 'event_handler' } });
            }
            for (const call of file.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
                const source = this.owner(call);
                const target = this.target(call.getExpression());
                if (target && callableQNs.has(target))
                    edges.push({ sourceQN: source, targetQN: target, type: 'CALLS' });
                for (const arg of call.getArguments()) {
                    const callback = this.target(arg);
                    if (callback && callback !== source && callableQNs.has(callback))
                        edges.push({ sourceQN: source, targetQN: callback, type: 'CALLS', properties: { call_kind: 'callback' } });
                }
                if (!target && unresolved.length < 100 && !this.reactApi(call))
                    unresolved.push({ callerQN: source, calleeName: call.getExpression().getText().slice(0, 160), confidence: 0 });
            }
        }
    }
    reconcileAngular(nodes, edges) {
        const angular = new Set(['INHERITS', 'IMPLEMENTS', 'INJECTS', 'HANDLES']);
        for (let index = edges.length - 1; index >= 0; index--) {
            const edge = edges[index];
            if (!angular.has(edge.type) || edge.properties?.framework === 'react')
                continue;
            const source = this.byQN.get(edge.sourceQN);
            let expression;
            if (source && ts_morph_1.Node.isClassDeclaration(source)) {
                if (edge.type === 'INHERITS')
                    expression = source.getExtends()?.getExpression();
                if (edge.type === 'IMPLEMENTS')
                    expression = source.getImplements().find(item => item.getExpression().getText() === edge.targetQN)?.getExpression();
                if (edge.type === 'INJECTS') {
                    const parameter = source.getConstructors().flatMap(ctor => ctor.getParameters()).find(param => param.getName() === edge.properties?.parameter_name);
                    expression = parameter?.getTypeNode();
                    if (!expression) {
                        const init = source.getProperty(String(edge.properties?.parameter_name))?.getInitializer();
                        if (init && ts_morph_1.Node.isCallExpression(init))
                            expression = init.getArguments()[0];
                    }
                }
            }
            if (edge.type === 'HANDLES') {
                const route = nodes.find(node => node.qualifiedName === edge.sourceQN);
                const file = route ? this.byPath.get(route.filePath) : undefined;
                expression = file?.getDescendantsOfKind(ts_morph_1.SyntaxKind.PropertyAssignment).find(prop => prop.getName() === 'component' && prop.getInitializer()?.getText() === edge.targetQN)?.getInitializer();
            }
            const target = this.target(expression);
            if (target)
                edge.targetQN = target;
            else
                edges.splice(index, 1);
        }
    }
}
exports.SemanticModel = SemanticModel;
