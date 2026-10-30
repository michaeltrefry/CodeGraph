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
const packageExports_1 = require("./packageExports");
const extractionWork_1 = require("./extractionWork");
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
    constructor(repository, root, files, imports, diagnostic, control = {}) {
        this.repository = repository;
        this.root = root;
        this.files = files;
        this.imports = imports;
        this.diagnostic = diagnostic;
        this.declarations = new Map();
        this.callables = new Map();
        this.byPath = new Map();
        this.byQN = new Map();
        this.relativePaths = new Map();
        this.importBySource = new Map();
        this.resolvedModules = new Map();
        this.externalCallSymbols = new Map();
        this.exportIndexes = new Map();
        this.publicNameSets = new Map();
        this.starNameSources = new Map();
        this.returnExpressions = new Map();
        this.allVariables = [];
        this.writeTargets = [];
        this.buildingMutations = false;
        this.work = control instanceof extractionWork_1.ExtractionWorkBudget ? control : new extractionWork_1.ExtractionWorkBudget(control);
        for (const file of files)
            this.byPath.set(this.relative(file), file);
        for (const imported of imports) {
            const key = `${imported.filePath}\0${imported.importedNamespace}`;
            if (!this.importBySource.has(key))
                this.importBySource.set(key, imported);
        }
        for (const file of files) {
            file.forEachDescendant(node => {
                this.work.charge();
                if (ts_morph_1.Node.isVariableDeclaration(node))
                    this.allVariables.push(node);
                if (ts_morph_1.Node.isBinaryExpression(node) && node.getOperatorToken().getKind() >= ts_morph_1.SyntaxKind.FirstAssignment && node.getOperatorToken().getKind() <= ts_morph_1.SyntaxKind.LastAssignment)
                    this.writeTargets.push(node.getLeft());
                else if ((ts_morph_1.Node.isPrefixUnaryExpression(node) || ts_morph_1.Node.isPostfixUnaryExpression(node)) && [ts_morph_1.SyntaxKind.PlusPlusToken, ts_morph_1.SyntaxKind.MinusMinusToken].includes(node.getOperatorToken()))
                    this.writeTargets.push(node.getOperand());
                else if (ts_morph_1.Node.isDeleteExpression(node))
                    this.writeTargets.push(node.getExpression());
                else if (ts_morph_1.Node.isCallExpression(node)) {
                    const expr = node.getExpression();
                    if (ts_morph_1.Node.isPropertyAccessExpression(expr) && expr.getName() === 'assign' && ts_morph_1.Node.isIdentifier(expr.getExpression()) && expr.getExpression().getText() === 'Object' && node.getArguments()[0])
                        this.writeTargets.push(node.getArguments()[0]);
                }
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
    relative(node) {
        const file = node.getSourceFile();
        let relative = this.relativePaths.get(file);
        if (relative === undefined) {
            relative = path.relative(this.root, file.getFilePath()).replace(/\\/g, '/');
            this.relativePaths.set(file, relative);
        }
        return relative;
    }
    fileQN(node) { return `${this.repository}:${this.relative(node)}`; }
    identify(node, qn) { this.declarations.set(node, qn); this.byQN.set(qn, node); }
    qn(node) {
        const owner = node.getAncestors().find(ancestor => isCallable(ancestor) || ts_morph_1.Node.isClassDeclaration(ancestor));
        const nameNode = node.getNameNode?.();
        this.work.charge((nameNode?.getFullWidth() ?? 0) + 1);
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
        this.work.charge(scopes.reduce((size, value) => size + value.length, 0) + containers.reduce((size, value) => size + value.length, 0) + name.length + this.relative(node).length + this.repository.length + 8);
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
        this.work.charge(this.relative(from).length + specifier.length + 1);
        const key = `${this.relative(from)}\0${specifier}`;
        if (this.resolvedModules.has(key))
            return this.resolvedModules.get(key);
        const resolved = this.importBySource.get(key);
        const module = resolved?.resolvedFilePath ? this.byPath.get(resolved.resolvedFilePath) : undefined;
        this.resolvedModules.set(key, module);
        return module;
    }
    exportIndex(file) {
        this.work.charge();
        const cached = this.exportIndexes.get(file);
        if (cached)
            return cached;
        this.work.charge(file.compilerNode.statements.length * 2 + 1);
        const index = { declarations: [], direct: new Map(), explicit: new Set(), named: new Map(), stars: [] };
        const exports = file.getSymbol();
        this.work.charge((exports?.compilerSymbol.exports?.size ?? 0) + 1);
        const exportedSymbols = new Set();
        const defaultSymbols = new Set();
        for (const symbol of exports?.getExports() ?? []) {
            this.work.charge();
            const alias = symbol.getAliasedSymbol();
            exportedSymbols.add(symbol.compilerSymbol);
            if (alias)
                exportedSymbols.add(alias.compilerSymbol);
            if (symbol.getName() === 'default') {
                defaultSymbols.add(symbol.compilerSymbol);
                if (alias)
                    defaultSymbols.add(alias.compilerSymbol);
            }
        }
        for (const statement of file.getStatements()) {
            this.work.charge();
            if (ts_morph_1.Node.isFunctionDeclaration(statement) || ts_morph_1.Node.isClassDeclaration(statement) || ts_morph_1.Node.isInterfaceDeclaration(statement) || ts_morph_1.Node.isEnumDeclaration(statement))
                index.declarations.push(statement);
            if (ts_morph_1.Node.isVariableStatement(statement)) {
                this.work.charge(statement.compilerNode.declarationList.declarations.length);
                index.declarations.push(...statement.getDeclarations());
            }
            if (ts_morph_1.Node.isExportAssignment(statement) && !index.assignment)
                index.assignment = statement;
            if (!ts_morph_1.Node.isExportDeclaration(statement))
                continue;
            const named = statement.getNamedExports();
            if (!named.length)
                index.stars.push(statement);
            for (const item of named) {
                this.work.charge(item.getFullWidth() + 1);
                const name = item.getAliasNode()?.getText() ?? item.getName();
                index.explicit.add(name);
                if (!index.named.has(name))
                    index.named.set(name, []);
                index.named.get(name).push({ declaration: statement, item });
            }
        }
        for (const declaration of index.declarations) {
            this.work.charge((declaration.getNameNode()?.getFullWidth() ?? 0) + 1);
            const name = declaration.getName();
            const symbol = (!declaration.hasExportKeyword() || defaultSymbols.size) ? declaration.getSymbol()?.compilerSymbol : undefined;
            const isDefault = declaration.hasDefaultKeyword() || !!symbol && defaultSymbols.has(symbol);
            if (isDefault) {
                index.explicit.add('default');
                if (!index.defaultDeclaration)
                    index.defaultDeclaration = declaration;
            }
            if (name && (declaration.hasExportKeyword() || !!symbol && exportedSymbols.has(symbol))) {
                if (!index.direct.has(name))
                    index.direct.set(name, declaration);
                index.explicit.add(isDefault ? 'default' : name);
            }
        }
        if (index.assignment)
            index.explicit.add('default');
        this.exportIndexes.set(file, index);
        return index;
    }
    publicExportNames(file) {
        this.work.charge();
        const prior = this.publicNameSets.get(file);
        if (prior)
            return prior;
        const pending = [file];
        const discovered = new Set();
        const reverse = new Map();
        const frontier = new Set();
        while (pending.length) {
            this.work.charge();
            const current = pending.pop();
            if (discovered.has(current))
                continue;
            discovered.add(current);
            if (this.publicNameSets.has(current)) {
                frontier.add(current);
                continue;
            }
            const index = this.exportIndex(current);
            const names = new Set();
            for (const name of index.explicit) {
                this.work.text(name);
                names.add(name);
            }
            this.publicNameSets.set(current, names);
            for (const star of index.stars) {
                this.work.charge();
                const specifier = star.getModuleSpecifierValue();
                if (!specifier)
                    continue;
                const target = this.module(current, specifier);
                if (!target)
                    continue;
                if (!reverse.has(target))
                    reverse.set(target, new Set());
                reverse.get(target).add(current);
                pending.push(target);
            }
        }
        const queue = [];
        for (const current of discovered)
            for (const name of this.publicNameSets.get(current) ?? []) {
                this.work.text(name);
                queue.push([current, name]);
            }
        for (let cursor = 0; cursor < queue.length; cursor++) {
            this.work.charge();
            const [source, name] = queue[cursor];
            if (name === 'default')
                continue;
            for (const consumer of reverse.get(source) ?? []) {
                this.work.text(name);
                const names = this.publicNameSets.get(consumer);
                if (!names.has(name)) {
                    names.add(name);
                    queue.push([consumer, name]);
                }
            }
        }
        return this.publicNameSets.get(file);
    }
    starSources(file, name) {
        let index = this.starNameSources.get(file);
        if (!index) {
            index = new Map();
            const targets = new Set();
            for (const star of this.exportIndex(file).stars) {
                this.work.charge();
                const specifier = star.getModuleSpecifierValue();
                const target = specifier ? this.module(file, specifier) : undefined;
                if (target)
                    targets.add(target);
            }
            for (const target of targets)
                for (const publicName of this.publicExportNames(target)) {
                    this.work.text(publicName);
                    if (publicName === 'default')
                        continue;
                    if (!index.has(publicName))
                        index.set(publicName, []);
                    index.get(publicName).push(target);
                }
            this.starNameSources.set(file, index);
        }
        return index.get(name) ?? [];
    }
    /** One run-wide alias/mutation graph. Writes propagate back to referenced storage once;
     * imported provenance anchors preserve namespace and exact named-import uncertainty. */
    ensureMutationIndex() {
        if (this.mutationIndex)
            return this.mutationIndex;
        const written = new Set();
        if (!this.writeTargets.length) {
            this.mutationIndex = written;
            return written;
        }
        if (this.buildingMutations)
            throw new Error('Recursive member mutation indexing');
        this.buildingMutations = true;
        const links = new Map();
        const key = (node) => { this.work.charge(); return node.getSymbol()?.getDeclarations()[0] ?? node; };
        const link = (from, target) => {
            this.work.charge();
            if (!links.has(from))
                links.set(from, new Set());
            links.get(from).add(target);
        };
        const root = (node) => {
            while (ts_morph_1.Node.isPropertyAccessExpression(node) || ts_morph_1.Node.isElementAccessExpression(node) || ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node)) {
                this.work.charge();
                node = node.getExpression();
            }
            return node;
        };
        try {
            for (const alias of this.allVariables) {
                this.work.charge();
                const init = alias.getInitializer();
                if (!init)
                    continue;
                const source = root(init);
                if (!ts_morph_1.Node.isIdentifier(source))
                    continue;
                const resolved = this.resolve(init);
                const rootDeclaration = this.resolve(source);
                const binding = alias.getNameNode();
                const names = ts_morph_1.Node.isIdentifier(binding) ? [binding] : binding.getDescendantsOfKind(ts_morph_1.SyntaxKind.Identifier);
                for (const name of names) {
                    const from = key(name);
                    link(from, key(source));
                    if (resolved)
                        link(from, key(resolved));
                    if (rootDeclaration)
                        link(from, key(rootDeclaration));
                }
            }
            for (const file of this.files)
                for (const imported of file.getImportDeclarations()) {
                    this.work.charge(imported.getModuleSpecifier().getFullWidth() + 1);
                    const specifier = imported.getModuleSpecifierValue();
                    for (const named of imported.getNamedImports()) {
                        this.work.charge(named.getFullWidth() + 1);
                        const local = named.getAliasNode() ?? named.getNameNode();
                        const from = key(local);
                        const target = this.resolve(local);
                        if (target)
                            link(from, key(target));
                        link(from, `named\0${specifier}\0${named.getName()}`);
                        link(from, `any\0${specifier}`);
                    }
                    const namespace = imported.getNamespaceImport();
                    if (namespace)
                        link(key(namespace), `namespace\0${specifier}`);
                }
            for (const target of this.writeTargets) {
                let part = target;
                while (part) {
                    this.work.charge();
                    written.add(key(part));
                    const resolved = this.resolve(part);
                    if (resolved)
                        written.add(key(resolved));
                    part = ts_morph_1.Node.isPropertyAccessExpression(part) || ts_morph_1.Node.isElementAccessExpression(part) ? part.getExpression() : undefined;
                }
            }
            const queue = [...written];
            for (let cursor = 0; cursor < queue.length; cursor++) {
                this.work.charge();
                for (const source of links.get(queue[cursor]) ?? []) {
                    this.work.charge();
                    if (!written.has(source)) {
                        written.add(source);
                        queue.push(source);
                    }
                }
            }
            this.mutationIndex = written;
            return written;
        }
        finally {
            this.buildingMutations = false;
        }
    }
    hasMemberWrites(node, provenance) {
        this.work.charge();
        const written = this.ensureMutationIndex();
        if (!written.size)
            return false;
        const key = node.getSymbol()?.getDeclarations()[0] ?? node;
        if (written.has(key))
            return true;
        const resolved = this.resolve(node);
        if (resolved && written.has(resolved.getSymbol()?.getDeclarations()[0] ?? resolved))
            return true;
        if (!provenance)
            return false;
        const specifier = String(provenance.import_path);
        this.work.text(specifier);
        return written.has(`namespace\0${specifier}`) || written.has(provenance.export_name === undefined ? `any\0${specifier}` : `named\0${specifier}\0${provenance.export_name}`);
    }
    /** Imported npm provenance, including aliases and bounded local barrels. A display name or
     * an instance member alone never establishes an external application declaration. */
    externalReference(expression, seen = new Set()) {
        if (!expression || seen.has(expression) || seen.size > 60)
            return undefined;
        seen.add(expression);
        const imported = (from, specifier, name, history = seen) => {
            const module = this.module(from, specifier);
            if (module)
                return exported(module, name, history);
            const resolution = this.importBySource.get(`${this.relative(from)}\0${specifier}`);
            if (resolution?.classification !== 'external_npm_package' || !resolution.externalPackageName || specifier.startsWith('node:'))
                return undefined;
            return { qualifiedName: `npm-symbol:${specifier}#${name}`, properties: {
                    package_name: resolution.externalPackageName, import_path: specifier, export_name: name,
                } };
        };
        const exported = (file, name, history) => {
            if (history.has(file) || history.size > 60)
                return undefined;
            const branch = new Set(history);
            branch.add(file);
            if (name === 'default' && file.getExportAssignments().length)
                return this.externalReference(file.getExportAssignments()[0].getExpression(), branch);
            const index = this.exportIndex(file);
            const explicit = index.named.get(name) ?? [];
            if (explicit.length) {
                const candidates = explicit.map(({ declaration, item }) => declaration.getModuleSpecifierValue()
                    ? imported(declaration, declaration.getModuleSpecifierValue(), item.getName(), branch)
                    : this.externalReference(item.getNameNode(), branch));
                const identities = new Map(candidates.filter((value) => !!value).map(value => [value.qualifiedName, value]));
                return candidates.every(value => !!value) && identities.size === 1 ? [...identities.values()][0] : undefined;
            }
            // Resolve a directly exported local alias before considering stars. Actual local
            // functions/classes have already resolved through target(), so they cannot become imports.
            const candidate = name === 'default' ? index.defaultDeclaration : index.direct.get(name);
            const local = candidate && (ts_morph_1.Node.isFunctionDeclaration(candidate) || ts_morph_1.Node.isClassDeclaration(candidate) || ts_morph_1.Node.isVariableDeclaration(candidate)) ? candidate : undefined;
            if (local)
                return this.externalReference(local, branch);
            const stars = index.stars.filter(declaration => !!declaration.getModuleSpecifierValue());
            const candidates = stars.map(declaration => imported(declaration, declaration.getModuleSpecifierValue(), name, branch));
            const identities = new Map(candidates.filter((value) => !!value).map(value => [value.qualifiedName, value]));
            return candidates.length && candidates.every(value => !!value) && identities.size === 1 ? [...identities.values()][0] : undefined;
        };
        if (ts_morph_1.Node.isParenthesizedExpression(expression) || ts_morph_1.Node.isAsExpression(expression) || ts_morph_1.Node.isNonNullExpression(expression))
            return this.externalReference(expression.getExpression(), seen);
        if (ts_morph_1.Node.isVariableDeclaration(expression) && expression.getVariableStatement()?.getDeclarationKind() === 'const')
            return this.externalReference(expression.getInitializer(), seen);
        if (ts_morph_1.Node.isPropertyAccessExpression(expression)) {
            const namespace = expression.getExpression().getSymbol()?.getDeclarations() ?? [];
            if (namespace.length === 1 && ts_morph_1.Node.isNamespaceImport(namespace[0])) {
                const declaration = namespace[0].getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration);
                if (this.hasMemberWrites(expression.getExpression(), { import_path: declaration.getModuleSpecifierValue() })) {
                    this.diagnostic(`externalMemberUnresolved: ${declaration.getModuleSpecifierValue()} namespace has writes`);
                    return undefined;
                }
                return imported(declaration, declaration.getModuleSpecifierValue(), expression.getName());
            }
            const base = this.externalReference(expression.getExpression(), new Set(seen));
            if (base) {
                if (this.hasMemberWrites(expression.getExpression(), base.properties)) {
                    this.diagnostic(`externalMemberUnresolved: ${base.qualifiedName} has writes through its binding or aliases`);
                    return undefined;
                }
                const member = [base.properties.member_path, expression.getName()].filter(Boolean).join('.');
                return { qualifiedName: `npm-symbol:${base.properties.import_path}#${base.properties.export_name}::${member}`,
                    properties: { ...base.properties, member_path: member } };
            }
            return undefined;
        }
        const declarations = expression.getSymbol()?.getDeclarations() ?? [];
        if (declarations.length !== 1)
            return undefined;
        const declaration = declarations[0];
        if (ts_morph_1.Node.isImportSpecifier(declaration)) {
            const imp = declaration.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration);
            return imported(imp, imp.getModuleSpecifierValue(), declaration.getName());
        }
        if (ts_morph_1.Node.isImportClause(declaration))
            return imported(declaration, declaration.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration).getModuleSpecifierValue(), 'default');
        if (ts_morph_1.Node.isVariableDeclaration(declaration))
            return this.externalReference(declaration, seen);
        return undefined;
    }
    exported(file, name, seen = new Set()) {
        this.work.charge(this.relative(file).length + name.length + seen.size + 1);
        const index = this.exportIndex(file);
        const key = `${this.relative(file)}:${name}`;
        if (seen.has(key))
            return undefined;
        seen.add(key);
        if (name === 'default') {
            if (index.assignment)
                return this.resolve(index.assignment.getExpression(), seen);
            if (index.defaultDeclaration)
                return index.defaultDeclaration;
        }
        const direct = index.direct.get(name);
        if (direct)
            return direct;
        const candidates = new Set();
        for (const { declaration, item } of index.named.get(name) ?? []) {
            this.work.charge(seen.size + 1);
            const specifier = declaration.getModuleSpecifierValue();
            const target = specifier ? this.module(file, specifier) : file;
            if (!target)
                continue;
            const value = target === file ? this.resolve(item.getNameNode(), new Set(seen)) : this.exported(target, item.getName(), new Set(seen));
            if (value)
                candidates.add(value);
        }
        if (name !== 'default')
            for (const target of this.starSources(file, name)) {
                this.work.charge(seen.size + 1);
                const value = this.exported(target, name, new Set(seen));
                if (value)
                    candidates.add(value);
            }
        if (candidates.size > 1)
            this.diagnostic(`symbolAmbiguity: ${key} has ${candidates.size} export candidates`);
        return candidates.size === 1 ? [...candidates][0] : undefined;
    }
    resolve(node, seen = new Set()) {
        this.work.charge();
        if (!node)
            return undefined;
        if (seen.size > 100) {
            this.diagnostic(`symbolResolutionLimit: ${this.relative(node)}`);
            return undefined;
        }
        this.work.charge(node.getSourceFile().getFilePath().length + 32);
        const key = `${node.getSourceFile().getFilePath()}:${node.getPos()}:${node.getKind()}`;
        if (seen.has(key))
            return undefined;
        seen.add(key);
        if (this.declarations.has(node)) {
            if (ts_morph_1.Node.isVariableDeclaration(node) || ts_morph_1.Node.isPropertyDeclaration(node)) {
                const init = node.getInitializer();
                if (init)
                    return this.resolve(init, seen) ?? node;
                const type = node.getTypeNode();
                if (type)
                    return this.resolve(type, seen) ?? node;
            }
            return node;
        }
        if (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isNonNullExpression(node) || ts_morph_1.Node.isTypeAssertion(node))
            return this.resolve(node.getExpression(), seen);
        if (ts_morph_1.Node.isNewExpression(node))
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
                    let expressions = this.returnExpressions.get(callee);
                    if (!expressions) {
                        expressions = [];
                        const pending = [body];
                        while (pending.length) {
                            this.work.charge();
                            const current = pending.pop();
                            if (current !== body && isCallable(current))
                                continue;
                            if (ts_morph_1.Node.isReturnStatement(current) && current.getExpression())
                                expressions.push(current.getExpression());
                            current.forEachChild(child => { this.work.charge(); pending.push(child); });
                        }
                        if (!ts_morph_1.Node.isBlock(body))
                            expressions = [body];
                        this.returnExpressions.set(callee, expressions);
                    }
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
                let external;
                if (target && callableQNs.has(target))
                    edges.push({ sourceQN: source, targetQN: target, type: 'CALLS' });
                else if ((external = this.externalReference(call.getExpression()))) {
                    if (!this.externalCallSymbols.has(external.qualifiedName))
                        this.externalCallSymbols.set(external.qualifiedName, {
                            label: 'ExternalSymbol', name: [external.properties.export_name, external.properties.member_path].filter(Boolean).join('.'), qualifiedName: external.qualifiedName,
                            filePath: this.relative(call), startLine: 0, endLine: 0, properties: external.properties,
                        });
                    edges.push({ sourceQN: source, targetQN: external.qualifiedName, type: 'CALLS', properties: { call_kind: 'external_import' } });
                }
                for (const arg of call.getArguments()) {
                    const callback = this.target(arg);
                    if (callback && callback !== source && callableQNs.has(callback))
                        edges.push({ sourceQN: source, targetQN: callback, type: 'CALLS', properties: { call_kind: 'callback' } });
                }
                if (!target && !external && unresolved.length < 100 && !this.reactApi(call))
                    unresolved.push({ callerQN: source, calleeName: call.getExpression().getText().slice(0, 160), confidence: 0 });
            }
        }
    }
    reconcilePackageExports(nodes) {
        // Rendering may already have materialized the same npm symbol. Keep a single declaration
        // owner instead of emitting conflicting node copies for call-plus-render references.
        const present = new Set(nodes.map(node => node.qualifiedName));
        for (const [qn, node] of this.externalCallSymbols)
            if (!present.has(qn))
                nodes.push(node);
        (0, packageExports_1.addNpmExportMetadata)(this, nodes);
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
