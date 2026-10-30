import { Node, SourceFile, SyntaxKind, CallExpression } from 'ts-morph';
import * as path from 'path';
import type { GraphNodeDto, PendingEdgeDto, ResolvedImportDto, UnresolvedCallDto } from './protocol';

export function declarationQN(file: string, name: string): string {
  return [...file.replace(/\.(?:tsx?|jsx?)$/, '').replace(/\\/g, '/').split('/').filter(p => p !== 'src' && p !== ''), name].join('.');
}

export function sameSymbol(reference: Node, declaration: Node | undefined): boolean {
  const symbol = reference.getSymbol();
  return !!symbol && !!declaration && symbol.compilerSymbol === declaration.getSymbol()?.compilerSymbol;
}

export function isCallable(node: Node): boolean {
  return Node.isFunctionDeclaration(node) || Node.isFunctionExpression(node) || Node.isArrowFunction(node) ||
    Node.isMethodDeclaration(node) || Node.isGetAccessorDeclaration(node) || Node.isSetAccessorDeclaration(node) || Node.isConstructorDeclaration(node);
}

/** Shared, source-scoped symbol seam for subsequent HTTP, routes and desktop passes.
 * All resolution is bounded to the already admitted ASTs. Package dependencies remain imports,
 * not guessed same-name declarations. Explicit exports/aliases are followed without node_modules.
 */
