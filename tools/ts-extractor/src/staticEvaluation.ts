import { Node, SyntaxKind } from 'ts-morph';
import { SemanticModel } from './semanticModel';

export type StaticValue = string | number | boolean | null | undefined | UnknownValue | ObjectValue;
export interface UnknownValue { kind: 'unknown'; hint: string; }
export interface ObjectValue { kind: 'object'; properties: Map<string, StaticValue>; unknownKeys: boolean; }
export type StaticEnvironment = Map<Node, StaticValue>;
export const unknown = (hint = 'param'): UnknownValue => ({ kind: 'unknown', hint });
export const isUnknown = (value: StaticValue): value is UnknownValue => typeof value === 'object' && value !== null && value.kind === 'unknown';
export const isObject = (value: StaticValue): value is ObjectValue => typeof value === 'object' && value !== null && value.kind === 'object';
export const emptyObject = (): ObjectValue => ({ kind: 'object', properties: new Map(), unknownKeys: false });
export function staticKey(value: StaticValue): string {
  if (isUnknown(value)) return '?';
  if (isObject(value)) return JSON.stringify([value.unknownKeys, [...value.properties].sort(([a], [b]) => a.localeCompare(b)).map(([k, v]) => [k, staticKey(v)])]);
  return JSON.stringify(value) ?? 'undefined';
}

/** Bounded, non-executing evaluation over admitted ASTs only. Environments are keyed by
 * declarations, never identifier text; no process-global caches survive extraction. */
export class StaticEvaluator {
  constructor(readonly model: SemanticModel, readonly diagnostic: (message: string) => void) {}

