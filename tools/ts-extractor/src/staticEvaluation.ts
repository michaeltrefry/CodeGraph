import { Node, SyntaxKind, ArrayLiteralExpression, ArrayBindingPattern, ObjectBindingPattern, ObjectLiteralExpression } from 'ts-morph';
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
  private readonly aliasParents = new Map<object, object>();
  private memberWrites: Map<object, { names: Set<string>; all: boolean; deep: boolean }> | undefined;
  private readonly writtenBindings = new Set<object>();
  private readonly arrays = new Map<ArrayLiteralExpression, { elements: readonly Node[]; spread: boolean }>();
  private readonly patterns = new Map<ArrayBindingPattern | ObjectBindingPattern, { elements: readonly Node[]; positions: Map<Node, number> }>();
  private readonly objects = new Map<ObjectLiteralExpression, { properties: readonly Node[]; members: Map<string, { property: Node; position: number }>; unknownPosition: number; spread: boolean }>();
  private readonly returns = new Map<Node, readonly Node[]>();
  /** Cache source structure only. Values and identity resolution still use each caller's environment. */
  private arrayStructure(node: ArrayLiteralExpression) {
    this.model.work.charge();
    const cached = this.arrays.get(node); if (cached) return cached;
    this.model.work.reserveArrayReferences(node.compilerNode.elements.length);
    const elements = node.getElements(); let spread = false;
    for (const element of elements) { this.model.work.charge(); spread ||= Node.isSpreadElement(element); }
    const result = { elements, spread }; this.arrays.set(node, result); return result;
  }
  private patternStructure(node: ArrayBindingPattern | ObjectBindingPattern) {
    this.model.work.charge();
    const cached = this.patterns.get(node); if (cached) return cached;
    const count = node.compilerNode.elements.length;
    this.model.work.reserveArrayReferences(count * 2);
    const elements = node.getElements(), positions = new Map<Node, number>();
    for (let index = 0; index < elements.length; index++) { this.model.work.charge(); positions.set(elements[index], index); }
    const result = { elements, positions }; this.patterns.set(node, result); return result;
  }
  private memberName(node: Node): string | undefined {
    this.model.work.charge(node.getFullWidth() + 1);
    if (Node.isComputedPropertyName(node)) {
      const expression = node.getExpression();
      return Node.isStringLiteral(expression) || Node.isNoSubstitutionTemplateLiteral(expression) ? expression.getLiteralText() : Node.isNumericLiteral(expression) ? String(expression.getLiteralValue()) : undefined;
    }
    return Node.isStringLiteral(node) || Node.isNoSubstitutionTemplateLiteral(node) ? node.getLiteralText() : Node.isNumericLiteral(node) ? String(node.getLiteralValue()) : node.getText();
  }
  private objectStructure(node: ObjectLiteralExpression) {
    this.model.work.charge();
    const cached = this.objects.get(node); if (cached) return cached;
    this.model.work.reserveArrayReferences(node.compilerNode.properties.length * 2);
    const properties = node.getProperties(), members = new Map<string, { property: Node; position: number }>();
    let unknownPosition = -1, spread = false;
    for (let position = 0; position < properties.length; position++) {
      const property = properties[position]; this.model.work.charge();
      if (Node.isSpreadAssignment(property)) spread = true;
      else if (Node.isPropertyAssignment(property) || Node.isShorthandPropertyAssignment(property)) {
        const key = this.memberName(property.getNameNode());
        if (key === undefined) unknownPosition = position;
        else members.set(key, { property, position }); // Last source property wins.
      }
    }
    const result = { properties, members, unknownPosition, spread }; this.objects.set(node, result); return result;
  }
  private ownedReturns(callable: Node, body: Node): readonly Node[] {
    this.model.work.charge();
    const cached = this.returns.get(callable); if (cached) return cached;
    this.model.work.charge(body.getFullWidth() + 1);
    const output: Node[] = [];
    body.forEachDescendant(node => {
      this.model.work.charge();
      if (Node.isReturnStatement(node) && this.model.owner(node) === this.model.callables.get(callable)) {
        this.model.work.reserveArrayReferences(1); output.push(node);
      }
    });
    this.returns.set(callable, output); return output;
  }
  constructor(readonly model: SemanticModel, readonly diagnostic: (message: string) => void) {}

  evaluate(node: Node | undefined, env: StaticEnvironment = new Map(), seen = new Set<Node>()): StaticValue {
    this.model.work.charge();
    if (!node) return undefined;
    if (seen.has(node) || seen.size > 60) return unknown();
    this.model.work.charge(seen.size + 1); seen = new Set(seen); seen.add(node);
    const ev = (value: Node | undefined) => this.evaluate(value, env, seen);
    // Bound arguments are still mutable inside the callee. Check writes before an
    // environment hit can return a stale object/method supplied by its caller.
    if (this.isBinding(node) && this.reassigned(node)) return unknown();
    if (env.has(node)) return this.maskWrites(node,env.get(node));
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
      for (const prop of this.objectStructure(node).properties) {
        this.model.work.charge();
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
      return this.maskWrites(node,result);
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
        const values: StaticValue[] = [];
        if (body && Node.isBlock(body)) for (const statement of this.ownedReturns(callable, body)) {
          this.model.work.reserveArrayReferences(1);
          values.push(this.evaluate(Node.isReturnStatement(statement) ? statement.getExpression() : undefined, next, seen));
        } else if (body) { this.model.work.reserveArrayReferences(1); values.push(this.evaluate(body, next, seen)); }
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
      return node.getInitializer() ? this.maskWrites(node,ev(node.getInitializer())) : unknown('getName' in node ? node.getName() : 'param');
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
      if (Node.isObjectBindingPattern(name)) for (const element of this.patternStructure(name).elements) {
        this.model.work.charge(); if (!Node.isBindingElement(element)) continue;
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
  private isBinding(node: Node): boolean {
    return Node.isVariableDeclaration(node) || Node.isPropertyDeclaration(node) || Node.isParameterDeclaration(node) || Node.isBindingElement(node);
  }
  private aliasKey(node: Node): object { return node.getSymbol()?.compilerSymbol ?? node; }
  private aliasRoot(key: object): object {
    this.model.work.charge();
    if (!this.aliasParents.has(key)) { this.aliasParents.set(key,key); return key; }
    let root=key;
    while (this.aliasParents.get(root)! !== root) { this.model.work.charge(); root=this.aliasParents.get(root)!; }
    while (key !== root) { this.model.work.charge(); const parent=this.aliasParents.get(key)!; this.aliasParents.set(key,root); key=parent; }
    return root;
  }
  private joinAliases(left: object, right: object): void {
    const a=this.aliasRoot(left), b=this.aliasRoot(right); if (a !== b) this.aliasParents.set(a,b);
  }
  /** One admitted-AST write/alias index per extraction, rather than a full fleet scan
   * per evaluated binding. Static aliases share a conservative mutable root; symbols
   * and explicit import resolution keep unrelated names and modules independent. */
  private indexWrites(): void {
    this.model.work.charge();
    if (this.memberWrites) return;
    const writes: Array<{ target: Node; member: boolean; objectTarget?: true }> = [];
    const aliases: Node[] = [];
    for (const file of this.model.files) {
      this.model.work.charge(file.getFullWidth() + 1);
      for (const imp of file.getImportDeclarations()) {
        this.model.work.charge(imp.getFullWidth() + 1);
        const target = this.model.module(file, imp.getModuleSpecifierValue()); if (!target) continue;
        for (const named of imp.getNamedImports()) {
          this.model.work.charge(named.getFullWidth() + 1);
          const exported = this.model.exported(target,named.getName());
          if (exported) this.joinAliases(this.aliasKey(named.getAliasNode() ?? named.getNameNode()),this.aliasKey(exported));
        }
        const local=imp.getDefaultImport(); const exported=local ? this.model.exported(target,'default') : undefined;
        if (local && exported) this.joinAliases(this.aliasKey(local),this.aliasKey(exported));
      }
      file.forEachDescendant(node => {
        this.model.work.charge();
        if (this.isBinding(node)) { this.model.work.reserveArrayReferences(1); aliases.push(node); }
        if (Node.isBinaryExpression(node) && node.getOperatorToken().getKind() >= SyntaxKind.FirstAssignment && node.getOperatorToken().getKind() <= SyntaxKind.LastAssignment) { this.model.work.reserveArrayReferences(1); writes.push({target:node.getLeft(),member:!Node.isIdentifier(node.getLeft())}); }
        else if ((Node.isPrefixUnaryExpression(node) || Node.isPostfixUnaryExpression(node)) && [SyntaxKind.PlusPlusToken,SyntaxKind.MinusMinusToken].includes(node.getOperatorToken())) { this.model.work.reserveArrayReferences(1); writes.push({target:node.getOperand(),member:!Node.isIdentifier(node.getOperand())}); }
        else if (Node.isDeleteExpression(node)) { this.model.work.reserveArrayReferences(1); writes.push({target:node.getExpression(),member:!Node.isIdentifier(node.getExpression())}); }
        else if (Node.isCallExpression(node)) {
          this.model.work.charge(node.getExpression().getFullWidth() + 1);
          if (node.getExpression().getText()==='Object.assign') {
            this.model.work.charge(node.getFullWidth() + 1); const target = node.getArguments()[0];
            if (target) { this.model.work.reserveArrayReferences(1); writes.push({target,member:true,objectTarget:true}); }
          }
        }
      });
    }
    const identities = new Map<Node, Node>();
    const restSources = new Map<Node, Node>();
    const uncertainIdentities = new Set<Node>();
    const selectedMember = (receiver: Node, name: string, seen: Set<Node>): {node: Node; uncertain?: true} | undefined => {
      this.model.work.charge(seen.size + 1);
      const source=identity(receiver,seen);
      if (uncertainIdentities.has(receiver)) return {node:source,uncertain:true};
      if (seen.has(source)) return undefined;
      this.model.work.charge(seen.size + 1); seen=new Set(seen); seen.add(source);
      if (Node.isSourceFile(source)) { const member=this.model.exported(source,name); return member ? {node:member} : undefined; }
      this.model.work.charge(name.length + 1);
      if (Node.isArrayLiteralExpression(source)) {
        const structure = this.arrayStructure(source);
        // Keep the original all-indices uncertainty whenever any spread is present.
        if (structure.spread) return { node: source, uncertain: true };
        const member = /^(?:0|[1-9]\d*)$/.test(name) ? structure.elements[Number(name)] : undefined;
        return member ? { node: member } : undefined;
      }
      if (Node.isClassDeclaration(source)) { const member = this.model.classProperty(source, name); return member ? { node: member } : undefined; }
      if (!Node.isObjectLiteralExpression(source)) return undefined;
      const structure = this.objectStructure(source);
      if (structure.spread) return { node: source, uncertain: true };
      const selected = structure.members.get(name);
      if (structure.unknownPosition >= (selected?.position ?? -1)) return structure.unknownPosition >= 0 ? { node: source, uncertain: true } : undefined;
      const property = selected?.property;
      if (property && Node.isPropertyAssignment(property)) { const member = property.getInitializer(); return member ? { node: member } : undefined; }
      if (property && Node.isShorthandPropertyAssignment(property)) {
        this.model.work.charge(property.getFullWidth() + 1);
        const member = property.getValueSymbol()?.getDeclarations().find(declaration => this.admitted(declaration));
        return member ? { node: member } : undefined;
      }
      return undefined;
    };
    // Resolve declaration/member identity, not runtime values. A parameter remains a
    // stable identity even when no invocation value exists in the indexed AST.
    const identity = (node: Node, seen = new Set<Node>()): Node => {
      this.model.work.charge(seen.size + 1);
      if (seen.has(node) || seen.size >= 60) return node;
      const cached=identities.get(node); if (cached) return cached;
      seen=new Set(seen); seen.add(node);
      let target: Node | undefined; let uncertain=false;
      if (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isNonNullExpression(node) || Node.isTypeAssertion(node)) target=node.getExpression();
      else if (Node.isVariableDeclaration(node) || Node.isPropertyDeclaration(node)) target=node.getInitializer();
      else if (Node.isBindingElement(node)) {
        const path: Array<string | undefined>=[]; let element=node; let source: Node | undefined; let rest=false;
        for (;;) {
          this.model.work.charge();
          const pattern=element.getParentOrThrow();
          const name=element.getPropertyNameNode() ?? element.getNameNode();
          if (element.getDotDotDotToken()) rest=true;
          else { this.model.work.reserveArrayReferences(1); path.push(Node.isArrayBindingPattern(pattern) ? String(this.patternStructure(pattern).positions.get(element) ?? -1) : this.memberName(name)); }
          const owner=pattern.getParentOrThrow();
          if (Node.isBindingElement(owner)) { element=owner; continue; }
          if (Node.isVariableDeclaration(owner)) source=owner.getInitializer();
          break; // Destructured parameters retain separate invocation-bound identities.
        }
        for (let index = path.length - 1; index >= 0; index--) {
          this.model.work.charge(seen.size + 1); const name = path[index];
          if (!source) break;
          const receiver=source;
          const selected=name===undefined ? undefined : selectedMember(receiver,name,new Set(seen));
          if (!selected || selected.uncertain) { source=selected?.node ?? identity(receiver,new Set(seen)); uncertain=true; break; }
          source=selected.node;
        }
        // Rest creates a new container with shifted/omitted members. Preserve its
        // source for conservative write invalidation, without inventing an index map.
        if (rest && source) { restSources.set(node,source); uncertain=true; }
        else target=source ?? node.getInitializer();
      } else if (Node.isImportSpecifier(node) || Node.isImportClause(node) || Node.isNamespaceImport(node)) {
        const declaration=node.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
        const file=this.model.module(node,declaration.getModuleSpecifierValue());
        target=file && (Node.isNamespaceImport(node) ? file : this.model.exported(file,Node.isImportSpecifier(node) ? node.getName() : 'default'));
      } else if (Node.isIdentifier(node)) {
        const declarations=node.getSymbol()?.getDeclarations().filter(declaration=>this.admitted(declaration)) ?? [];
        if (declarations.length===1) target=declarations[0];
      } else if (Node.isPropertyAccessExpression(node) || Node.isElementAccessExpression(node)) {
        const argument=Node.isElementAccessExpression(node) ? node.getArgumentExpression() : undefined;
        const name=Node.isPropertyAccessExpression(node) ? node.getName() : argument && (Node.isStringLiteral(argument) || Node.isNumericLiteral(argument)) ? String(argument.getLiteralValue()) : undefined;
        const selected=name===undefined ? undefined : selectedMember(node.getExpression(),name,new Set(seen));
        target=selected?.node; uncertain=selected?.uncertain ?? false;
        // Unknown members conservatively refer to their own receiver, never all
        // siblings of an object that merely contains that receiver.
        target ??= node.getSymbol()?.getDeclarations().find(declaration=>this.admitted(declaration) && Node.isPropertyDeclaration(declaration));
        if (!target) { target=node.getExpression(); uncertain=true; }
      }
      const result=target && target!==node ? identity(target,seen) : node;
      if (uncertain || target && uncertainIdentities.has(target)) uncertainIdentities.add(node);
      identities.set(node,result); return result;
    };
    for (const alias of aliases) {
      this.model.work.charge();
      const target=identity(alias); this.joinAliases(this.aliasKey(alias),this.aliasKey(target));
      if ((Node.isParameterDeclaration(alias) || Node.isBindingElement(alias)) && alias.getInitializer()) this.joinAliases(this.aliasKey(alias),this.aliasKey(identity(alias.getInitializer()!)));
    }
    // Containment is directional: writes through a container may affect referenced
    // children, but writing one child cannot mutate the container's other values.
    const forEachContained = (container: Node, visit: (child: Node) => void): void => {
      this.model.work.charge();
      const rest = restSources.get(container); if (rest) { visit(rest); return; }
      if (Node.isArrayLiteralExpression(container)) {
        for (const element of this.arrayStructure(container).elements) {
          this.model.work.charge(); visit(Node.isSpreadElement(element) ? element.getExpression() : element);
        }
      } else if (Node.isObjectLiteralExpression(container)) for (const property of this.objectStructure(container).properties) {
        this.model.work.charge();
        if (Node.isPropertyAssignment(property)) { const value = property.getInitializer(); if (value) visit(value); }
        else if (Node.isShorthandPropertyAssignment(property)) {
          const value = property.getValueSymbol()?.getDeclarations().find(declaration => this.admitted(declaration)); if (value) visit(value);
        } else if (Node.isSpreadAssignment(property)) visit(property.getExpression());
      }
    };
    this.memberWrites=new Map();
    const record = (reference: Node, name: string | undefined, deep: boolean, seen = new Set<Node>()): void => {
      this.model.work.charge();
      const source=identity(reference), key=this.aliasRoot(this.aliasKey(source));
      const entry=this.memberWrites!.get(key) ?? {names:new Set<string>(),all:false,deep:false};
      if (name===undefined) entry.all=true; else entry.names.add(name);
      entry.deep ||= deep; this.memberWrites!.set(key,entry);
      if (!deep || seen.has(source)) return;
      seen.add(source);
      forEachContained(source, child => record(child,name,true,seen));
    };
    for (const {target,member,objectTarget} of writes) {
      this.model.work.charge();
      // Rebinding changes the binding; an exact member write changes its receiver's
      // property. Ordinary ancestors and captured child values are not mutated.
      if (!member) { this.writtenBindings.add(this.aliasKey(target)); continue; }
      let receiver=target, name: string | undefined;
      if (!objectTarget && (Node.isPropertyAccessExpression(target) || Node.isElementAccessExpression(target))) {
        receiver=target.getExpression();
        const argument=Node.isElementAccessExpression(target) ? target.getArgumentExpression() : undefined;
        name=Node.isPropertyAccessExpression(target) ? target.getName() : argument && (Node.isStringLiteral(argument) || Node.isNumericLiteral(argument)) ? String(argument.getLiteralValue()) : undefined;
        // A class field is itself a binding. Replacing it cannot reuse its initializer.
        for (const declaration of target.getSymbol()?.getDeclarations() ?? []) if (Node.isPropertyDeclaration(declaration) && this.admitted(declaration)) this.writtenBindings.add(this.aliasKey(declaration));
      }
      identity(receiver);
      record(receiver,name,uncertainIdentities.has(receiver));
    }
  }
  private reassigned(node: Node): boolean {
    this.indexWrites(); return this.writtenBindings.has(this.aliasKey(node));
  }
  /** Mask properties that may have changed, keeping independent siblings/captured
   * primitives intact. Deep masking is only for an uncertain selected receiver. */
  private maskWrites(node: Node, value: StaticValue): StaticValue {
    this.indexWrites();
    const writes=this.memberWrites!.get(this.aliasRoot(this.aliasKey(node)));
    if (!writes || !isObject(value)) return value;
    const mask = (object: ObjectValue, seen = new Set<ObjectValue>()): StaticValue => {
      if (seen.has(object) || seen.size >= 60) return unknown();
      seen=new Set(seen); seen.add(object);
      const properties=new Map<string,StaticValue>();
      for (const [name, member] of object.properties) properties.set(name,writes.all || writes.names.has(name) ? unknown(name) : writes.deep && isObject(member) ? mask(member,seen) : member);
      for (const name of writes.names) if (!properties.has(name)) properties.set(name,unknown(name));
      return {kind:'object',properties,unknownKeys:object.unknownKeys || writes.all};
    };
    return mask(value);
  }
}
