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
    }
    evaluate(node, env = new Map(), seen = new Set()) {
        if (!node)
            return undefined;
        if (seen.has(node) || seen.size > 60)
            return (0, exports.unknown)();
        seen = new Set(seen);
        seen.add(node);
        const ev = (value) => this.evaluate(value, env, seen);
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
                    else
                        result.unknownKeys = true;
                }
                else if (ts_morph_1.Node.isShorthandPropertyAssignment(prop))
                    result.properties.set(prop.getName(), ev(prop.getNameNode()));
            }
            return result;
        }
        if (ts_morph_1.Node.isPropertyAccessExpression(node) || ts_morph_1.Node.isElementAccessExpression(node)) {
            const key = ts_morph_1.Node.isPropertyAccessExpression(node) ? node.getName() : ev(node.getArgumentExpression());
            const receiver = ev(node.getExpression());
            if (typeof key === 'string' && (0, exports.isObject)(receiver))
                return receiver.properties.has(key) ? receiver.properties.get(key) : receiver.unknownKeys ? (0, exports.unknown)(key) : undefined;
            // Static class properties / enum members / namespace imports use declaration ownership.
            const declaration = node.getSymbol()?.getDeclarations().find(d => this.admitted(d));
            if (declaration)
                return ev(declaration);
            if (key === 'href' && ts_morph_1.Node.isNewExpression(node.getExpression()))
                return ev(node.getExpression());
            const resolved = this.model.resolve(node);
            if (resolved && resolved !== node)
                return ev(resolved);
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
                if (ts_morph_1.Node.isNewExpression(receiver) && this.builtin(receiver.getExpression(), 'URL'))
                    return ev(receiver);
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
            if (env.has(node))
                return env.get(node);
            // A reassigned binding is not a constant. Avoid using a stale initializer as proof.
            if (ts_morph_1.Node.isVariableDeclaration(node) && this.mutated(node))
                return (0, exports.unknown)(node.getName());
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
    text(value) { return (0, exports.isUnknown)(value) || (0, exports.isObject)(value) ? '{param}' : String(value ?? ''); }
    mutated(node) {
        const symbol = node.getSymbol()?.compilerSymbol;
        return !!symbol && node.getSourceFile().getDescendantsOfKind(ts_morph_1.SyntaxKind.BinaryExpression).some(binary => binary.getOperatorToken().getKind() >= ts_morph_1.SyntaxKind.FirstAssignment && binary.getOperatorToken().getKind() <= ts_morph_1.SyntaxKind.LastAssignment &&
            binary.getLeft().getSymbol()?.compilerSymbol === symbol);
    }
}
exports.StaticEvaluator = StaticEvaluator;