  evaluate(node: Node | undefined, env: StaticEnvironment = new Map(), seen = new Set<Node>()): StaticValue {
    if (!node) return undefined;
    if (seen.has(node) || seen.size > 60) return unknown();
    seen = new Set(seen); seen.add(node);
    const ev = (value: Node | undefined) => this.evaluate(value, env, seen);
    if (env.has(node)) return env.get(node);
    if (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isNonNullExpression(node) || Node.isTypeAssertion(node) || Node.isAwaitExpression(node) || Node.isSatisfiesExpression(node)) return ev(node.getExpression());
    if (Node.isStringLiteral(node) || Node.isNoSubstitutionTemplateLiteral(node)) return node.getLiteralText();
    if (Node.isNumericLiteral(node)) return node.getLiteralValue();
    if (node.getKind() === SyntaxKind.TrueKeyword) return true;
    if (node.getKind() === SyntaxKind.FalseKeyword) return false;
    if (node.getKind() === SyntaxKind.NullKeyword) return null;
    if (Node.isIdentifier(node) && node.getText() === 'undefined' && !node.getSymbol()?.getDeclarations().some(d => this.admitted(d))) return undefined;
    if (Node.isTemplateExpression(node)) {
      let value = node.getHead().getLiteralText();
      for (const span of node.getTemplateSpans()) value += this.text(ev(span.getExpression())) + span.getLiteral().getLiteralText();
      return value;
    }
    if (Node.isBinaryExpression(node)) {
      const op = node.getOperatorToken().getKind(); const left = ev(node.getLeft());
      if (op === SyntaxKind.PlusToken) {
        const right = ev(node.getRight());
        if (typeof left === 'number' && typeof right === 'number') return left + right;
        if (typeof left === 'string' || typeof right === 'string') return this.text(left) + this.text(right);
        return unknown();
      }
      if (op === SyntaxKind.QuestionQuestionToken) return isUnknown(left) ? unknown() : left == null ? ev(node.getRight()) : left;
      if (op === SyntaxKind.BarBarToken) return isUnknown(left) ? unknown() : left ? left : ev(node.getRight());
      if (op === SyntaxKind.AmpersandAmpersandToken) return isUnknown(left) ? unknown() : left ? ev(node.getRight()) : left;
      if (op === SyntaxKind.EqualsEqualsEqualsToken || op === SyntaxKind.EqualsEqualsToken) return isUnknown(left) || isUnknown(ev(node.getRight())) ? unknown() : left === ev(node.getRight());
      if (op === SyntaxKind.ExclamationEqualsEqualsToken || op === SyntaxKind.ExclamationEqualsToken) return isUnknown(left) || isUnknown(ev(node.getRight())) ? unknown() : left !== ev(node.getRight());
      return unknown();
    }
    if (Node.isConditionalExpression(node)) {
      const condition = ev(node.getCondition());
      if (!isUnknown(condition)) return ev(condition ? node.getWhenTrue() : node.getWhenFalse());
      const yes = ev(node.getWhenTrue()), no = ev(node.getWhenFalse());
      if (staticKey(yes) === staticKey(no)) return yes;
      // A conditional query suffix cannot change endpoint identity.
      if (typeof yes === 'string' && typeof no === 'string' && [yes, no].every(v => !v || /^[?#]/.test(v))) return '?{param}';
      return unknown();
    }
    if (Node.isObjectLiteralExpression(node)) {
      const result = emptyObject();
      for (const prop of node.getProperties()) {
        if (Node.isSpreadAssignment(prop)) {
          const spread = ev(prop.getExpression());
          if (isObject(spread)) {
            if (spread.unknownKeys) { for (const key of result.properties.keys()) result.properties.set(key, unknown()); result.unknownKeys = true; }
            for (const [key, value] of spread.properties) result.properties.set(key, value);
          } else if (spread != null) {
            for (const key of result.properties.keys()) result.properties.set(key, unknown());
            result.unknownKeys = true;
          }
        } else if (Node.isPropertyAssignment(prop)) {
          const name = prop.getNameNode(); const key = Node.isComputedPropertyName(name) ? ev(name.getExpression()) : prop.getName().replace(/^['"]|['"]$/g, '');
          if (typeof key === 'string') result.properties.set(key, ev(prop.getInitializer())); else result.unknownKeys = true;
        } else if (Node.isShorthandPropertyAssignment(prop)) result.properties.set(prop.getName(), ev(prop.getNameNode()));
      }
      return result;
    }
    if (Node.isPropertyAccessExpression(node) || Node.isElementAccessExpression(node)) {
      const key = Node.isPropertyAccessExpression(node) ? node.getName() : ev(node.getArgumentExpression());
      const receiver = ev(node.getExpression());
      if (typeof key === 'string' && isObject(receiver)) return receiver.properties.has(key) ? receiver.properties.get(key) : receiver.unknownKeys ? unknown(key) : undefined;
      // Static class properties / enum members / namespace imports use declaration ownership.
      const declaration = node.getSymbol()?.getDeclarations().find(d => this.admitted(d));
      if (declaration) return ev(declaration);
      if (key === 'href' && Node.isNewExpression(node.getExpression())) return ev(node.getExpression());
      const resolved = this.model.resolve(node);
      if (resolved && resolved !== node) return ev(resolved);
      return unknown(typeof key === 'string' ? key : 'param');
    }
    if (Node.isNewExpression(node) && this.builtin(node.getExpression(), 'URL')) {
      const [url, base] = node.getArguments().map(ev);
      if (typeof url !== 'string') return unknown();
      if (/^[a-z][\w+.-]*:\/\//i.test(url)) return url;
      if (typeof base !== 'string' || base.includes('{param}')) return unknown();
      try { return new URL(url, base).href.replace(/%7Bparam%7D/gi, '{param}'); } catch { return unknown(); }
    }
    if (Node.isCallExpression(node)) {
      const expression = node.getExpression();
      if (Node.isPropertyAccessExpression(expression) && expression.getName() === 'toString' && node.getArguments().length === 0) {
        const receiver = expression.getExpression();
        if (Node.isNewExpression(receiver) && this.builtin(receiver.getExpression(), 'URL')) return ev(receiver);
      }
      // Encode known segments without executing arbitrary user code.
      if (this.builtin(expression, 'encodeURIComponent')) { const value = ev(node.getArguments()[0]); return isUnknown(value) ? unknown() : typeof value === 'string' ? encodeURIComponent(value) : unknown(); }
      const callable = this.model.resolve(expression);
      if (callable && (Node.isFunctionDeclaration(callable) || Node.isFunctionExpression(callable) || Node.isArrowFunction(callable) || Node.isMethodDeclaration(callable))) {
        const next = this.bind(callable, node.getArguments().map(ev), env);
        const body = callable.getBody();
        const values = !body ? [] : Node.isBlock(body) ? body.getDescendantsOfKind(SyntaxKind.ReturnStatement)
          .filter(r => this.model.owner(r) === this.model.callables.get(callable)).map(r => this.evaluate(r.getExpression(), next, seen)) : [this.evaluate(body, next, seen)];
        if (values.length && values.every(v => staticKey(v) === staticKey(values[0]))) return values[0];
      }
      return unknown();
    }
    if (Node.isIdentifier(node)) {
      const declarations = node.getSymbol()?.getDeclarations().filter(d => this.admitted(d)) ?? [];
      if (declarations.length === 1) return ev(declarations[0]);
      const resolved = this.model.resolve(node);
      if (resolved && resolved !== node) return ev(resolved);
      return unknown(node.getText());
    }
    if (Node.isVariableDeclaration(node) || Node.isPropertyDeclaration(node) || Node.isPropertyAssignment(node) || Node.isParameterDeclaration(node) || Node.isEnumMember(node) || Node.isBindingElement(node)) {
      if (env.has(node)) return env.get(node);
      // A reassigned binding is not a constant. Avoid using a stale initializer as proof.
      if (Node.isVariableDeclaration(node) && this.mutated(node)) return unknown(node.getName());
      return node.getInitializer() ? ev(node.getInitializer()) : unknown('getName' in node ? node.getName() : 'param');
    }
    if (Node.isImportSpecifier(node) || Node.isImportClause(node)) {
      const imp = node.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
      const file = this.model.module(node, imp.getModuleSpecifierValue());
      const declaration = file ? this.model.exported(file, Node.isImportSpecifier(node) ? node.getName() : 'default') : undefined;
      return declaration ? ev(declaration) : unknown();
    }
    return unknown();
  }

  bind(callable: Node, values: StaticValue[], outer: StaticEnvironment = new Map()): StaticEnvironment {
    const env = new Map(outer);
    if (!(Node.isFunctionDeclaration(callable) || Node.isFunctionExpression(callable) || Node.isArrowFunction(callable) || Node.isMethodDeclaration(callable) || Node.isConstructorDeclaration(callable))) return env;
    callable.getParameters().forEach((parameter, index) => {
      const value = values[index] === undefined && parameter.getInitializer() ? this.evaluate(parameter.getInitializer(), env) : values[index];
      env.set(parameter, value);
      const name = parameter.getNameNode();
      if (Node.isObjectBindingPattern(name)) for (const element of name.getElements()) {
        const key = element.getPropertyNameNode()?.getText() ?? element.getName();
        let property = isObject(value) ? value.properties.has(key) ? value.properties.get(key) : value.unknownKeys ? unknown(key) : undefined : unknown(key);
        if (property === undefined && element.getInitializer()) property = this.evaluate(element.getInitializer(), env);
        env.set(element, property);
      }
    });
    return env;
  }
  builtin(node: Node, name: string): boolean {
    return Node.isIdentifier(node) && node.getText() === name && !(node.getSymbol()?.getDeclarations() ?? []).some(d => this.admitted(d));
  }
  private admitted(node: Node): boolean { return this.model.byPath.has(this.model.relative(node)); }
  private text(value: StaticValue): string { return isUnknown(value) || isObject(value) ? '{param}' : String(value ?? ''); }
  private mutated(node: Node): boolean {
    const symbol = node.getSymbol()?.compilerSymbol;
    return !!symbol && node.getSourceFile().getDescendantsOfKind(SyntaxKind.BinaryExpression).some(binary =>
      binary.getOperatorToken().getKind() >= SyntaxKind.FirstAssignment && binary.getOperatorToken().getKind() <= SyntaxKind.LastAssignment &&
      binary.getLeft().getSymbol()?.compilerSymbol === symbol);
  }
}