export class SemanticModel {
  readonly declarations = new Map<Node, string>();
  readonly callables = new Map<Node, string>();
  readonly byPath = new Map<string, SourceFile>();
  readonly byQN = new Map<string, Node>();
  constructor(readonly repository: string, readonly root: string, readonly files: SourceFile[],
    readonly imports: ResolvedImportDto[], readonly diagnostic: (message: string) => void) {
    for (const file of files) this.byPath.set(this.relative(file), file);
    for (const file of files) {
      file.forEachDescendant(node => {
        if (Node.isClassDeclaration(node) || Node.isInterfaceDeclaration(node) || Node.isEnumDeclaration(node) || Node.isVariableDeclaration(node)) {
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
  relative(node: Node): string { return path.relative(this.root, node.getSourceFile().getFilePath()).replace(/\\/g, '/'); }
  fileQN(node: Node): string { return `${this.repository}:${this.relative(node)}`; }
  private identify(node: Node, qn: string): void { this.declarations.set(node, qn); this.byQN.set(qn, node); }
  private qn(node: Node): string {
    const owner = node.getAncestors().find(ancestor => isCallable(ancestor) || Node.isClassDeclaration(ancestor));
    let name = (node as unknown as { getName?: () => string | undefined }).getName?.();
    const parent = node.getParent();
    if (parent && (Node.isVariableDeclaration(parent) || Node.isPropertyAssignment(parent) || Node.isPropertyDeclaration(parent)) &&
        (Node.isArrowFunction(node) || Node.isFunctionExpression(node))) name = parent.getName();
    if (!name && parent && Node.isExportAssignment(parent)) name = 'default';
    if (!name && Node.isClassDeclaration(node) && node.isDefaultExport()) name = 'default';
    // React wrappers own the enclosed render callable under the exported variable's identity.
    if (!name && parent && Node.isCallExpression(parent)) {
      let wrapper: Node = parent;
      while (Node.isCallExpression(wrapper) && this.reactApi(wrapper)) {
        const next = wrapper.getParent();
        if (next && Node.isVariableDeclaration(next)) { name = next.getName(); break; }
        if (!next) break; wrapper = next;
      }
    }
    if (!name) {
      // AST-relative path survives unrelated edits elsewhere in the file; duplicate callbacks in
      // the same owner remain distinct without relying on line numbers or global counters.
      let current: Node = node; const segments: number[] = [];
      while (current.getParent() && current.getParent() !== owner && !Node.isSourceFile(current.getParent()!)) {
        const parentNode = current.getParentOrThrow(); segments.unshift(parentNode.getChildren().indexOf(current)); current = parentNode;
      }
      name = Node.isConstructorDeclaration(node) ? 'constructor' : `$callback${segments.join('_')}`;
    }
    const containers: string[] = [];
    for (const ancestor of node.getAncestors()) {
      if (ancestor === owner) break;
      if ((Node.isVariableDeclaration(ancestor) || Node.isPropertyAssignment(ancestor) || Node.isPropertyDeclaration(ancestor)) &&
          Node.isObjectLiteralExpression(ancestor.getInitializer())) containers.unshift(ancestor.getName());
    }
    const scopes: string[] = [];
    if (owner) for (const ancestor of node.getAncestors()) {
      if (ancestor === owner) break;
      if (!Node.isBlock(ancestor) || ancestor.getParent() === owner) continue;
      let current: Node = ancestor; const indices: number[] = [];
      while (current.getParent() && current !== owner) {
        const parentNode = current.getParentOrThrow(); indices.unshift(parentNode.getChildren().indexOf(current)); current = parentNode;
      }
      scopes.unshift(`$scope${indices.join('_')}`);
    }
    const lexicalName = [...scopes, ...containers, name].join('.');
    if (owner) return `${this.declarations.get(owner) ?? this.qn(owner)}.${lexicalName}`;
    return Node.isClassDeclaration(node) || Node.isInterfaceDeclaration(node) || Node.isEnumDeclaration(node)
      ? declarationQN(this.relative(node), lexicalName)
      : `${this.fileQN(node)}#${lexicalName}`;
  }
  owner(node: Node): string {
    for (const ancestor of node.getAncestors()) {
      const callable = this.callables.get(ancestor);
      if (callable) return callable;
    }
    return this.fileQN(node);
  }
  module(from: Node, specifier: string): SourceFile | undefined {
    const resolved = this.imports.find(item => item.filePath === this.relative(from) && item.importedNamespace === specifier);
    return resolved?.resolvedFilePath ? this.byPath.get(resolved.resolvedFilePath) : undefined;
  }
  exported(file: SourceFile, name: string, seen = new Set<string>()): Node | undefined {
    const key = `${this.relative(file)}:${name}`;
    if (seen.has(key)) return undefined; seen.add(key);
    if (name === 'default') {
      const assignment = file.getExportAssignments()[0];
      if (assignment) return this.resolve(assignment.getExpression(), seen);
      const declaration = [...file.getFunctions(), ...file.getClasses()].find(item => item.isDefaultExport());
      if (declaration) return declaration;
    }
    for (const declaration of [...file.getFunctions(), ...file.getClasses(), ...file.getInterfaces(), ...file.getVariableDeclarations(), ...file.getEnums()]) {
      if (declaration.getName() === name && declaration.isExported()) return declaration;
    }
    const candidates = new Set<Node>();
    for (const exp of file.getExportDeclarations()) {
      const target = exp.getModuleSpecifierValue() ? this.module(file, exp.getModuleSpecifierValue()!) : file;
      if (!target) continue;
      const named = exp.getNamedExports();
      if (named.length) {
        const item = named.find(item => (item.getAliasNode()?.getText() ?? item.getName()) === name);
        if (item) {
          const value = target === file ? this.resolve(item.getNameNode(), new Set(seen)) : this.exported(target, item.getName(), new Set(seen));
          if (value) candidates.add(value);
        }
      } else if (name !== 'default') {
        const value = this.exported(target, name, new Set(seen)); if (value) candidates.add(value);
      }
    }
    if (candidates.size > 1) this.diagnostic(`symbolAmbiguity: ${key} has ${candidates.size} export candidates`);
    return candidates.size === 1 ? [...candidates][0] : undefined;
  }
  resolve(node: Node | undefined, seen = new Set<string>()): Node | undefined {
    if (!node) return undefined;
    const key = `${node.getSourceFile().getFilePath()}:${node.getPos()}:${node.getKind()}`;
    if (seen.has(key)) return undefined; seen.add(key);
    if (this.declarations.has(node)) {
      if (Node.isVariableDeclaration(node) || Node.isPropertyDeclaration(node)) {
        const init = node.getInitializer();
        if (init) return this.resolve(init, seen) ?? node;
      }
      return node;
    }
    if (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isNonNullExpression(node) || Node.isTypeAssertion(node)) return this.resolve(node.getExpression(), seen);
    if (Node.isCallExpression(node)) {
      if (['memo', 'forwardRef', 'useCallback'].includes(this.reactApi(node) ?? '')) return this.resolve(node.getArguments()[0], seen);
      if (this.reactApi(node) === 'lazy') {
        const load = node.getDescendantsOfKind(SyntaxKind.CallExpression).find(call => call.getExpression().getKind() === SyntaxKind.ImportKeyword);
        const argument = load?.getArguments()[0];
        if (argument && Node.isStringLiteral(argument)) {
          const file = this.module(node, argument.getLiteralText());
          // lazy(() => import(...).then(m => ({ default: m.Named })))
          const named = node.getDescendantsOfKind(SyntaxKind.PropertyAssignment).find(prop => prop.getName() === 'default');
          const expression = named?.getInitializer();
          const exportName = expression && Node.isPropertyAccessExpression(expression) ? expression.getName() : 'default';
          return file ? this.exported(file, exportName) : undefined;
        }
      }
      return undefined;
    }
    const symbol = node.getSymbol();
    const declarations = symbol?.getDeclarations() ?? [];
    for (const declaration of declarations) {
      if (!this.byPath.has(this.relative(declaration))) continue;
      if (Node.isImportSpecifier(declaration)) {
        const imp = declaration.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
        const target = this.module(node, imp.getModuleSpecifierValue());
        if (target) return this.exported(target, declaration.getName(), seen);
      } else if (Node.isImportClause(declaration)) {
        const imp = declaration.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
        const target = this.module(node, imp.getModuleSpecifierValue());
        if (target) return this.exported(target, 'default', seen);
      } else if (this.declarations.has(declaration)) return this.resolve(declaration, seen);
      else if (Node.isPropertyDeclaration(declaration) || Node.isPropertyAssignment(declaration)) return this.resolve(declaration.getInitializer(), seen);
      else if (Node.isParameterDeclaration(declaration)) {
        const type = declaration.getTypeNode();
        if (type) return this.resolve(type, seen);
      }
    }
    if (Node.isPropertyAccessExpression(node)) {
      const receiver = node.getExpression();
      if (Node.isIdentifier(receiver)) {
        for (const imp of node.getSourceFile().getImportDeclarations()) {
          if (!sameSymbol(receiver, imp.getNamespaceImport())) continue;
          const file = this.module(node, imp.getModuleSpecifierValue());
          if (file) return this.exported(file, node.getName(), seen);
        }
      }
      const target = this.resolve(receiver, seen);
      if (target && Node.isClassDeclaration(target)) return target.getMethod(node.getName()) ?? target.getProperty(node.getName());
      if (target && Node.isObjectLiteralExpression(target)) {
        const prop = target.getProperty(node.getName());
        if (prop && Node.isPropertyAssignment(prop)) return this.resolve(prop.getInitializer(), seen);
      }
    }
    if (Node.isTypeReference(node)) return this.resolve(node.getTypeName(), seen);
    if (Node.isObjectLiteralExpression(node)) return node;
    return isCallable(node) ? node : undefined;
  }
  target(node: Node | undefined): string | undefined {
    const resolved = this.resolve(node);
    return resolved ? this.declarations.get(resolved) : undefined;
  }
  /** Recognize React APIs only through an actual react import, including named aliases. */
  reactApi(call: CallExpression): string | undefined {
    const expression = call.getExpression();
    for (const imp of call.getSourceFile().getImportDeclarations()) {
      if (imp.getModuleSpecifierValue() !== 'react') continue;
      if (Node.isIdentifier(expression)) {
        const specifier = imp.getNamedImports().find(item => (item.getAliasNode()?.getText() ?? item.getName()) === expression.getText());
        if (specifier && sameSymbol(expression, specifier.getAliasNode() ?? specifier.getNameNode())) return specifier.getName();
      }
      if (Node.isPropertyAccessExpression(expression) &&
          (sameSymbol(expression.getExpression(), imp.getNamespaceImport()) || sameSymbol(expression.getExpression(), imp.getDefaultImport()))) return expression.getName();
    }
    return undefined;
  }
  extract(nodes: GraphNodeDto[], edges: PendingEdgeDto[], unresolved: UnresolvedCallDto[]): void {
    const existing = new Map(nodes.map(node => [node.qualifiedName, node]));
    const callableQNs = new Set(this.callables.values());
    for (const [callable, qn] of this.callables) {
      const jsx = callable.getDescendants().some(node => Node.isJsxElement(node) || Node.isJsxSelfClosingElement(node) || Node.isJsxFragment(node));
      const name = qn.split(/[.#]/).pop()!;
      const component = jsx && (/^[A-Z]/.test(name) || name === 'default');
      const node = existing.get(qn);
      if (node) { if (component) { node.label = 'Component'; node.properties.framework = 'react'; } continue; }
      const emitted: GraphNodeDto = { label: component ? 'Component' : Node.isMethodDeclaration(callable) ? 'Method' : 'Function', name,
        qualifiedName: qn, filePath: this.relative(callable), startLine: callable.getStartLineNumber(), endLine: callable.getEndLineNumber(),
        properties: { callable: true, ...(component ? { framework: 'react' } : {}), is_hook: /^use[A-Z]/.test(name) } };
      nodes.push(emitted); existing.set(qn, emitted);
      const owner = callable.getAncestors().find(node => this.callables.has(node) || Node.isClassDeclaration(node));
      edges.push({ sourceQN: owner ? this.declarations.get(owner)! : this.fileQN(callable), targetQN: qn,
        type: owner && Node.isClassDeclaration(owner) ? 'DEFINES_METHOD' : 'DEFINES' });
    }
    for (const file of this.files) {
      for (const attribute of file.getDescendantsOfKind(SyntaxKind.JsxAttribute)) {
        if (!/^on[A-Z]/.test(attribute.getNameNode().getText())) continue;
        const init = attribute.getInitializer();
        const target = init && Node.isJsxExpression(init) ? this.target(init.getExpression()) : undefined;
        if (target && callableQNs.has(target)) edges.push({ sourceQN: this.owner(attribute), targetQN: target, type: 'CALLS', properties: { call_kind: 'event_handler' } });
      }
      for (const call of file.getDescendantsOfKind(SyntaxKind.CallExpression)) {
        const source = this.owner(call); const target = this.target(call.getExpression());
        if (target && callableQNs.has(target)) edges.push({ sourceQN: source, targetQN: target, type: 'CALLS' });
        for (const arg of call.getArguments()) {
          const callback = this.target(arg);
          if (callback && callback !== source && callableQNs.has(callback)) edges.push({ sourceQN: source, targetQN: callback, type: 'CALLS', properties: { call_kind: 'callback' } });
        }
        if (!target && unresolved.length < 100 && !this.reactApi(call)) unresolved.push({ callerQN: source, calleeName: call.getExpression().getText().slice(0, 160), confidence: 0 });
      }
    }
  }
  reconcileAngular(nodes: GraphNodeDto[], edges: PendingEdgeDto[]): void {
    const angular = new Set(['INHERITS', 'IMPLEMENTS', 'INJECTS', 'HANDLES']);
    for (let index = edges.length - 1; index >= 0; index--) {
      const edge = edges[index]; if (!angular.has(edge.type) || edge.properties?.framework === 'react') continue;
      const source = this.byQN.get(edge.sourceQN);
      let expression: Node | undefined;
      if (source && Node.isClassDeclaration(source)) {
        if (edge.type === 'INHERITS') expression = source.getExtends()?.getExpression();
        if (edge.type === 'IMPLEMENTS') expression = source.getImplements().find(item => item.getExpression().getText() === edge.targetQN)?.getExpression();
        if (edge.type === 'INJECTS') {
          const parameter = source.getConstructors().flatMap(ctor => ctor.getParameters()).find(param => param.getName() === edge.properties?.parameter_name);
          expression = parameter?.getTypeNode();
          if (!expression) {
            const init = source.getProperty(String(edge.properties?.parameter_name))?.getInitializer();
            if (init && Node.isCallExpression(init)) expression = init.getArguments()[0];
          }
        }
      }
      if (edge.type === 'HANDLES') {
        const route = nodes.find(node => node.qualifiedName === edge.sourceQN);
        const file = route ? this.byPath.get(route.filePath) : undefined;
        expression = file?.getDescendantsOfKind(SyntaxKind.PropertyAssignment).find(prop => prop.getName() === 'component' && prop.getInitializer()?.getText() === edge.targetQN)?.getInitializer();
      }
      const target = this.target(expression);
      if (target) edge.targetQN = target; else edges.splice(index, 1);
    }
  }
}
