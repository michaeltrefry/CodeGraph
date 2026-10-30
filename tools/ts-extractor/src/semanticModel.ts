import { Node, SourceFile, SyntaxKind, CallExpression } from 'ts-morph';
import * as path from 'path';
import { addNpmExportMetadata } from './packageExports';
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
  private readonly relativePaths = new Map<SourceFile, string>();
  private readonly importBySource = new Map<string, ResolvedImportDto>();
  private readonly resolvedModules = new Map<string, SourceFile | undefined>();
  private readonly externalCallSymbols = new Map<string, GraphNodeDto>();
  private readonly memberWrites = new Map<Node, boolean>();
  private readonly memberWriteEvidence = new Map<string, boolean>();
  constructor(readonly repository: string, readonly root: string, readonly files: SourceFile[],
    readonly imports: ResolvedImportDto[], readonly diagnostic: (message: string) => void) {
    for (const file of files) this.byPath.set(this.relative(file), file);
    for (const imported of imports) {
      const key = `${imported.filePath}\0${imported.importedNamespace}`;
      if (!this.importBySource.has(key)) this.importBySource.set(key, imported);
    }
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
  relative(node: Node): string {
    const file = node.getSourceFile();
    let relative = this.relativePaths.get(file);
    if (relative === undefined) {
      relative = path.relative(this.root, file.getFilePath()).replace(/\\/g, '/');
      this.relativePaths.set(file, relative);
    }
    return relative;
  }
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
    const key = `${this.relative(from)}\0${specifier}`;
    if (this.resolvedModules.has(key)) return this.resolvedModules.get(key);
    const resolved = this.importBySource.get(key);
    const module = resolved?.resolvedFilePath ? this.byPath.get(resolved.resolvedFilePath) : undefined;
    this.resolvedModules.set(key, module);
    return module;
  }
  /** Conservative writes through an object/import and its static aliases. Rooting assignment
   * targets catches Icon.Model/alias.Model writes without interpreting application execution. */
  hasMemberWrites(node: Node, provenance?: Record<string, unknown>): boolean {
    const key = node.getSymbol()?.getDeclarations()[0] ?? node;
    if (this.memberWrites.has(key)) return this.memberWrites.get(key)!;
    const evidenceKey = provenance ? `${provenance.import_path}\0${provenance.export_name ?? '*'}` : undefined;
    if (evidenceKey && this.memberWriteEvidence.has(evidenceKey)) return this.memberWriteEvidence.get(evidenceKey)!;
    const root = (value: Node): Node => Node.isPropertyAccessExpression(value) || Node.isElementAccessExpression(value)
      ? root(value.getExpression()) : Node.isParenthesizedExpression(value) || Node.isAsExpression(value) ? root(value.getExpression()) : value;
    const symbols = new Set([node.getSymbol()?.compilerSymbol, key.getSymbol()?.compilerSymbol].filter(value => !!value));
    const owns = (value: Node | undefined): boolean => { const symbol = value?.getSymbol()?.compilerSymbol; return !!symbol && symbols.has(symbol); };
    const aliases = this.files.flatMap(file => file.getDescendantsOfKind(SyntaxKind.VariableDeclaration));
    for (const file of this.files) for (const imp of file.getImportDeclarations()) {
      for (const named of imp.getNamedImports()) {
        const local = named.getAliasNode() ?? named.getNameNode();
        const target = this.resolve(local);
        if ((target && (target === key || owns(target)))
          || (provenance && imp.getModuleSpecifierValue() === provenance.import_path && (provenance.export_name === undefined || named.getName() === provenance.export_name))) {
          const symbol = local.getSymbol()?.compilerSymbol; if (symbol) symbols.add(symbol);
        }
      }
      if (provenance && imp.getModuleSpecifierValue() === provenance.import_path) {
        const namespace = imp.getNamespaceImport()?.getSymbol()?.compilerSymbol; if (namespace) symbols.add(namespace);
      }
    }
    for (let pass = 0; pass <= aliases.length; pass++) {
      let changed = false;
      for (const alias of aliases) {
        const init = alias.getInitializer(); if (!init) continue;
        const source = root(init);
        if (!Node.isIdentifier(source)) continue;
        const resolved = this.resolve(init); const rootDeclaration = this.resolve(source);
        if (!owns(source)
          && !(resolved && (resolved === key || owns(resolved)))
          && !(rootDeclaration && (rootDeclaration === key || owns(rootDeclaration)))) continue;
        const binding = alias.getNameNode();
        const names = Node.isIdentifier(binding) ? [binding] : binding.getDescendantsOfKind(SyntaxKind.Identifier);
        for (const name of names) { const symbol = name.getSymbol()?.compilerSymbol; if (symbol && !symbols.has(symbol)) { symbols.add(symbol); changed = true; } }
      }
      if (!changed) break;
    }
    const written = this.files.some(file => file.getDescendants().some(expression => {
      let target: Node | undefined;
      if (Node.isBinaryExpression(expression) && expression.getOperatorToken().getKind() >= SyntaxKind.FirstAssignment
        && expression.getOperatorToken().getKind() <= SyntaxKind.LastAssignment) target = expression.getLeft();
      else if ((Node.isPrefixUnaryExpression(expression) || Node.isPostfixUnaryExpression(expression))
        && [SyntaxKind.PlusPlusToken, SyntaxKind.MinusMinusToken].includes(expression.getOperatorToken())) target = expression.getOperand();
      else if (Node.isDeleteExpression(expression)) target = expression.getExpression();
      else if (Node.isCallExpression(expression) && expression.getExpression().getText() === 'Object.assign') target = expression.getArguments()[0];
      if (!target) return false;
      let part: Node | undefined = target;
      while (part) {
        const resolved = this.resolve(part);
        if (owns(part) || (resolved && (resolved === key || owns(resolved)))) return true;
        part = Node.isPropertyAccessExpression(part) || Node.isElementAccessExpression(part) ? part.getExpression() : undefined;
      }
      return false;
    }));
    this.memberWrites.set(key, written);
    if (evidenceKey) this.memberWriteEvidence.set(evidenceKey, written);
    return written;
  }
  /** Imported npm provenance, including aliases and bounded local barrels. A display name or
   * an instance member alone never establishes an external application declaration. */
  externalReference(expression: Node | undefined, seen = new Set<Node>()): { qualifiedName: string; properties: Record<string, unknown> } | undefined {
    if (!expression || seen.has(expression) || seen.size > 60) return undefined;
    seen.add(expression);
    const imported = (from: Node, specifier: string, name: string, history = seen): ReturnType<SemanticModel['externalReference']> => {
      const module = this.module(from, specifier);
      if (module) return exported(module, name, history);
      const resolution = this.importBySource.get(`${this.relative(from)}\0${specifier}`);
      if (resolution?.classification !== 'external_npm_package' || !resolution.externalPackageName || specifier.startsWith('node:')) return undefined;
      return { qualifiedName: `npm-symbol:${specifier}#${name}`, properties: {
        package_name: resolution.externalPackageName, import_path: specifier, export_name: name,
      } };
    };
    const exported = (file: SourceFile, name: string, history: Set<Node>): ReturnType<SemanticModel['externalReference']> => {
      if (history.has(file) || history.size > 60) return undefined;
      const branch = new Set(history); branch.add(file);
      if (name === 'default' && file.getExportAssignments().length)
        return this.externalReference(file.getExportAssignments()[0].getExpression(), branch);
      const explicit = file.getExportDeclarations().flatMap(declaration => declaration.getNamedExports()
        .filter(item => (item.getAliasNode()?.getText() ?? item.getName()) === name).map(item => ({ declaration, item })));
      if (explicit.length) {
        const candidates = explicit.map(({ declaration, item }) => declaration.getModuleSpecifierValue()
          ? imported(declaration, declaration.getModuleSpecifierValue()!, item.getName(), branch)
          : this.externalReference(item.getNameNode(), branch));
        const identities = new Map(candidates.filter((value): value is NonNullable<typeof value> => !!value).map(value => [value.qualifiedName, value]));
        return candidates.every(value => !!value) && identities.size === 1 ? [...identities.values()][0] : undefined;
      }
      // Resolve a directly exported local alias before considering stars. Actual local
      // functions/classes have already resolved through target(), so they cannot become imports.
      const local = [...file.getFunctions(), ...file.getClasses(), ...file.getVariableDeclarations()]
        .find(declaration => declaration.isExported() && (name === 'default' ? declaration.isDefaultExport() : declaration.getName() === name));
      if (local) return this.externalReference(local, branch);
      const stars = file.getExportDeclarations().filter(declaration => !declaration.getNamedExports().length && declaration.getModuleSpecifierValue());
      const candidates = stars.map(declaration => imported(declaration, declaration.getModuleSpecifierValue()!, name, branch));
      const identities = new Map(candidates.filter((value): value is NonNullable<typeof value> => !!value).map(value => [value.qualifiedName, value]));
      return candidates.length && candidates.every(value => !!value) && identities.size === 1 ? [...identities.values()][0] : undefined;
    };
    if (Node.isParenthesizedExpression(expression) || Node.isAsExpression(expression) || Node.isNonNullExpression(expression))
      return this.externalReference(expression.getExpression(), seen);
    if (Node.isVariableDeclaration(expression) && expression.getVariableStatement()?.getDeclarationKind() === 'const')
      return this.externalReference(expression.getInitializer(), seen);
    if (Node.isPropertyAccessExpression(expression)) {
      const namespace = expression.getExpression().getSymbol()?.getDeclarations() ?? [];
      if (namespace.length === 1 && Node.isNamespaceImport(namespace[0])) {
        const declaration = namespace[0].getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
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
    if (declarations.length !== 1) return undefined;
    const declaration = declarations[0];
    if (Node.isImportSpecifier(declaration)) {
      const imp = declaration.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
      return imported(imp, imp.getModuleSpecifierValue(), declaration.getName());
    }
    if (Node.isImportClause(declaration)) return imported(declaration, declaration.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!.getModuleSpecifierValue(), 'default');
    if (Node.isVariableDeclaration(declaration)) return this.externalReference(declaration, seen);
    return undefined;
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
    if (seen.size > 100) { this.diagnostic(`symbolResolutionLimit: ${this.relative(node)}`); return undefined; }
    const key = `${node.getSourceFile().getFilePath()}:${node.getPos()}:${node.getKind()}`;
    if (seen.has(key)) return undefined; seen.add(key);
    if (this.declarations.has(node)) {
      if (Node.isVariableDeclaration(node) || Node.isPropertyDeclaration(node)) {
        const init = node.getInitializer();
        if (init) return this.resolve(init, seen) ?? node;
        const type = node.getTypeNode();
        if (type) return this.resolve(type, seen) ?? node;
      }
      return node;
    }
    if (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isNonNullExpression(node) || Node.isTypeAssertion(node)) return this.resolve(node.getExpression(), seen);
    if (Node.isNewExpression(node)) return this.resolve(node.getExpression(), seen);
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
      // A known local/imported factory or custom hook can return a callable/object. Follow
      // only statically unique returns and keep alternatives explicitly unresolved.
      const callee = this.resolve(node.getExpression(), new Set(seen));
      if (callee && (Node.isFunctionDeclaration(callee) || Node.isFunctionExpression(callee) || Node.isArrowFunction(callee) || Node.isMethodDeclaration(callee))) {
        const body = callee.getBody();
        if (body) {
          const expressions = Node.isBlock(body)
            ? body.getDescendantsOfKind(SyntaxKind.ReturnStatement)
              .filter(statement => this.owner(statement) === this.callables.get(callee))
              .flatMap(statement => statement.getExpression() ? [statement.getExpression()!] : [])
            : [body];
          const resolvedReturns = expressions.map(expression => this.resolve(expression, new Set(seen)));
          const returns = new Set(resolvedReturns.filter((value): value is Node => !!value));
          if (returns.size === 1 && resolvedReturns.every(value => !!value)) return [...returns][0];
          if (returns.size > 1 || returns.size && resolvedReturns.some(value => !value)) this.diagnostic(`functionReturnAmbiguity: ${this.declarations.get(callee)} has ${returns.size} known return candidates and ${resolvedReturns.filter(value => !value).length} unknown returns`);
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
        if (prop && Node.isShorthandPropertyAssignment(prop)) return this.resolve(prop.getValueSymbol()?.getDeclarations()[0], seen);
        if (prop && Node.isMethodDeclaration(prop)) return prop;
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
        let external: ReturnType<SemanticModel['externalReference']>;
        if (target && callableQNs.has(target)) edges.push({ sourceQN: source, targetQN: target, type: 'CALLS' });
        else if ((external = this.externalReference(call.getExpression()))) {
          if (!this.externalCallSymbols.has(external.qualifiedName)) this.externalCallSymbols.set(external.qualifiedName, {
            label: 'ExternalSymbol', name: [external.properties.export_name, external.properties.member_path].filter(Boolean).join('.'), qualifiedName: external.qualifiedName,
            filePath: this.relative(call), startLine: 0, endLine: 0, properties: external.properties,
          });
          edges.push({ sourceQN: source, targetQN: external.qualifiedName, type: 'CALLS', properties: { call_kind: 'external_import' } });
        }
        for (const arg of call.getArguments()) {
          const callback = this.target(arg);
          if (callback && callback !== source && callableQNs.has(callback)) edges.push({ sourceQN: source, targetQN: callback, type: 'CALLS', properties: { call_kind: 'callback' } });
        }
        if (!target && !external && unresolved.length < 100 && !this.reactApi(call)) unresolved.push({ callerQN: source, calleeName: call.getExpression().getText().slice(0, 160), confidence: 0 });
      }
    }
  }
  reconcilePackageExports(nodes: GraphNodeDto[]): void {
    // Rendering may already have materialized the same npm symbol. Keep a single declaration
    // owner instead of emitting conflicting node copies for call-plus-render references.
    const present = new Set(nodes.map(node => node.qualifiedName));
    for (const [qn, node] of this.externalCallSymbols) if (!present.has(qn)) nodes.push(node);
    addNpmExportMetadata(this, nodes);
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
