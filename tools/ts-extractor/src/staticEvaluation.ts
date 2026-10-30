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
  private readonly mutations = new Map<Node, boolean>();
  private readonly aliasParents = new Map<object, object>();
  private writtenAliases: Set<object> | undefined;
  private readonly writtenBindings = new Set<object>();
  constructor(readonly model: SemanticModel, readonly diagnostic: (message: string) => void) {}

  evaluate(node: Node | undefined, env: StaticEnvironment = new Map(), seen = new Set<Node>()): StaticValue {
    if (!node) return undefined;
    if (seen.has(node) || seen.size > 60) return unknown();
    seen = new Set(seen); seen.add(node);
    const ev = (value: Node | undefined) => this.evaluate(value, env, seen);
    // Bound arguments are still mutable inside the callee. Check writes before an
    // environment hit can return a stale object/method supplied by its caller.
    if (this.isBinding(node) && this.mutated(node)) return unknown();
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
          if (typeof key === 'string') result.properties.set(key, ev(prop.getInitializer()));
          else { for (const key of result.properties.keys()) result.properties.set(key, unknown()); result.unknownKeys = true; }
        } else if (Node.isShorthandPropertyAssignment(prop)) result.properties.set(prop.getName(), ev(prop.getValueSymbol()?.getDeclarations().find(d => this.admitted(d))));
      }
      return result;
    }
    if (Node.isPropertyAccessExpression(node) || Node.isElementAccessExpression(node)) {
      const key = Node.isPropertyAccessExpression(node) ? node.getName() : ev(node.getArgumentExpression());
      const receiver = ev(node.getExpression());
      if (typeof key === 'string' && isObject(receiver)) return receiver.properties.has(key) ? receiver.properties.get(key) : receiver.unknownKeys ? unknown(key) : undefined;
      if (key === 'href' && typeof receiver === 'string' && this.urlExpression(node.getExpression())) return receiver;
      // A default object's inferred type does not prove a parameter's runtime property.
      // Follow declaration properties only for static class/enum/namespace ownership or this.
      const expression = node.getExpression();
      const owner = this.model.resolve(expression);
      const staticOwner = expression.getKind() === SyntaxKind.ThisKeyword ||
        !!owner && (Node.isClassDeclaration(owner) || Node.isEnumDeclaration(owner)) ||
        (expression.getSymbol()?.getDeclarations() ?? []).some(Node.isNamespaceImport);
      if (staticOwner) {
        const declaration = node.getSymbol()?.getDeclarations().find(d => this.admitted(d));
        if (declaration) return ev(declaration);
        const resolved = this.model.resolve(node);
        if (resolved && resolved !== node) return ev(resolved);
      }
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
        const value = ev(receiver);
        if (typeof value === 'string') return value;
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
  private text(value: StaticValue): string { return isUnknown(value) || isObject(value) ? '{param}' : String(value); }
  private urlExpression(node: Node, seen = new Set<Node>()): boolean {
    if (seen.has(node) || seen.size > 30) return false; seen.add(node);
    if (Node.isNewExpression(node)) return this.builtin(node.getExpression(), 'URL');
    if (Node.isVariableDeclaration(node) || Node.isPropertyDeclaration(node)) { const init=node.getInitializer(); return !!init && this.urlExpression(init, seen); }
    const declarations=node.getSymbol()?.getDeclarations().filter(d => this.admitted(d)) ?? [];
    return declarations.length === 1 && this.urlExpression(declarations[0], seen);
  }
  private assignmentTargets(node: Node): Node[] {
    return Node.isPropertyAccessExpression(node) || Node.isElementAccessExpression(node) ? [node, ...this.assignmentTargets(node.getExpression())] : [node];
  }
  private isBinding(node: Node): boolean {
    return Node.isVariableDeclaration(node) || Node.isPropertyDeclaration(node) || Node.isParameterDeclaration(node) || Node.isBindingElement(node);
  }
  private aliasKey(node: Node): object { return node.getSymbol()?.compilerSymbol ?? node; }
  private aliasRoot(key: object): object {
    if (!this.aliasParents.has(key)) { this.aliasParents.set(key,key); return key; }
    let root=key;
    while (this.aliasParents.get(root)! !== root) root=this.aliasParents.get(root)!;
    while (key !== root) { const parent=this.aliasParents.get(key)!; this.aliasParents.set(key,root); key=parent; }
    return root;
  }
  private joinAliases(left: object, right: object): void {
    const a=this.aliasRoot(left), b=this.aliasRoot(right); if (a !== b) this.aliasParents.set(a,b);
  }
  /** One admitted-AST write/alias index per extraction, rather than a full fleet scan
   * per evaluated binding. Static aliases share a conservative mutable root; symbols
   * and explicit import resolution keep unrelated names and modules independent. */
  private indexWrites(): void {
    if (this.writtenAliases) return;
    const writes: Array<{ target: Node; member: boolean }> = [];
    const aliases: Node[] = [];
    const containers: Node[] = [];
    for (const file of this.model.files) {
      for (const imp of file.getImportDeclarations()) {
        const target = this.model.module(file, imp.getModuleSpecifierValue()); if (!target) continue;
        for (const named of imp.getNamedImports()) {
          const exported = this.model.exported(target,named.getName());
          if (exported) this.joinAliases(this.aliasKey(named.getAliasNode() ?? named.getNameNode()),this.aliasKey(exported));
        }
        const local=imp.getDefaultImport(); const exported=local ? this.model.exported(target,'default') : undefined;
        if (local && exported) this.joinAliases(this.aliasKey(local),this.aliasKey(exported));
      }
      file.forEachDescendant(node => {
        if (this.isBinding(node)) aliases.push(node);
        if (Node.isObjectLiteralExpression(node) || Node.isArrayLiteralExpression(node)) containers.push(node);
        if (Node.isBinaryExpression(node) && node.getOperatorToken().getKind() >= SyntaxKind.FirstAssignment && node.getOperatorToken().getKind() <= SyntaxKind.LastAssignment) writes.push({target:node.getLeft(),member:!Node.isIdentifier(node.getLeft())});
        else if ((Node.isPrefixUnaryExpression(node) || Node.isPostfixUnaryExpression(node)) && [SyntaxKind.PlusPlusToken,SyntaxKind.MinusMinusToken].includes(node.getOperatorToken())) writes.push({target:node.getOperand(),member:!Node.isIdentifier(node.getOperand())});
        else if (Node.isDeleteExpression(node)) writes.push({target:node.getExpression(),member:!Node.isIdentifier(node.getExpression())});
        else if (Node.isCallExpression(node) && node.getExpression().getText()==='Object.assign' && node.getArguments()[0]) writes.push({target:node.getArguments()[0],member:true});
      });
    }
    const connectValue = (key: object, value: Node): void => {
      if (Node.isObjectLiteralExpression(value) || Node.isArrayLiteralExpression(value)) { this.joinAliases(key,this.aliasKey(value)); return; }
      if (!(Node.isIdentifier(value) || Node.isPropertyAccessExpression(value) || Node.isElementAccessExpression(value) || Node.isParenthesizedExpression(value) || Node.isAsExpression(value) || Node.isNonNullExpression(value))) return;
      const resolved=this.model.resolve(value); if (resolved) this.joinAliases(key,this.aliasKey(resolved));
      for (const reference of this.assignmentTargets(value)) {
        // A namespace is a container of independent exports, not one mutable export.
        if (reference.getSymbol()?.getDeclarations().some(Node.isNamespaceImport)) continue;
        // Library member symbols (e.g. AbortController.signal) describe a type,
        // not one instance. Keep only admitted binding identities and receivers.
        if (reference.getSymbol()?.getDeclarations().some(declaration=>this.admitted(declaration))) this.joinAliases(key,this.aliasKey(reference));
      }
    };
    for (const alias of aliases) {
      if (!(Node.isVariableDeclaration(alias) || Node.isPropertyDeclaration(alias) || Node.isParameterDeclaration(alias) || Node.isBindingElement(alias))) continue;
      const key=this.aliasKey(alias), initializer=alias.getInitializer();
      const name=alias.getNameNode();
      // Destructured parameters are independent invocation-bound values. A write
      // through one child must not make an immutable sibling endpoint unknown.
      if (!Node.isParameterDeclaration(alias) && (Node.isObjectBindingPattern(name) || Node.isArrayBindingPattern(name))) for (const element of name.getDescendantsOfKind(SyntaxKind.BindingElement)) this.joinAliases(key,this.aliasKey(element));
      if (initializer) connectValue(key,initializer);
    }
    // Containers retain binding identity even when their member has no resolvable
    // value (notably an invocation-bound parameter). Destructuring and nested writes
    // can conservatively invalidate contained references without simulating execution.
    for (const container of containers) {
      const key=this.aliasKey(container);
      if (Node.isObjectLiteralExpression(container)) for (const property of container.getProperties()) {
        if (Node.isShorthandPropertyAssignment(property)) {
          const symbol=property.getValueSymbol()?.compilerSymbol; if (symbol) this.joinAliases(key,symbol);
        } else if (Node.isPropertyAssignment(property)) { const value=property.getInitializer(); if (value) connectValue(key,value); }
        else if (Node.isSpreadAssignment(property)) connectValue(key,property.getExpression());
      }
      else if (Node.isArrayLiteralExpression(container)) for (const element of container.getElements()) connectValue(key,Node.isSpreadElement(element) ? element.getExpression() : element);
    }
    this.writtenAliases=new Set<object>();
    for (const {target,member} of writes) {
      // Rebinding a local changes that binding, not objects that previously captured
      // its value. Member writes, unlike rebinding, invalidate the shared referent.
      if (!member) { this.writtenBindings.add(this.aliasKey(target)); continue; }
      for (const reference of this.assignmentTargets(target)) {
        const resolved=this.model.resolve(reference);
        this.writtenAliases.add(this.aliasRoot(this.aliasKey(reference)));
        if (resolved) this.writtenAliases.add(this.aliasRoot(this.aliasKey(resolved)));
      }
    }
  }
  private mutated(node: Node): boolean {
    if (this.mutations.has(node)) return this.mutations.get(node)!;
    this.indexWrites();
    const key=this.aliasKey(node);
    const mutated=this.writtenBindings.has(key) || this.writtenAliases!.has(this.aliasRoot(key));
    this.mutations.set(node,mutated); return mutated;
  }
}
