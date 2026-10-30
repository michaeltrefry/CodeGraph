"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.StaticEvaluator = exports.emptyObject = exports.isObject = exports.isUnknown = exports.unknown = void 0;
exports.staticKey = staticKey;
const ts_morph_1 = require("ts-morph");
const unknown = (hint = 'param') => ({ kind: 'unknown', hint });
exports.unknown = unknown;
const isUnknown = (value) => typeof value === 'object' && value !== null && value.kind === 'unknown';
exports.isUnknown = isUnknown;
const isObject = (value) => typeof value === 'object' && value !== null && value.kind === 'object';
exports.isObject = isObject;
const emptyObject = () => ({ kind: 'object', properties: new Map(), unknownKeys: false });
exports.emptyObject = emptyObject;
function staticKey(value) {
    if ((0, exports.isUnknown)(value))
        return '?';
    if ((0, exports.isObject)(value))
        return JSON.stringify([value.unknownKeys, [...value.properties].sort(([a], [b]) => a.localeCompare(b)).map(([k, v]) => [k, staticKey(v)])]);
    return JSON.stringify(value) ?? 'undefined';
}
/** Bounded, non-executing evaluation over admitted ASTs only. Environments are keyed by
 * declarations, never identifier text; no process-global caches survive extraction. */
class StaticEvaluator {
    constructor(model, diagnostic) {
        this.model = model;
        this.diagnostic = diagnostic;
        this.mutations = new Map();
        this.aliasParents = new Map();
        this.writtenBindings = new Set();
    }
    evaluate(node, env = new Map(), seen = new Set()) {
        if (!node)
            return undefined;
        if (seen.has(node) || seen.size > 60)
            return (0, exports.unknown)();
        seen = new Set(seen);
        seen.add(node);
        const ev = (value) => this.evaluate(value, env, seen);
        // Bound arguments are still mutable inside the callee. Check writes before an
        // environment hit can return a stale object/method supplied by its caller.
        if (this.isBinding(node) && this.mutated(node))
            return (0, exports.unknown)();
        if (env.has(node))
            return env.get(node);
        if (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isNonNullExpression(node) || ts_morph_1.Node.isTypeAssertion(node) || ts_morph_1.Node.isAwaitExpression(node) || ts_morph_1.Node.isSatisfiesExpression(node))
            return ev(node.getExpression());
        if (ts_morph_1.Node.isStringLiteral(node) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(node))
            return node.getLiteralText();
        if (ts_morph_1.Node.isNumericLiteral(node))
            return node.getLiteralValue();
        if (node.getKind() === ts_morph_1.SyntaxKind.TrueKeyword)
            return true;
        if (node.getKind() === ts_morph_1.SyntaxKind.FalseKeyword)
            return false;
        if (node.getKind() === ts_morph_1.SyntaxKind.NullKeyword)
            return null;
        if (ts_morph_1.Node.isIdentifier(node) && node.getText() === 'undefined' && !node.getSymbol()?.getDeclarations().some(d => this.admitted(d)))
            return undefined;
        if (ts_morph_1.Node.isTemplateExpression(node)) {
            let value = node.getHead().getLiteralText();
            for (const span of node.getTemplateSpans())
                value += this.text(ev(span.getExpression())) + span.getLiteral().getLiteralText();
            return value;
        }
        if (ts_morph_1.Node.isBinaryExpression(node)) {
            const op = node.getOperatorToken().getKind();
            const left = ev(node.getLeft());
            if (op === ts_morph_1.SyntaxKind.PlusToken) {
                const right = ev(node.getRight());
                if (typeof left === 'number' && typeof right === 'number')
                    return left + right;
                if (typeof left === 'string' || typeof right === 'string')
                    return this.text(left) + this.text(right);
                return (0, exports.unknown)();
            }
            if (op === ts_morph_1.SyntaxKind.QuestionQuestionToken)
                return (0, exports.isUnknown)(left) ? (0, exports.unknown)() : left == null ? ev(node.getRight()) : left;
            if (op === ts_morph_1.SyntaxKind.BarBarToken)
                return (0, exports.isUnknown)(left) ? (0, exports.unknown)() : left ? left : ev(node.getRight());
            if (op === ts_morph_1.SyntaxKind.AmpersandAmpersandToken)
                return (0, exports.isUnknown)(left) ? (0, exports.unknown)() : left ? ev(node.getRight()) : left;
            if (op === ts_morph_1.SyntaxKind.EqualsEqualsEqualsToken || op === ts_morph_1.SyntaxKind.EqualsEqualsToken)
                return (0, exports.isUnknown)(left) || (0, exports.isUnknown)(ev(node.getRight())) ? (0, exports.unknown)() : left === ev(node.getRight());
            if (op === ts_morph_1.SyntaxKind.ExclamationEqualsEqualsToken || op === ts_morph_1.SyntaxKind.ExclamationEqualsToken)
                return (0, exports.isUnknown)(left) || (0, exports.isUnknown)(ev(node.getRight())) ? (0, exports.unknown)() : left !== ev(node.getRight());
            return (0, exports.unknown)();
        }
        if (ts_morph_1.Node.isConditionalExpression(node)) {
            const condition = ev(node.getCondition());
            if (!(0, exports.isUnknown)(condition))
                return ev(condition ? node.getWhenTrue() : node.getWhenFalse());
            const yes = ev(node.getWhenTrue()), no = ev(node.getWhenFalse());
            if (staticKey(yes) === staticKey(no))
                return yes;
            // A conditional query suffix cannot change endpoint identity.
            if (typeof yes === 'string' && typeof no === 'string' && [yes, no].every(v => !v || /^[?#]/.test(v)))
                return '?{param}';
            return (0, exports.unknown)();
        }
        if (ts_morph_1.Node.isObjectLiteralExpression(node)) {
            const result = (0, exports.emptyObject)();
            for (const prop of node.getProperties()) {
                if (ts_morph_1.Node.isSpreadAssignment(prop)) {
                    const spread = ev(prop.getExpression());
                    if ((0, exports.isObject)(spread)) {
                        if (spread.unknownKeys) {
                            for (const key of result.properties.keys())
                                result.properties.set(key, (0, exports.unknown)());
                            result.unknownKeys = true;
                        }
                        for (const [key, value] of spread.properties)
                            result.properties.set(key, value);
                    }
                    else if (spread != null) {
                        for (const key of result.properties.keys())
                            result.properties.set(key, (0, exports.unknown)());
                        result.unknownKeys = true;
                    }
                }
                else if (ts_morph_1.Node.isPropertyAssignment(prop)) {
                    const name = prop.getNameNode();
                    const key = ts_morph_1.Node.isComputedPropertyName(name) ? ev(name.getExpression()) : prop.getName().replace(/^['"]|['"]$/g, '');
                    if (typeof key === 'string')
                        result.properties.set(key, ev(prop.getInitializer()));
                    else {
                        for (const key of result.properties.keys())
                            result.properties.set(key, (0, exports.unknown)());
                        result.unknownKeys = true;
                    }
                }
                else if (ts_morph_1.Node.isShorthandPropertyAssignment(prop))
                    result.properties.set(prop.getName(), ev(prop.getValueSymbol()?.getDeclarations().find(d => this.admitted(d))));
            }
            return result;
        }
        if (ts_morph_1.Node.isPropertyAccessExpression(node) || ts_morph_1.Node.isElementAccessExpression(node)) {
            const key = ts_morph_1.Node.isPropertyAccessExpression(node) ? node.getName() : ev(node.getArgumentExpression());
            const receiver = ev(node.getExpression());
            if (typeof key === 'string' && (0, exports.isObject)(receiver))
                return receiver.properties.has(key) ? receiver.properties.get(key) : receiver.unknownKeys ? (0, exports.unknown)(key) : undefined;
            if (key === 'href' && typeof receiver === 'string' && this.urlExpression(node.getExpression()))
                return receiver;
            // A default object's inferred type does not prove a parameter's runtime property.
            // Follow declaration properties only for static class/enum/namespace ownership or this.
            const expression = node.getExpression();
            const owner = this.model.resolve(expression);
            const staticOwner = expression.getKind() === ts_morph_1.SyntaxKind.ThisKeyword ||
                !!owner && (ts_morph_1.Node.isClassDeclaration(owner) || ts_morph_1.Node.isEnumDeclaration(owner)) ||
                (expression.getSymbol()?.getDeclarations() ?? []).some(ts_morph_1.Node.isNamespaceImport);
            if (staticOwner) {
                const declaration = node.getSymbol()?.getDeclarations().find(d => this.admitted(d));
                if (declaration)
                    return ev(declaration);
                const resolved = this.model.resolve(node);
                if (resolved && resolved !== node)
                    return ev(resolved);
            }
            return (0, exports.unknown)(typeof key === 'string' ? key : 'param');
        }
        if (ts_morph_1.Node.isNewExpression(node) && this.builtin(node.getExpression(), 'URL')) {
            const [url, base] = node.getArguments().map(ev);
            if (typeof url !== 'string')
                return (0, exports.unknown)();
            if (/^[a-z][\w+.-]*:\/\//i.test(url))
                return url;
            if (typeof base !== 'string' || base.includes('{param}'))
                return (0, exports.unknown)();
            try {
                return new URL(url, base).href.replace(/%7Bparam%7D/gi, '{param}');
            }
            catch {
                return (0, exports.unknown)();
            }
        }
        if (ts_morph_1.Node.isCallExpression(node)) {
            const expression = node.getExpression();
            if (ts_morph_1.Node.isPropertyAccessExpression(expression) && expression.getName() === 'toString' && node.getArguments().length === 0) {
                const receiver = expression.getExpression();
                const value = ev(receiver);
                if (typeof value === 'string')
                    return value;
            }
            // Encode known segments without executing arbitrary user code.
            if (this.builtin(expression, 'encodeURIComponent')) {
                const value = ev(node.getArguments()[0]);
                return (0, exports.isUnknown)(value) ? (0, exports.unknown)() : typeof value === 'string' ? encodeURIComponent(value) : (0, exports.unknown)();
            }
            const callable = this.model.resolve(expression);
            if (callable && (ts_morph_1.Node.isFunctionDeclaration(callable) || ts_morph_1.Node.isFunctionExpression(callable) || ts_morph_1.Node.isArrowFunction(callable) || ts_morph_1.Node.isMethodDeclaration(callable))) {
                const next = this.bind(callable, node.getArguments().map(ev), env);
                const body = callable.getBody();
                const values = !body ? [] : ts_morph_1.Node.isBlock(body) ? body.getDescendantsOfKind(ts_morph_1.SyntaxKind.ReturnStatement)
                    .filter(r => this.model.owner(r) === this.model.callables.get(callable)).map(r => this.evaluate(r.getExpression(), next, seen)) : [this.evaluate(body, next, seen)];
                if (values.length && values.every(v => staticKey(v) === staticKey(values[0])))
                    return values[0];
            }
            return (0, exports.unknown)();
        }
        if (ts_morph_1.Node.isIdentifier(node)) {
            const declarations = node.getSymbol()?.getDeclarations().filter(d => this.admitted(d)) ?? [];
            if (declarations.length === 1)
                return ev(declarations[0]);
            const resolved = this.model.resolve(node);
            if (resolved && resolved !== node)
                return ev(resolved);
            return (0, exports.unknown)(node.getText());
        }
        if (ts_morph_1.Node.isVariableDeclaration(node) || ts_morph_1.Node.isPropertyDeclaration(node) || ts_morph_1.Node.isPropertyAssignment(node) || ts_morph_1.Node.isParameterDeclaration(node) || ts_morph_1.Node.isEnumMember(node) || ts_morph_1.Node.isBindingElement(node)) {
            return node.getInitializer() ? ev(node.getInitializer()) : (0, exports.unknown)('getName' in node ? node.getName() : 'param');
        }
        if (ts_morph_1.Node.isImportSpecifier(node) || ts_morph_1.Node.isImportClause(node)) {
            const imp = node.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration);
            const file = this.model.module(node, imp.getModuleSpecifierValue());
            const declaration = file ? this.model.exported(file, ts_morph_1.Node.isImportSpecifier(node) ? node.getName() : 'default') : undefined;
            return declaration ? ev(declaration) : (0, exports.unknown)();
        }
        return (0, exports.unknown)();
    }
    bind(callable, values, outer = new Map()) {
        const env = new Map(outer);
        if (!(ts_morph_1.Node.isFunctionDeclaration(callable) || ts_morph_1.Node.isFunctionExpression(callable) || ts_morph_1.Node.isArrowFunction(callable) || ts_morph_1.Node.isMethodDeclaration(callable) || ts_morph_1.Node.isConstructorDeclaration(callable)))
            return env;
        callable.getParameters().forEach((parameter, index) => {
            const value = values[index] === undefined && parameter.getInitializer() ? this.evaluate(parameter.getInitializer(), env) : values[index];
            env.set(parameter, value);
            const name = parameter.getNameNode();
            if (ts_morph_1.Node.isObjectBindingPattern(name))
                for (const element of name.getElements()) {
                    const key = element.getPropertyNameNode()?.getText() ?? element.getName();
                    let property = (0, exports.isObject)(value) ? value.properties.has(key) ? value.properties.get(key) : value.unknownKeys ? (0, exports.unknown)(key) : undefined : (0, exports.unknown)(key);
                    if (property === undefined && element.getInitializer())
                        property = this.evaluate(element.getInitializer(), env);
                    env.set(element, property);
                }
        });
        return env;
    }
    builtin(node, name) {
        return ts_morph_1.Node.isIdentifier(node) && node.getText() === name && !(node.getSymbol()?.getDeclarations() ?? []).some(d => this.admitted(d));
    }
    admitted(node) { return this.model.byPath.has(this.model.relative(node)); }
    text(value) { return (0, exports.isUnknown)(value) || (0, exports.isObject)(value) ? '{param}' : String(value); }
    urlExpression(node, seen = new Set()) {
        if (seen.has(node) || seen.size > 30)
            return false;
        seen.add(node);
        if (ts_morph_1.Node.isNewExpression(node))
            return this.builtin(node.getExpression(), 'URL');
        if (ts_morph_1.Node.isVariableDeclaration(node) || ts_morph_1.Node.isPropertyDeclaration(node)) {
            const init = node.getInitializer();
            return !!init && this.urlExpression(init, seen);
        }
        const declarations = node.getSymbol()?.getDeclarations().filter(d => this.admitted(d)) ?? [];
        return declarations.length === 1 && this.urlExpression(declarations[0], seen);
    }
    assignmentTargets(node) {
        return ts_morph_1.Node.isPropertyAccessExpression(node) || ts_morph_1.Node.isElementAccessExpression(node) ? [node, ...this.assignmentTargets(node.getExpression())] : [node];
    }
    isBinding(node) {
        return ts_morph_1.Node.isVariableDeclaration(node) || ts_morph_1.Node.isPropertyDeclaration(node) || ts_morph_1.Node.isParameterDeclaration(node) || ts_morph_1.Node.isBindingElement(node);
    }
    aliasKey(node) { return node.getSymbol()?.compilerSymbol ?? node; }
    aliasRoot(key) {
        if (!this.aliasParents.has(key)) {
            this.aliasParents.set(key, key);
            return key;
        }
        let root = key;
        while (this.aliasParents.get(root) !== root)
            root = this.aliasParents.get(root);
        while (key !== root) {
            const parent = this.aliasParents.get(key);
            this.aliasParents.set(key, root);
            key = parent;
        }
        return root;
    }
    joinAliases(left, right) {
        const a = this.aliasRoot(left), b = this.aliasRoot(right);
        if (a !== b)
            this.aliasParents.set(a, b);
    }
    /** One admitted-AST write/alias index per extraction, rather than a full fleet scan
     * per evaluated binding. Static aliases share a conservative mutable root; symbols
     * and explicit import resolution keep unrelated names and modules independent. */
    indexWrites() {
        if (this.writtenAliases)
            return;
        const writes = [];
        const aliases = [];
        for (const file of this.model.files) {
            for (const imp of file.getImportDeclarations()) {
                const target = this.model.module(file, imp.getModuleSpecifierValue());
                if (!target)
                    continue;
                for (const named of imp.getNamedImports()) {
                    const exported = this.model.exported(target, named.getName());
                    if (exported)
                        this.joinAliases(this.aliasKey(named.getAliasNode() ?? named.getNameNode()), this.aliasKey(exported));
                }
                const local = imp.getDefaultImport();
                const exported = local ? this.model.exported(target, 'default') : undefined;
                if (local && exported)
                    this.joinAliases(this.aliasKey(local), this.aliasKey(exported));
            }
            file.forEachDescendant(node => {
                if (this.isBinding(node))
                    aliases.push(node);
                if (ts_morph_1.Node.isBinaryExpression(node) && node.getOperatorToken().getKind() >= ts_morph_1.SyntaxKind.FirstAssignment && node.getOperatorToken().getKind() <= ts_morph_1.SyntaxKind.LastAssignment)
                    writes.push({ target: node.getLeft(), member: !ts_morph_1.Node.isIdentifier(node.getLeft()) });
                else if ((ts_morph_1.Node.isPrefixUnaryExpression(node) || ts_morph_1.Node.isPostfixUnaryExpression(node)) && [ts_morph_1.SyntaxKind.PlusPlusToken, ts_morph_1.SyntaxKind.MinusMinusToken].includes(node.getOperatorToken()))
                    writes.push({ target: node.getOperand(), member: !ts_morph_1.Node.isIdentifier(node.getOperand()) });
                else if (ts_morph_1.Node.isDeleteExpression(node))
                    writes.push({ target: node.getExpression(), member: !ts_morph_1.Node.isIdentifier(node.getExpression()) });
                else if (ts_morph_1.Node.isCallExpression(node) && node.getExpression().getText() === 'Object.assign' && node.getArguments()[0])
                    writes.push({ target: node.getArguments()[0], member: true });
            });
        }
        const identities = new Map();
        const restSources = new Map();
        const uncertainIdentities = new Set();
        const memberName = (node) => {
            if (ts_morph_1.Node.isComputedPropertyName(node)) {
                const expression = node.getExpression();
                return ts_morph_1.Node.isStringLiteral(expression) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(expression) ? expression.getLiteralText() : ts_morph_1.Node.isNumericLiteral(expression) ? String(expression.getLiteralValue()) : undefined;
            }
            return ts_morph_1.Node.isStringLiteral(node) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(node) ? node.getLiteralText() : ts_morph_1.Node.isNumericLiteral(node) ? String(node.getLiteralValue()) : node.getText();
        };
        const selectedMember = (receiver, name, seen) => {
            const source = identity(receiver, seen);
            if (uncertainIdentities.has(receiver))
                return { node: source, uncertain: true };
            if (seen.has(source))
                return undefined;
            seen = new Set(seen);
            seen.add(source);
            if (ts_morph_1.Node.isSourceFile(source)) {
                const member = this.model.exported(source, name);
                return member ? { node: member } : undefined;
            }
            if (ts_morph_1.Node.isArrayLiteralExpression(source)) {
                const elements = source.getElements();
                // A spread changes subsequent indices and may have unknown length. Retain
                // the receiver identity so a write covers every statically contained source.
                if (elements.some(ts_morph_1.Node.isSpreadElement))
                    return { node: source, uncertain: true };
                const member = /^(?:0|[1-9]\d*)$/.test(name) ? elements[Number(name)] : undefined;
                return member ? { node: member } : undefined;
            }
            if (ts_morph_1.Node.isClassDeclaration(source)) {
                const member = source.getProperty(name);
                return member ? { node: member } : undefined;
            }
            if (!ts_morph_1.Node.isObjectLiteralExpression(source))
                return undefined;
            // An unresolved spread/computed override may select any contained object.
            // Keep the container as the conservative identity instead of picking one child.
            if (source.getProperties().some(ts_morph_1.Node.isSpreadAssignment))
                return { node: source, uncertain: true };
            for (const property of source.getProperties().reverse()) {
                if (ts_morph_1.Node.isPropertyAssignment(property)) {
                    const key = memberName(property.getNameNode());
                    if (key === undefined)
                        return { node: source, uncertain: true };
                    if (key === name) {
                        const member = property.getInitializer();
                        return member ? { node: member } : undefined;
                    }
                }
                else if (ts_morph_1.Node.isShorthandPropertyAssignment(property) && property.getName() === name) {
                    const member = property.getValueSymbol()?.getDeclarations().find(declaration => this.admitted(declaration));
                    return member ? { node: member } : undefined;
                }
            }
            return undefined;
        };
        // Resolve declaration/member identity, not runtime values. A parameter remains a
        // stable identity even when no invocation value exists in the indexed AST.
        const identity = (node, seen = new Set()) => {
            if (seen.has(node) || seen.size >= 60)
                return node;
            const cached = identities.get(node);
            if (cached)
                return cached;
            seen = new Set(seen);
            seen.add(node);
            let target;
            let uncertain = false;
            if (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isNonNullExpression(node) || ts_morph_1.Node.isTypeAssertion(node))
                target = node.getExpression();
            else if (ts_morph_1.Node.isVariableDeclaration(node) || ts_morph_1.Node.isPropertyDeclaration(node))
                target = node.getInitializer();
            else if (ts_morph_1.Node.isBindingElement(node)) {
                const path = [];
                let element = node;
                let source;
                let rest = false;
                for (;;) {
                    const pattern = element.getParentOrThrow();
                    const name = element.getPropertyNameNode() ?? element.getNameNode();
                    if (element.getDotDotDotToken())
                        rest = true;
                    else
                        path.unshift(ts_morph_1.Node.isArrayBindingPattern(pattern) ? String(pattern.getElements().indexOf(element)) : memberName(name));
                    const owner = pattern.getParentOrThrow();
                    if (ts_morph_1.Node.isBindingElement(owner)) {
                        element = owner;
                        continue;
                    }
                    if (ts_morph_1.Node.isVariableDeclaration(owner))
                        source = owner.getInitializer();
                    break; // Destructured parameters retain separate invocation-bound identities.
                }
                for (const name of path) {
                    if (!source)
                        break;
                    const receiver = source;
                    const selected = name === undefined ? undefined : selectedMember(receiver, name, new Set(seen));
                    if (!selected || selected.uncertain) {
                        source = selected?.node ?? identity(receiver, new Set(seen));
                        uncertain = true;
                        break;
                    }
                    source = selected.node;
                }
                // Rest creates a new container with shifted/omitted members. Preserve its
                // source for conservative write invalidation, without inventing an index map.
                if (rest && source) {
                    restSources.set(node, source);
                    uncertain = true;
                }
                else
                    target = source ?? node.getInitializer();
            }
            else if (ts_morph_1.Node.isImportSpecifier(node) || ts_morph_1.Node.isImportClause(node) || ts_morph_1.Node.isNamespaceImport(node)) {
                const declaration = node.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration);
                const file = this.model.module(node, declaration.getModuleSpecifierValue());
                target = file && (ts_morph_1.Node.isNamespaceImport(node) ? file : this.model.exported(file, ts_morph_1.Node.isImportSpecifier(node) ? node.getName() : 'default'));
            }
            else if (ts_morph_1.Node.isIdentifier(node)) {
                const declarations = node.getSymbol()?.getDeclarations().filter(declaration => this.admitted(declaration)) ?? [];
                if (declarations.length === 1)
                    target = declarations[0];
            }
            else if (ts_morph_1.Node.isPropertyAccessExpression(node) || ts_morph_1.Node.isElementAccessExpression(node)) {
                const argument = ts_morph_1.Node.isElementAccessExpression(node) ? node.getArgumentExpression() : undefined;
                const name = ts_morph_1.Node.isPropertyAccessExpression(node) ? node.getName() : argument && (ts_morph_1.Node.isStringLiteral(argument) || ts_morph_1.Node.isNumericLiteral(argument)) ? String(argument.getLiteralValue()) : undefined;
                const selected = name === undefined ? undefined : selectedMember(node.getExpression(), name, new Set(seen));
                target = selected?.node;
                uncertain = selected?.uncertain ?? false;
                // Unknown members conservatively refer to their own receiver, never all
                // siblings of an object that merely contains that receiver.
                target ?? (target = node.getSymbol()?.getDeclarations().find(declaration => this.admitted(declaration) && ts_morph_1.Node.isPropertyDeclaration(declaration)));
                if (!target) {
                    target = node.getExpression();
                    uncertain = true;
                }
            }
            const result = target && target !== node ? identity(target, seen) : node;
            if (uncertain || target && uncertainIdentities.has(target))
                uncertainIdentities.add(node);
            identities.set(node, result);
            return result;
        };
        for (const alias of aliases) {
            const target = identity(alias);
            this.joinAliases(this.aliasKey(alias), this.aliasKey(target));
            if ((ts_morph_1.Node.isParameterDeclaration(alias) || ts_morph_1.Node.isBindingElement(alias)) && alias.getInitializer())
                this.joinAliases(this.aliasKey(alias), this.aliasKey(identity(alias.getInitializer())));
        }
        // Containment is directional: writes through a container may affect referenced
        // children, but writing one child cannot mutate the container's other values.
        const contained = (container) => {
            const rest = restSources.get(container);
            if (rest)
                return [rest];
            if (ts_morph_1.Node.isArrayLiteralExpression(container))
                return container.getElements().map(element => ts_morph_1.Node.isSpreadElement(element) ? element.getExpression() : element);
            if (!ts_morph_1.Node.isObjectLiteralExpression(container))
                return [];
            return container.getProperties().flatMap(property => {
                if (ts_morph_1.Node.isPropertyAssignment(property)) {
                    const value = property.getInitializer();
                    return value ? [value] : [];
                }
                if (ts_morph_1.Node.isShorthandPropertyAssignment(property)) {
                    const value = property.getValueSymbol()?.getDeclarations().find(declaration => this.admitted(declaration));
                    return value ? [value] : [];
                }
                return ts_morph_1.Node.isSpreadAssignment(property) ? [property.getExpression()] : [];
            });
        };
        this.writtenAliases = new Set();
        const marked = new Set();
        const mark = (reference) => {
            const source = identity(reference);
            this.writtenAliases.add(this.aliasRoot(this.aliasKey(source)));
            if (marked.has(source))
                return;
            marked.add(source);
            for (const child of contained(source))
                mark(child);
        };
        for (const { target, member } of writes) {
            // Rebinding a local changes that binding, not objects that previously captured
            // its value. Member writes, unlike rebinding, invalidate the shared referent.
            if (!member) {
                this.writtenBindings.add(this.aliasKey(target));
                continue;
            }
            for (const reference of this.assignmentTargets(target))
                mark(reference);
        }
    }
    mutated(node) {
        if (this.mutations.has(node))
            return this.mutations.get(node);
        this.indexWrites();
        const key = this.aliasKey(node);
        const mutated = this.writtenBindings.has(key) || this.writtenAliases.has(this.aliasRoot(key));
        this.mutations.set(node, mutated);
        return mutated;
    }
}
exports.StaticEvaluator = StaticEvaluator;
