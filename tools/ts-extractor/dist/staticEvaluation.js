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
    /** Cache source structure only. Values and identity resolution still use each caller's environment. */
    arrayStructure(node) {
        this.model.work.charge();
        const cached = this.arrays.get(node);
        if (cached)
            return cached;
        this.model.work.reserveArrayReferences(node.compilerNode.elements.length);
        const elements = node.getElements();
        let spread = false;
        for (const element of elements) {
            this.model.work.charge();
            spread || (spread = ts_morph_1.Node.isSpreadElement(element));
        }
        const result = { elements, spread };
        this.arrays.set(node, result);
        return result;
    }
    patternStructure(node) {
        this.model.work.charge();
        const cached = this.patterns.get(node);
        if (cached)
            return cached;
        const count = node.compilerNode.elements.length;
        this.model.work.reserveArrayReferences(count * 2);
        const elements = node.getElements(), positions = new Map();
        for (let index = 0; index < elements.length; index++) {
            this.model.work.charge();
            positions.set(elements[index], index);
        }
        const result = { elements, positions };
        this.patterns.set(node, result);
        return result;
    }
    memberName(node) {
        this.model.work.charge(node.getFullWidth() + 1);
        if (ts_morph_1.Node.isComputedPropertyName(node)) {
            const expression = node.getExpression();
            return ts_morph_1.Node.isStringLiteral(expression) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(expression) ? expression.getLiteralText() : ts_morph_1.Node.isNumericLiteral(expression) ? String(expression.getLiteralValue()) : undefined;
        }
        return ts_morph_1.Node.isStringLiteral(node) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(node) ? node.getLiteralText() : ts_morph_1.Node.isNumericLiteral(node) ? String(node.getLiteralValue()) : node.getText();
    }
    objectStructure(node) {
        this.model.work.charge();
        const cached = this.objects.get(node);
        if (cached)
            return cached;
        this.model.work.reserveArrayReferences(node.compilerNode.properties.length * 2);
        const properties = node.getProperties(), members = new Map();
        let unknownPosition = -1, spread = false;
        for (let position = 0; position < properties.length; position++) {
            const property = properties[position];
            this.model.work.charge();
            if (ts_morph_1.Node.isSpreadAssignment(property))
                spread = true;
            else if (ts_morph_1.Node.isPropertyAssignment(property) || ts_morph_1.Node.isShorthandPropertyAssignment(property)) {
                const key = this.memberName(property.getNameNode());
                if (key === undefined)
                    unknownPosition = position;
                else
                    members.set(key, { property, position }); // Last source property wins.
            }
        }
        const result = { properties, members, unknownPosition, spread };
        this.objects.set(node, result);
        return result;
    }
    ownedReturns(callable, body) {
        this.model.work.charge();
        const cached = this.returns.get(callable);
        if (cached)
            return cached;
        this.model.work.charge(body.getFullWidth() + 1);
        const output = [];
        body.forEachDescendant(node => {
            this.model.work.charge();
            if (ts_morph_1.Node.isReturnStatement(node) && this.model.owner(node) === this.model.callables.get(callable)) {
                this.model.work.reserveArrayReferences(1);
                output.push(node);
            }
        });
        this.returns.set(callable, output);
        return output;
    }
    constructor(model, diagnostic) {
        this.model = model;
        this.diagnostic = diagnostic;
        this.aliasParents = new Map();
        this.writtenBindings = new Set();
        this.arrays = new Map();
        this.patterns = new Map();
        this.objects = new Map();
        this.returns = new Map();
    }
    evaluate(node, env = new Map(), seen = new Set()) {
        this.model.work.charge();
        if (!node)
            return undefined;
        if (seen.has(node) || seen.size > 60)
            return (0, exports.unknown)();
        this.model.work.charge(seen.size + 1);
        seen = new Set(seen);
        seen.add(node);
        const ev = (value) => this.evaluate(value, env, seen);
        // Bound arguments are still mutable inside the callee. Check writes before an
        // environment hit can return a stale object/method supplied by its caller.
        if (this.isBinding(node) && this.reassigned(node))
            return (0, exports.unknown)();
        if (env.has(node))
            return this.maskWrites(node, env.get(node));
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
            for (const prop of this.objectStructure(node).properties) {
                this.model.work.charge();
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
            return this.maskWrites(node, result);
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
                const values = [];
                if (body && ts_morph_1.Node.isBlock(body))
                    for (const statement of this.ownedReturns(callable, body)) {
                        this.model.work.reserveArrayReferences(1);
                        values.push(this.evaluate(ts_morph_1.Node.isReturnStatement(statement) ? statement.getExpression() : undefined, next, seen));
                    }
                else if (body) {
                    this.model.work.reserveArrayReferences(1);
                    values.push(this.evaluate(body, next, seen));
                }
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
            return node.getInitializer() ? this.maskWrites(node, ev(node.getInitializer())) : (0, exports.unknown)('getName' in node ? node.getName() : 'param');
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
                for (const element of this.patternStructure(name).elements) {
                    this.model.work.charge();
                    if (!ts_morph_1.Node.isBindingElement(element))
                        continue;
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
    isBinding(node) {
        return ts_morph_1.Node.isVariableDeclaration(node) || ts_morph_1.Node.isPropertyDeclaration(node) || ts_morph_1.Node.isParameterDeclaration(node) || ts_morph_1.Node.isBindingElement(node);
    }
    aliasKey(node) { return node.getSymbol()?.compilerSymbol ?? node; }
    aliasRoot(key) {
        this.model.work.charge();
        if (!this.aliasParents.has(key)) {
            this.aliasParents.set(key, key);
            return key;
        }
        let root = key;
        while (this.aliasParents.get(root) !== root) {
            this.model.work.charge();
            root = this.aliasParents.get(root);
        }
        while (key !== root) {
            this.model.work.charge();
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
        this.model.work.charge();
        if (this.memberWrites)
            return;
        const writes = [];
        const aliases = [];
        for (const file of this.model.files) {
            this.model.work.charge(file.getFullWidth() + 1);
            for (const imp of file.getImportDeclarations()) {
                this.model.work.charge(imp.getFullWidth() + 1);
                const target = this.model.module(file, imp.getModuleSpecifierValue());
                if (!target)
                    continue;
                for (const named of imp.getNamedImports()) {
                    this.model.work.charge(named.getFullWidth() + 1);
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
                this.model.work.charge();
                if (this.isBinding(node)) {
                    this.model.work.reserveArrayReferences(1);
                    aliases.push(node);
                }
                if (ts_morph_1.Node.isBinaryExpression(node) && node.getOperatorToken().getKind() >= ts_morph_1.SyntaxKind.FirstAssignment && node.getOperatorToken().getKind() <= ts_morph_1.SyntaxKind.LastAssignment) {
                    this.model.work.reserveArrayReferences(1);
                    writes.push({ target: node.getLeft(), member: !ts_morph_1.Node.isIdentifier(node.getLeft()) });
                }
                else if ((ts_morph_1.Node.isPrefixUnaryExpression(node) || ts_morph_1.Node.isPostfixUnaryExpression(node)) && [ts_morph_1.SyntaxKind.PlusPlusToken, ts_morph_1.SyntaxKind.MinusMinusToken].includes(node.getOperatorToken())) {
                    this.model.work.reserveArrayReferences(1);
                    writes.push({ target: node.getOperand(), member: !ts_morph_1.Node.isIdentifier(node.getOperand()) });
                }
                else if (ts_morph_1.Node.isDeleteExpression(node)) {
                    this.model.work.reserveArrayReferences(1);
                    writes.push({ target: node.getExpression(), member: !ts_morph_1.Node.isIdentifier(node.getExpression()) });
                }
                else if (ts_morph_1.Node.isCallExpression(node)) {
                    this.model.work.charge(node.getExpression().getFullWidth() + 1);
                    if (node.getExpression().getText() === 'Object.assign') {
                        this.model.work.charge(node.getFullWidth() + 1);
                        const target = node.getArguments()[0];
                        if (target) {
                            this.model.work.reserveArrayReferences(1);
                            writes.push({ target, member: true, objectTarget: true });
                        }
                    }
                }
            });
        }
        const identities = new Map();
        const restSources = new Map();
        const uncertainIdentities = new Set();
        const selectedMember = (receiver, name, seen) => {
            this.model.work.charge(seen.size + 1);
            const source = identity(receiver, seen);
            if (uncertainIdentities.has(receiver))
                return { node: source, uncertain: true };
            if (seen.has(source))
                return undefined;
            this.model.work.charge(seen.size + 1);
            seen = new Set(seen);
            seen.add(source);
            if (ts_morph_1.Node.isSourceFile(source)) {
                const member = this.model.exported(source, name);
                return member ? { node: member } : undefined;
            }
            this.model.work.charge(name.length + 1);
            if (ts_morph_1.Node.isArrayLiteralExpression(source)) {
                const structure = this.arrayStructure(source);
                // Keep the original all-indices uncertainty whenever any spread is present.
                if (structure.spread)
                    return { node: source, uncertain: true };
                const member = /^(?:0|[1-9]\d*)$/.test(name) ? structure.elements[Number(name)] : undefined;
                return member ? { node: member } : undefined;
            }
            if (ts_morph_1.Node.isClassDeclaration(source)) {
                const member = this.model.classProperty(source, name);
                return member ? { node: member } : undefined;
            }
            if (!ts_morph_1.Node.isObjectLiteralExpression(source))
                return undefined;
            const structure = this.objectStructure(source);
            if (structure.spread)
                return { node: source, uncertain: true };
            const selected = structure.members.get(name);
            if (structure.unknownPosition >= (selected?.position ?? -1))
                return structure.unknownPosition >= 0 ? { node: source, uncertain: true } : undefined;
            const property = selected?.property;
            if (property && ts_morph_1.Node.isPropertyAssignment(property)) {
                const member = property.getInitializer();
                return member ? { node: member } : undefined;
            }
            if (property && ts_morph_1.Node.isShorthandPropertyAssignment(property)) {
                this.model.work.charge(property.getFullWidth() + 1);
                const member = property.getValueSymbol()?.getDeclarations().find(declaration => this.admitted(declaration));
                return member ? { node: member } : undefined;
            }
            return undefined;
        };
        // Resolve declaration/member identity, not runtime values. A parameter remains a
        // stable identity even when no invocation value exists in the indexed AST.
        const identity = (node, seen = new Set()) => {
            this.model.work.charge(seen.size + 1);
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
                    this.model.work.charge();
                    const pattern = element.getParentOrThrow();
                    const name = element.getPropertyNameNode() ?? element.getNameNode();
                    if (element.getDotDotDotToken())
                        rest = true;
                    else {
                        this.model.work.reserveArrayReferences(1);
                        path.push(ts_morph_1.Node.isArrayBindingPattern(pattern) ? String(this.patternStructure(pattern).positions.get(element) ?? -1) : this.memberName(name));
                    }
                    const owner = pattern.getParentOrThrow();
                    if (ts_morph_1.Node.isBindingElement(owner)) {
                        element = owner;
                        continue;
                    }
                    if (ts_morph_1.Node.isVariableDeclaration(owner))
                        source = owner.getInitializer();
                    break; // Destructured parameters retain separate invocation-bound identities.
                }
                for (let index = path.length - 1; index >= 0; index--) {
                    this.model.work.charge(seen.size + 1);
                    const name = path[index];
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
            this.model.work.charge();
            const target = identity(alias);
            this.joinAliases(this.aliasKey(alias), this.aliasKey(target));
            if ((ts_morph_1.Node.isParameterDeclaration(alias) || ts_morph_1.Node.isBindingElement(alias)) && alias.getInitializer())
                this.joinAliases(this.aliasKey(alias), this.aliasKey(identity(alias.getInitializer())));
        }
        // Containment is directional: writes through a container may affect referenced
        // children, but writing one child cannot mutate the container's other values.
        const forEachContained = (container, visit) => {
            this.model.work.charge();
            const rest = restSources.get(container);
            if (rest) {
                visit(rest);
                return;
            }
            if (ts_morph_1.Node.isArrayLiteralExpression(container)) {
                for (const element of this.arrayStructure(container).elements) {
                    this.model.work.charge();
                    visit(ts_morph_1.Node.isSpreadElement(element) ? element.getExpression() : element);
                }
            }
            else if (ts_morph_1.Node.isObjectLiteralExpression(container))
                for (const property of this.objectStructure(container).properties) {
                    this.model.work.charge();
                    if (ts_morph_1.Node.isPropertyAssignment(property)) {
                        const value = property.getInitializer();
                        if (value)
                            visit(value);
                    }
                    else if (ts_morph_1.Node.isShorthandPropertyAssignment(property)) {
                        const value = property.getValueSymbol()?.getDeclarations().find(declaration => this.admitted(declaration));
                        if (value)
                            visit(value);
                    }
                    else if (ts_morph_1.Node.isSpreadAssignment(property))
                        visit(property.getExpression());
                }
        };
        this.memberWrites = new Map();
        const record = (reference, name, deep, seen = new Set()) => {
            this.model.work.charge();
            const source = identity(reference), key = this.aliasRoot(this.aliasKey(source));
            const entry = this.memberWrites.get(key) ?? { names: new Set(), all: false, deep: false };
            if (name === undefined)
                entry.all = true;
            else
                entry.names.add(name);
            entry.deep || (entry.deep = deep);
            this.memberWrites.set(key, entry);
            if (!deep || seen.has(source))
                return;
            seen.add(source);
            forEachContained(source, child => record(child, name, true, seen));
        };
        for (const { target, member, objectTarget } of writes) {
            this.model.work.charge();
            // Rebinding changes the binding; an exact member write changes its receiver's
            // property. Ordinary ancestors and captured child values are not mutated.
            if (!member) {
                this.writtenBindings.add(this.aliasKey(target));
                continue;
            }
            let receiver = target, name;
            if (!objectTarget && (ts_morph_1.Node.isPropertyAccessExpression(target) || ts_morph_1.Node.isElementAccessExpression(target))) {
                receiver = target.getExpression();
                const argument = ts_morph_1.Node.isElementAccessExpression(target) ? target.getArgumentExpression() : undefined;
                name = ts_morph_1.Node.isPropertyAccessExpression(target) ? target.getName() : argument && (ts_morph_1.Node.isStringLiteral(argument) || ts_morph_1.Node.isNumericLiteral(argument)) ? String(argument.getLiteralValue()) : undefined;
                // A class field is itself a binding. Replacing it cannot reuse its initializer.
                for (const declaration of target.getSymbol()?.getDeclarations() ?? [])
                    if (ts_morph_1.Node.isPropertyDeclaration(declaration) && this.admitted(declaration))
                        this.writtenBindings.add(this.aliasKey(declaration));
            }
            identity(receiver);
            record(receiver, name, uncertainIdentities.has(receiver));
        }
    }
    reassigned(node) {
        this.indexWrites();
        return this.writtenBindings.has(this.aliasKey(node));
    }
    /** Mask properties that may have changed, keeping independent siblings/captured
     * primitives intact. Deep masking is only for an uncertain selected receiver. */
    maskWrites(node, value) {
        this.indexWrites();
        const writes = this.memberWrites.get(this.aliasRoot(this.aliasKey(node)));
        if (!writes || !(0, exports.isObject)(value))
            return value;
        const mask = (object, seen = new Set()) => {
            if (seen.has(object) || seen.size >= 60)
                return (0, exports.unknown)();
            seen = new Set(seen);
            seen.add(object);
            const properties = new Map();
            for (const [name, member] of object.properties)
                properties.set(name, writes.all || writes.names.has(name) ? (0, exports.unknown)(name) : writes.deep && (0, exports.isObject)(member) ? mask(member, seen) : member);
            for (const name of writes.names)
                if (!properties.has(name))
                    properties.set(name, (0, exports.unknown)(name));
            return { kind: 'object', properties, unknownKeys: object.unknownKeys || writes.all };
        };
        return mask(value);
    }
}
exports.StaticEvaluator = StaticEvaluator;
