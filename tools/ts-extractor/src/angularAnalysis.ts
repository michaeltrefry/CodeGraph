import { Node, SyntaxKind, ClassDeclaration, InterfaceDeclaration, ObjectLiteralExpression } from 'ts-morph';
import * as fs from 'fs';
import * as path from 'path';
import { SemanticModel, sameSymbol } from './semanticModel';
import { DiscoveryScope } from './discoveryScope';
import type { GraphNodeDto, PendingEdgeDto } from './protocol';

type Binding = { token: Node; implementation?: ClassDeclaration; alias?: Node; multi: boolean; scope: ProviderScope };
type ProviderScope = { owner: Node; qn: string; identity: string; bindings: Map<Node, Binding[]>; parents: ProviderScope[]; uncertain: boolean };
type Selection = { implementations: ClassDeclaration[]; certain: boolean; token?: Node; runtime?: boolean };
type Receiver = { contract?: InterfaceDeclaration; selection?: Selection; injected?: boolean };

/** Derive Angular links exclusively from admitted declarations and imported framework APIs.
 * Provider tables remain scoped: overrides never leak into a sibling application's injector.
 */
export function analyzeAngular(model: SemanticModel, nodes: GraphNodeDto[], edges: PendingEdgeDto[],
  scope?: DiscoveryScope, diagnostic: (message: string) => void = model.diagnostic): { complete: boolean; processedTemplates: string[] } {
  let complete = true;
  const processedTemplates = new Set<string>();
  const classes = model.files.flatMap(file => file.getClasses());
  const interfaces = model.files.flatMap(file => file.getInterfaces());
  const graphNodes = new Map(nodes.map(node => [node.qualifiedName, node]));
  const tokenQNs = new Map<Node, string>();
  const tokenIdentities = new Map<string, Node>();
  function qn(node: Node): string {
    const known = tokenQNs.get(node) ?? model.declarations.get(node);
    if (known) return known;
    if (Node.isPropertyDeclaration(node) || Node.isParameterDeclaration(node)) {
      const owner = classOf(node);
      return `${owner ? qn(owner) : model.fileQN(node)}.${node.getName()}`;
    }
    return model.fileQN(node);
  }
  // The semantic pass resolves declared types before Angular selects an injector binding.
  // Keep its edge identities so only superseded call evidence is retracted below.
  const callKey = (source: string, target: string) => JSON.stringify([source, target]);
  const provisionalCalls = new Map<string, Set<PendingEdgeDto>>();
  for (const edge of edges) if (edge.type === 'CALLS' && !edge.properties?.call_kind) {
    const key = callKey(edge.sourceQN, edge.targetQN);
    const existing = provisionalCalls.get(key) ?? new Set<PendingEdgeDto>();
    existing.add(edge); provisionalCalls.set(key, existing);
  }
  const redirectedCallSites = new Set<Node>();
  const redirectedCallKeys = new Set<string>();
  const edgeKeys = new Set<string>();
  function addEdge(sourceQN: string, targetQN: string, type: string, properties: Record<string, unknown> = {}) {
    const props = { framework: 'angular', ...properties };
    const key = `${type}|${sourceQN}|${targetQN}|${JSON.stringify(props)}`;
    if (!edgeKeys.has(key)) {
      edgeKeys.add(key);
      const direct = type === 'CALLS' ? edges.find(edge => edge.type === type && edge.sourceQN === sourceQN && edge.targetQN === targetQN && !edge.properties?.call_kind) : undefined;
      if (direct) direct.properties = {...direct.properties, ...props};
      else edges.push({ sourceQN, targetQN, type, properties: props });
    }
  }
  function emit(node: Node, qualifiedName: string, label: string, name: string, properties: Record<string, unknown> = {}) {
    const existing = graphNodes.get(qualifiedName);
    if (existing) { Object.assign(existing.properties, properties); return; }
    const value = { label, name, qualifiedName, filePath: model.relative(node), startLine: node.getStartLineNumber(), endLine: node.getEndLineNumber(), properties };
    nodes.push(value); graphNodes.set(qualifiedName, value);
  }
  function imported(node: Node | undefined, packageName: string): string | undefined {
    if (!node) return undefined;
    for (const imp of node.getSourceFile().getImportDeclarations()) {
      if (imp.getModuleSpecifierValue() !== packageName) continue;
      if (Node.isIdentifier(node)) {
        const item = imp.getNamedImports().find(item => sameSymbol(node, item.getAliasNode() ?? item.getNameNode()));
        if (item) return item.getName();
      }
      if (Node.isPropertyAccessExpression(node) && sameSymbol(node.getExpression(), imp.getNamespaceImport())) return node.getName();
      if (Node.isQualifiedName(node) && sameSymbol(node.getLeft(), imp.getNamespaceImport())) return node.getRight().getText();
    }
    return undefined;
  }
  function decorator(node: ClassDeclaration, name: string): ObjectLiteralExpression | undefined {
    const dec = node.getDecorators().find(dec => imported(dec.getCallExpression()?.getExpression() ?? dec.getExpression(), '@angular/core') === name);
    const value = reference(dec?.getArguments()[0]);
    return value && Node.isObjectLiteralExpression(value) ? value : undefined;
  }
  function property(object: ObjectLiteralExpression | undefined, name: string): Node | undefined {
    const member = object?.getProperty(name);
    return member && Node.isPropertyAssignment(member) ? member.getInitializer() : undefined;
  }
  function classOf(node: Node): ClassDeclaration | undefined { return Node.isClassDeclaration(node) ? node : node.getFirstAncestorByKind(SyntaxKind.ClassDeclaration); }
  function reference(node: Node | undefined, seen = new Set<Node>()): Node | undefined {
    if (!node || seen.has(node)) return undefined;
    seen.add(node);
    if (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isNonNullExpression(node) || Node.isTypeAssertion(node)) return reference(node.getExpression(), seen);
    if (Node.isPropertyAccessExpression(node) && node.getExpression().getKind() === SyntaxKind.ThisKeyword) {
      const owner = classOf(node);
      const field = owner ? classField(owner, node.getName()) : undefined;
      return field ? reference(field, seen) : undefined;
    }
    if (Node.isPropertyDeclaration(node) || Node.isParameterDeclaration(node) || Node.isPropertyAssignment(node)) {
      const initial = node.getInitializer();
      return initial ? reference(initial, seen) : node;
    }
    if (Node.isCallExpression(node) && imported(node.getExpression(), '@angular/core') === 'forwardRef') {
      const callback = node.getArguments()[0];
      if (callback && Node.isArrowFunction(callback)) {
        const body = callback.getBody();
        return reference(Node.isBlock(body) ? body.getStatements().find(Node.isReturnStatement)?.getExpression() : body, seen);
      }
    }
    const resolved = model.resolve(node);
    if (resolved && resolved !== node && (Node.isPropertyDeclaration(resolved) || Node.isPropertyAssignment(resolved) || Node.isVariableDeclaration(resolved))) return reference(resolved, seen);
    return resolved ?? node.getSymbol()?.getDeclarations().find(declaration =>
      model.byPath.has(model.relative(declaration)) && (Node.isVariableDeclaration(declaration) || Node.isParameterDeclaration(declaration) || Node.isPropertyDeclaration(declaration)));
  }
  function tokenReference(node: Node | undefined): Node | undefined {
    const resolved = reference(node);
    if (resolved) return resolved;
    if (!node) return undefined;
    let identity: string | undefined;
    if (Node.isStringLiteral(node)) identity = `angular-token:literal:${node.getLiteralText()}`;
    for (const imp of node.getSourceFile().getImportDeclarations()) {
      const resolution = model.imports.find(item => item.filePath === model.relative(node) && item.importedNamespace === imp.getModuleSpecifierValue());
      if (resolution?.classification !== 'external_npm_package') continue;
      const name = imported(node, imp.getModuleSpecifierValue());
      if (name) identity = `angular-token:${imp.getModuleSpecifierValue()}:${name}`;
    }
    if (!identity) return undefined;
    const previous = tokenIdentities.get(identity);
    if (previous) return previous;
    tokenIdentities.set(identity, node); tokenQNs.set(node, identity); return node;
  }
  function classField(cls: ClassDeclaration, name: string, seen = new Set<Node>()): Node | undefined {
    if (seen.has(cls)) return undefined; seen.add(cls);
    const field = cls.getProperty(name) ?? cls.getConstructors().flatMap(ctor => ctor.getParameters()).find(parameter => parameter.getName() === name);
    if (field) return field;
    const parent = reference(cls.getExtends()?.getExpression());
    return parent && Node.isClassDeclaration(parent) ? classField(parent, name, seen) : undefined;
  }
  function values(node: Node | undefined, seen = new Set<Node>()): Node[] {
    if (!node || seen.has(node)) return [];
    seen.add(node);
    const value = reference(node);
    if (value && value !== node && !Node.isClassDeclaration(value) && !Node.isInterfaceDeclaration(value)) return values(value, seen);
    if (Node.isVariableDeclaration(node) || Node.isPropertyDeclaration(node)) return values(node.getInitializer(), seen);
    if (Node.isArrayLiteralExpression(node)) return node.getElements().flatMap(item => values(Node.isSpreadElement(item) ? item.getExpression() : item, new Set(seen)));
    if (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isNonNullExpression(node) || Node.isTypeAssertion(node)) return values(node.getExpression(), seen);
    return value ? [value] : [node];
  }
  function string(node: Node | undefined, seen = new Set<Node>()): string | undefined {
    if (!node || seen.has(node)) return undefined; seen.add(node);
    if (Node.isStringLiteral(node) || Node.isNoSubstitutionTemplateLiteral(node)) return node.getLiteralText();
    const value = reference(node);
    if (value && value !== node && (Node.isVariableDeclaration(value) || Node.isPropertyDeclaration(value))) return string(value.getInitializer(), seen);
    return undefined;
  }
  function typeInterface(node: Node | undefined): InterfaceDeclaration | undefined {
    if (!node) return undefined;
    const value = reference(node);
    if (value && Node.isInterfaceDeclaration(value)) return value;
    if (Node.isTypeReference(node) && ['InjectionToken', 'ProviderToken', 'Type'].includes(imported(node.getTypeName(), '@angular/core') ?? '')) return typeInterface(node.getTypeArguments()[0]);
    if (Node.isUnionTypeNode(node)) {
      const candidates = new Set(node.getTypeNodes().map(typeInterface).filter((value): value is InterfaceDeclaration => !!value));
      return candidates.size === 1 ? [...candidates][0] : undefined;
    }
    return undefined;
  }
  function tokenInterface(token: Node | undefined, seen = new Set<Node>()): InterfaceDeclaration | undefined {
    if (!token || seen.has(token)) return undefined; seen.add(token);
    if (Node.isInterfaceDeclaration(token)) return token;
    if (Node.isVariableDeclaration(token) || Node.isPropertyDeclaration(token) || Node.isParameterDeclaration(token)) {
      const typed = typeInterface(token.getTypeNode());
      if (typed) return typed;
      const initial = token.getInitializer();
      if (initial && Node.isNewExpression(initial) && imported(initial.getExpression(), '@angular/core') === 'InjectionToken') return typeInterface(initial.getTypeArguments()[0]);
      const resolved = reference(initial);
      if (resolved && resolved !== token) return tokenInterface(resolved, seen);
    }
    return undefined;
  }
  const angular = classes.flatMap(declaration => {
    for (const kind of ['Component', 'NgModule', 'Directive', 'Injectable']) {
      if (declaration.getDecorators().some(dec => imported(dec.getCallExpression()?.getExpression() ?? dec.getExpression(), '@angular/core') === kind)) return [{ declaration, metadata: decorator(declaration, kind), kind }];
    }
    return [];
  });
  const components = angular.filter(value => value.kind === 'Component');
  const componentSet = new Set(components.map(component => component.declaration));
  for (const cls of classes) {
    const node = graphNodes.get(qn(cls));
    if (!node) continue;
    const angularClass = angular.find(item => item.declaration === cls);
    node.properties.is_service = angularClass?.kind === 'Injectable';
    node.properties.is_ng_module = angularClass?.kind === 'NgModule';
    if (componentSet.has(cls)) {
      node.label = 'Component'; node.properties.framework = 'angular';
      const selector = string(property(angularClass?.metadata, 'selector'));
      const templateUrl = string(property(angularClass?.metadata, 'templateUrl'));
      if (selector !== undefined) node.properties.selector = selector;
      if (templateUrl !== undefined) node.properties.templateUrl = templateUrl;
    }
    else if (node.properties.framework === 'angular' && node.label === 'Component') {
      node.label = 'Class'; delete node.properties.framework; delete node.properties.selector; delete node.properties.templateUrl;
    }
  }
  const interfaceMembers = new Map<InterfaceDeclaration, Map<string, Node>>();
  function members(iface: InterfaceDeclaration, seen = new Set<Node>()): Map<string, Node> {
    if (interfaceMembers.has(iface)) return interfaceMembers.get(iface)!;
    if (seen.has(iface)) return new Map(); seen.add(iface);
    const result = new Map<string, Node>();
    for (const parent of iface.getExtends()) {
      const value = reference(parent.getExpression());
      if (value && Node.isInterfaceDeclaration(value)) for (const [name, method] of members(value, seen)) result.set(name, method);
    }
    for (const member of iface.getMembers()) {
      if (Node.isMethodSignature(member) || (Node.isPropertySignature(member) && member.getType().getCallSignatures().length > 0)) {
        result.set(member.getName(), member);
        const methodQN = `${qn(iface)}.${member.getName()}`;
        emit(member, methodQN, 'Method', member.getName(), { callable: true, is_interface_member: true, is_function_property: Node.isPropertySignature(member) });
        addEdge(qn(iface), methodQN, 'DEFINES_METHOD');
      }
    }
    interfaceMembers.set(iface, result); return result;
  }
  for (const iface of interfaces) members(iface);
  const implementers = new Map<InterfaceDeclaration, ClassDeclaration[]>();
  function contracts(cls: ClassDeclaration, seen = new Set<Node>()): Set<InterfaceDeclaration> {
    if (seen.has(cls)) return new Set(); seen.add(cls);
    const result = new Set<InterfaceDeclaration>();
    const add = (iface: InterfaceDeclaration) => {
      if (result.has(iface)) return; result.add(iface);
      for (const parent of iface.getExtends()) { const value = reference(parent.getExpression()); if (value && Node.isInterfaceDeclaration(value)) add(value); }
    };
    for (const impl of cls.getImplements()) { const value = reference(impl.getExpression()); if (value && Node.isInterfaceDeclaration(value)) add(value); }
    const base = reference(cls.getExtends()?.getExpression());
    if (base && Node.isClassDeclaration(base)) for (const item of contracts(base, seen)) add(item);
    return result;
  }
  function implementationMethod(cls: ClassDeclaration, name: string, seen = new Set<Node>()): string | undefined {
    if (seen.has(cls)) return undefined; seen.add(cls);
    const method = cls.getMethod(name);
    if (method) return qn(method);
    const prop = cls.getProperty(name)?.getInitializer();
    if (prop) {
      if (Node.isArrowFunction(prop) || Node.isFunctionExpression(prop)) return model.callables.get(prop);
      const callable = reference(prop);
      if (callable && model.callables.has(callable)) return model.callables.get(callable);
    }
    const base = reference(cls.getExtends()?.getExpression());
    return base && Node.isClassDeclaration(base) ? implementationMethod(base, name, seen) : undefined;
  }
  for (const cls of classes) for (const iface of contracts(cls)) {
    const existing = implementers.get(iface) ?? []; existing.push(cls); implementers.set(iface, existing);
    for (const [name, member] of members(iface)) {
      const target = implementationMethod(cls, name);
      const owner = member.getFirstAncestorByKind(SyntaxKind.InterfaceDeclaration)!;
      if (target) addEdge(`${qn(owner)}.${name}`, target, 'CALLS', { call_kind: 'interface_dispatch', confidence_band: 'medium', candidate: true, interface_qn: qn(iface) });
    }
  }
  const scopes = new Map<Node, ProviderScope>();
  function makeScope(owner: Node): ProviderScope {
    let value = scopes.get(owner);
    if (!value) { value = { owner, qn: qn(owner), identity: qn(owner), bindings: new Map(), parents: [], uncertain: false }; scopes.set(owner, value); }
    return value;
  }
  const moduleDeclarations = new Map<ClassDeclaration, ClassDeclaration[]>();
  const moduleImports = new Map<ClassDeclaration, ClassDeclaration[]>();
  const moduleExports = new Map<ClassDeclaration, ClassDeclaration[]>();
  const scopeForClass = (cls: ClassDeclaration): ProviderScope => makeScope(cls);
  for (const item of angular.filter(item => item.kind === 'NgModule' || item.kind === 'Component' || item.kind === 'Directive')) {
    const providerScope = makeScope(item.declaration);
    if (item.kind === 'NgModule') for (const value of values(property(item.metadata, 'declarations'))) {
      if (Node.isClassDeclaration(value)) { const owners = moduleDeclarations.get(value) ?? []; owners.push(item.declaration); moduleDeclarations.set(value, owners); }
    }
    const importedValues = values(property(item.metadata, 'imports'));
    const imports = importedValues.flatMap(value => {
      if (Node.isClassDeclaration(value)) return [value];
      if (Node.isObjectLiteralExpression(value)) {
        const module = reference(property(value, 'ngModule'));
        collectProviders(property(value, 'providers'), providerScope);
        return module && Node.isClassDeclaration(module) ? [module] : [];
      }
      return [];
    });
    moduleImports.set(item.declaration, imports);
    moduleExports.set(item.declaration, values(property(item.metadata, 'exports')).filter(Node.isClassDeclaration));
    for (const imported of imports) if (angular.some(item => item.declaration === imported && item.kind === 'NgModule')) providerScope.parents.push(makeScope(imported));
  }
  function collectProviders(node: Node | undefined, providerScope: ProviderScope, seen = new Set<Node>()): void {
    if (!node || seen.has(node)) return; seen.add(node);
    if (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isNonNullExpression(node) || Node.isTypeAssertion(node)) { collectProviders(node.getExpression(), providerScope, seen); return; }
    if (Node.isArrayLiteralExpression(node)) {
      for (const element of node.getElements()) collectProviders(Node.isSpreadElement(element) ? element.getExpression() : element, providerScope, new Set(seen)); return;
    }
    if (Node.isCallExpression(node)) {
      const api = imported(node.getExpression(), '@angular/core');
      if (api === 'makeEnvironmentProviders') { collectProviders(node.getArguments()[0], providerScope, seen); return; }
      if (api === 'importProvidersFrom') {
        for (const argument of node.getArguments()) for (const module of values(argument).filter(Node.isClassDeclaration)) providerScope.parents.push(makeScope(module));
        return;
      }
    }
    if (Node.isConditionalExpression(node)) {
      providerScope.uncertain = true;
      collectProviders(node.getWhenTrue(), providerScope, new Set(seen)); collectProviders(node.getWhenFalse(), providerScope, new Set(seen)); return;
    }
    const value = reference(node);
    if (value && Node.isVariableDeclaration(value)) { collectProviders(value.getInitializer(), providerScope, seen); return; }
    if (value && Node.isClassDeclaration(value)) {
      providerScope.bindings.set(value, [{ token: value, implementation: value, multi: false, scope: providerScope }]); return;
    }
    if (value && value !== node && (Node.isArrayLiteralExpression(value) || Node.isObjectLiteralExpression(value))) { collectProviders(value, providerScope, seen); return; }
    if (!Node.isObjectLiteralExpression(node)) {
      providerScope.uncertain = true; diagnostic(`angularProvider: unsupported provider in ${providerScope.qn}: ${node.getText().slice(0, 120)}`); return;
    }
    const token = tokenReference(property(node, 'provide'));
    const useClass = reference(property(node, 'useClass'));
    const alias = tokenReference(property(node, 'useExisting'));
    if (!token || (!alias && (!useClass || !Node.isClassDeclaration(useClass)))) {
      providerScope.uncertain = true; diagnostic(`angularProvider: unresolved provider in ${providerScope.qn}: ${node.getText().slice(0, 120)}`); return;
    }
    const multi = property(node, 'multi')?.getText() === 'true';
    const binding: Binding = { token, implementation: useClass && Node.isClassDeclaration(useClass) ? useClass : undefined, alias, multi, scope: providerScope };
    const previous = providerScope.bindings.get(token) ?? [];
    providerScope.bindings.set(token, multi || providerScope.uncertain ? [...previous, binding] : [binding]);
  }
  for (const item of angular) {
    const providerScope = makeScope(item.declaration);
    collectProviders(property(item.metadata, 'providers'), providerScope);
    collectProviders(property(item.metadata, 'viewProviders'), providerScope);
  }
  // Each top-level provider configuration gets its own injector table, even when two
  // bootstraps share a source file. REGISTERS remains owned by the source file slice.
  const configScopes = new Map<Node, ProviderScope>();
  function configScope(owner: Node): ProviderScope {
    let value = configScopes.get(owner);
    if (!value) { value = makeScope(owner); value.qn = model.fileQN(owner); configScopes.set(owner, value); }
    return value;
  }
  for (const file of model.files) for (const declaration of file.getVariableDeclarations()) {
    const initial = declaration.getInitializer();
    if (!initial || declaration.getFirstAncestorByKind(SyntaxKind.ClassDeclaration)) continue;
    if (Node.isObjectLiteralExpression(initial) && property(initial, 'providers')) collectProviders(property(initial, 'providers'), configScope(declaration));
    else if (Node.isArrayLiteralExpression(initial) && initial.getDescendantsOfKind(SyntaxKind.ObjectLiteralExpression).some(object => property(object, 'provide'))) collectProviders(initial, configScope(declaration));
  }
  for (const file of model.files) for (const call of file.getDescendantsOfKind(SyntaxKind.CallExpression)) {
    if (imported(call.getExpression(), '@angular/platform-browser') !== 'bootstrapApplication') continue;
    const component = reference(call.getArguments()[0]);
    if (!component || !Node.isClassDeclaration(component)) continue;
    const config = call.getArguments()[1];
    const resolved = reference(config);
    const declaration = config?.getSymbol()?.getDeclarations().find(Node.isVariableDeclaration) ?? resolved?.getFirstAncestorByKind(SyntaxKind.VariableDeclaration);
    const value = configScope(declaration ?? config ?? call);
    if (!declaration) value.identity = `${model.fileQN(call)}#bootstrap:${qn(component)}`;
    if (resolved && Node.isObjectLiteralExpression(resolved) && !declaration) collectProviders(property(resolved, 'providers'), value);
    else if (config && (!resolved || !Node.isObjectLiteralExpression(resolved))) { value.uncertain = true; diagnostic(`angularProvider: unsupported application config in ${model.relative(call)}`); }
    scopeForClass(component).parents.push(value);
  }
  const modules = angular.filter(item => item.kind === 'NgModule').map(item => item.declaration);
  function moduleReaches(root: ClassDeclaration, target: ClassDeclaration, seen = new Set<Node>()): boolean {
    if (root === target) return true;
    if (seen.has(root)) return false; seen.add(root);
    return (moduleImports.get(root) ?? []).some(child => modules.includes(child) && moduleReaches(child, target, seen));
  }
  const moduleRoots = modules.filter(module => !modules.some(other => other !== module && (moduleImports.get(other) ?? []).includes(module)));
  for (const [cls, owners] of moduleDeclarations) {
    const contexts = moduleRoots.filter(root => owners.some(owner => moduleReaches(root, owner)));
    for (const owner of contexts.length ? contexts : owners) scopeForClass(cls).parents.push(scopeForClass(owner));
  }
  const renderedParents = new Map<ProviderScope, Set<ProviderScope>>();
  for (const providerScope of scopes.values()) for (const bindings of providerScope.bindings.values()) for (const binding of bindings) {
    if (binding.implementation && binding.implementation !== providerScope.owner) scopeForClass(binding.implementation).parents.push(providerScope);
  }
  function lookup(token: Node, providerScope: ProviderScope, seen = new Set<ProviderScope>()): { bindings: Binding[]; certain: boolean } {
    if (seen.has(providerScope)) return { bindings: [], certain: false }; seen.add(providerScope);
    const local = providerScope.bindings.get(token);
    if (local) return { bindings: local, certain: !providerScope.uncertain && local.length === 1 && !local[0].multi };
    const parents = [...(renderedParents.get(providerScope) ?? providerScope.parents)];
    const branches = parents.map(parent => lookup(token, parent, new Set(seen))).filter(result => result.bindings.length);
    // NgModule imports are ordered in one injector: later imported providers win.
    if (Node.isClassDeclaration(providerScope.owner) && modules.includes(providerScope.owner) && branches.length) return branches[branches.length - 1];
    const bindings = [...new Set(branches.flatMap(result => result.bindings))];
    return { bindings, certain: !providerScope.uncertain && branches.length > 0 && branches.every(result => result.certain) && new Set(bindings.map(binding => binding.implementation ?? binding.alias)).size === 1 };
  }
  function aliasInjectors(binding: Binding, consumingScope: ProviderScope): ProviderScope[] {
    if (!Node.isClassDeclaration(binding.scope.owner) || !modules.includes(binding.scope.owner)) return [binding.scope];
    const declaringModule = binding.scope.owner;
    const contexts = new Set<ProviderScope>(); const visited = new Set<ProviderScope>();
    const visit = (current: ProviderScope) => {
      if (visited.has(current)) return; visited.add(current);
      if (Node.isClassDeclaration(current.owner) && moduleRoots.includes(current.owner) && moduleReaches(current.owner, declaringModule)) contexts.add(current);
      for (const parent of renderedParents.get(current) ?? current.parents) visit(parent);
    };
    visit(consumingScope);
    if (!contexts.size && consumingScope.owner === declaringModule) for (const root of moduleRoots) if (moduleReaches(root, declaringModule)) contexts.add(scopeForClass(root));
    return contexts.size ? [...contexts] : [binding.scope];
  }
  function resolveBinding(token: Node, providerScope: ProviderScope, seen = new Set<Node>()): Selection {
    if (seen.has(token)) { diagnostic(`angularProvider: alias cycle at ${qn(token)}`); return { implementations: [], certain: false, token }; }
    seen.add(token);
    const selected = lookup(token, providerScope);
    const implementations = new Set<ClassDeclaration>(); let certain = selected.certain;
    for (const binding of selected.bindings) {
      if (binding.alias) {
        const contexts = aliasInjectors(binding, providerScope);
        const targets = contexts.map(context => resolveBinding(binding.alias!, context, new Set(seen)));
        for (const target of targets) target.implementations.forEach(value => implementations.add(value));
        certain = certain && targets.every(target => target.certain) && new Set(targets.flatMap(target => target.implementations)).size === 1;
      } else if (binding.implementation) implementations.add(binding.implementation);
    }
    if (!selected.bindings.length && Node.isClassDeclaration(token)) { implementations.add(token); certain = true; }
    return { implementations: [...implementations], certain, token };
  }
  for (const providerScope of scopes.values()) for (const [token, bindings] of providerScope.bindings) {
    const selected = resolveBinding(token, providerScope);
    for (const target of selected.implementations) addEdge(providerScope.qn, qn(target), 'REGISTERS', {
      provider_scope: providerScope.identity, token_qn: qn(token), provider_kind: bindings.some(binding => binding.alias) ? 'useExisting' : token === target ? 'class' : 'useClass',
      confidence_band: selected.certain ? 'high' : 'medium', candidate: !selected.certain,
    });
  }
  const selectors = components.flatMap(component => (string(property(component.metadata, 'selector')) ?? '').split(',').map(selector => ({ selector: selector.trim(), component: component.declaration })).filter(item => item.selector));
  function available(owner: ClassDeclaration, target: ClassDeclaration): boolean {
    const item = components.find(item => item.declaration === owner);
    const explicitImports = property(item?.metadata, 'imports');
    const modules = moduleDeclarations.get(owner) ?? [];
    if (!explicitImports && !modules.length) return true;
    const admitted = new Set<Node>(); const visited = new Set<Node>();
    const exported = (cls: ClassDeclaration) => {
      if (componentSet.has(cls)) { admitted.add(cls); return; }
      if (visited.has(cls)) return; visited.add(cls);
      for (const declaration of moduleExports.get(cls) ?? []) exported(declaration);
    };
    if (explicitImports) values(explicitImports).filter(Node.isClassDeclaration).forEach(exported);
    for (const module of modules) {
      for (const [child, owners] of moduleDeclarations) if (owners.includes(module)) admitted.add(child);
      for (const imported of moduleImports.get(module) ?? []) exported(imported);
    }
    return admitted.has(target);
  }

  function htmlReference(owner: ClassDeclaration, expression: string): ClassDeclaration[] {
    const names = expression.trim().replace(/;.*$/, '').split('.');
    if (!names.every(name => /^[\w$]+$/.test(name))) return [];
    if (names[0] === 'this') names.shift();
    let declaration: Node | undefined = classField(owner, names[0]);
    if (!declaration) {
      const file = owner.getSourceFile();
      declaration = file.getClasses().find(cls => cls.getName() === names[0]) ?? file.getVariableDeclaration(names[0]);
      for (const imp of file.getImportDeclarations()) {
        const named = imp.getNamedImports().find(item => (item.getAliasNode()?.getText() ?? item.getName()) === names[0]);
        if (named) { const target = model.module(imp, imp.getModuleSpecifierValue()); declaration = target ? model.exported(target, named.getName()) : undefined; }
        if (imp.getDefaultImport()?.getText() === names[0]) { const target = model.module(imp, imp.getModuleSpecifierValue()); declaration = target ? model.exported(target, 'default') : undefined; }
        if (imp.getNamespaceImport()?.getText() === names[0] && names.length > 1) { const target = model.module(imp, imp.getModuleSpecifierValue()); declaration = target ? model.exported(target, names[1]) : undefined; names.splice(1, 1); }
      }
    }
    let value = reference(declaration);
    for (const name of names.slice(1)) {
      if (value && Node.isClassDeclaration(value)) value = reference(value.getProperty(name));
      else if (value && Node.isObjectLiteralExpression(value)) value = reference(property(value, name));
      else value = undefined;
    }
    return value && Node.isClassDeclaration(value) && componentSet.has(value) ? [value] : [];
  }
  function render(owner: ClassDeclaration, target: ClassDeclaration, templatePath: string, properties: Record<string, unknown> = {}) {
    addEdge(qn(owner), qn(target), 'RENDERS', { template_path: templatePath, confidence_band: 'high', ...properties });
    if (owner !== target) { const parents = renderedParents.get(scopeForClass(target)) ?? new Set<ProviderScope>(); parents.add(scopeForClass(owner)); renderedParents.set(scopeForClass(target), parents); }
  }
  for (const item of components) {
    const inline = string(property(item.metadata, 'template'));
    const templateUrl = string(property(item.metadata, 'templateUrl'));
    if ((property(item.metadata, 'templateUrl') && templateUrl === undefined) || (property(item.metadata, 'template') && inline === undefined)) { complete = false; diagnostic(`angularTemplateUnresolved: ${model.relative(item.declaration)}`); continue; }
    let html = inline; let templatePath = model.relative(item.declaration);
    if (templateUrl !== undefined) {
      const absolute = path.resolve(path.dirname(item.declaration.getSourceFile().getFilePath()), templateUrl);
      templatePath = path.relative(model.root, absolute).replace(/\\/g, '/');
      if (templatePath.startsWith('../') || path.isAbsolute(templatePath) || (scope && !scope.includesFile(absolute))) { diagnostic(`angularTemplateScope: refused ${templatePath}`); continue; }
      try {
        const real = fs.realpathSync(absolute);
        const realRelative = path.relative(fs.realpathSync(model.root), real);
        if (realRelative.startsWith('..') || path.isAbsolute(realRelative) || (scope && !scope.includesFile(path.join(model.root, realRelative)))) { diagnostic(`angularTemplateScope: refused symlink ${templatePath}`); continue; }
        html = fs.readFileSync(real, 'utf8'); processedTemplates.add(templatePath);
      } catch (error) { complete = false; diagnostic(`angularTemplateRead: ${templatePath}: ${String(error)}`); continue; }
    }
    if (html === undefined) continue;
    const clean = html.replace(/<!--[\s\S]*?-->/g, '').replace(/<(script|style)\b[^>]*>[\s\S]*?<\/\1\s*>/gi, '');
    for (const tag of clean.matchAll(/<([a-zA-Z][\w:.-]*)\b((?:"[^"]*"|'[^']*'|[^'">])*)>/g)) {
      const attributes = new Map<string, string>();
      for (const attr of tag[2].matchAll(/([^\s=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/g)) attributes.set(attr[1].replace(/^(?:\[\(|\[|\(|\*)|(?:\)\]|\]|\))$/g, ''), attr[2] ?? attr[3] ?? attr[4] ?? '');
      const matches = selectors.filter(({selector, component}) => available(item.declaration, component) && matchesSelector(selector, tag[1], attributes));
      const targets = new Set(matches.map(match => match.component));
      for (const {selector, component} of matches) render(item.declaration, component, templatePath, { render_kind: 'selector', selector, ...(targets.size > 1 ? {confidence_band:'medium', candidate:true} : {}) });
      const outlet = attributes.get('ngComponentOutlet');
      if (outlet) {
        const targets = htmlReference(item.declaration, outlet);
        if (!targets.length) diagnostic(`angularDynamicComponent: unresolved outlet ${model.relative(item.declaration)}: ${outlet.slice(0, 120)}`);
        for (const target of targets) render(item.declaration, target, templatePath, { render_kind: 'ngComponentOutlet' });
      }
    }
  }
  function fieldDeclaration(expression: Node): Node | undefined {
    if (Node.isPropertyAccessExpression(expression) && expression.getExpression().getKind() === SyntaxKind.ThisKeyword) {
      const cls = classOf(expression);
      return cls ? classField(cls, expression.getName()) : undefined;
    }
    return expression.getSymbol()?.getDeclarations().find(declaration => model.byPath.has(model.relative(declaration)) &&
      (Node.isParameterDeclaration(declaration) || Node.isPropertyDeclaration(declaration) || Node.isVariableDeclaration(declaration)));
  }
  function typeNode(declaration: Node | undefined): Node | undefined { return declaration && (Node.isPropertyDeclaration(declaration) || Node.isParameterDeclaration(declaration) || Node.isVariableDeclaration(declaration)) ? declaration.getTypeNode() : undefined; }
  function initialNode(declaration: Node | undefined): Node | undefined { return declaration && (Node.isPropertyDeclaration(declaration) || Node.isParameterDeclaration(declaration) || Node.isVariableDeclaration(declaration)) ? declaration.getInitializer() : undefined; }
  function frameworkType(expression: Node, packageName: string, expected: string, seen = new Set<Node>()): boolean {
    if (seen.has(expression)) return false; seen.add(expression);
    const declaration = fieldDeclaration(expression);
    const typed = typeNode(declaration);
    if (typed && Node.isTypeReference(typed) && imported(typed.getTypeName(), packageName) === expected) return true;
    const initial = initialNode(declaration);
    if (initial && Node.isCallExpression(initial) && imported(initial.getExpression(), '@angular/core') === 'inject' && imported(initial.getArguments()[0], packageName) === expected) return true;
    return !!initial && initial !== expression && frameworkType(initial, packageName, expected, seen);
  }
  function injectionCall(call: Node): boolean {
    if (!Node.isCallExpression(call)) return false;
    if (imported(call.getExpression(), '@angular/core') === 'inject') return true;
    const expression = call.getExpression();
    return Node.isPropertyAccessExpression(expression) && expression.getName() === 'get' && frameworkType(expression.getExpression(), '@angular/core', 'Injector');
  }
  // Render context must be established before any child injection is resolved.
  for (const cls of classes) for (const call of cls.getDescendantsOfKind(SyntaxKind.CallExpression)) {
    if (classOf(call) !== cls) continue;
    const expression = call.getExpression();
    let dynamic: string | undefined;
    if (imported(expression, '@angular/core') === 'createComponent') dynamic = 'createComponent';
    if (Node.isPropertyAccessExpression(expression)) {
      if (expression.getName() === 'createComponent' && frameworkType(expression.getExpression(), '@angular/core', 'ViewContainerRef')) dynamic = 'viewContainer.createComponent';
      if (expression.getName() === 'open' && frameworkType(expression.getExpression(), '@angular/material/dialog', 'MatDialog')) dynamic = 'dialog.open';
      if (expression.getName() === 'openFromComponent' && frameworkType(expression.getExpression(), '@angular/material/snack-bar', 'MatSnackBar')) dynamic = 'snackbar.openFromComponent';
    }
    if (dynamic) {
      const target = reference(call.getArguments()[0]);
      if (target && Node.isClassDeclaration(target) && componentSet.has(target)) render(cls, target, model.relative(cls), { render_kind: dynamic, owning_callable: model.owner(call) });
      else diagnostic(`angularDynamicComponent: unresolved ${dynamic} in ${model.relative(cls)}`);
    }
  }
  function select(tokenExpression: Node | undefined, context: Node, contract?: InterfaceDeclaration): Selection {
    const field = tokenExpression ? fieldDeclaration(tokenExpression) : undefined;
    const token = tokenReference(tokenExpression);
    const runtime = (!!field && Node.isPropertyDeclaration(field) && field.getDecorators().some(dec => imported(dec.getCallExpression()?.getExpression() ?? dec.getExpression(), '@angular/core') === 'Input')) || !!token && (Node.isParameterDeclaration(token) || Node.isPropertyDeclaration(token));
    const providerScope = scopeForContext(context);
    if (!runtime && token) return resolveBinding(token, providerScope);
    const iface = contract ?? tokenInterface(field ?? token);
    const implementations = new Set<ClassDeclaration>();
    const candidateTokens = new Set<Node>(); const seenScopes = new Set<ProviderScope>();
    const visit = (current: ProviderScope) => {
      if (seenScopes.has(current)) return; seenScopes.add(current);
      for (const candidate of current.bindings.keys()) if (iface && tokenInterface(candidate) === iface) candidateTokens.add(candidate);
      current.parents.forEach(visit);
    };
    visit(providerScope);
    for (const candidate of candidateTokens) resolveBinding(candidate, providerScope).implementations.forEach(value => implementations.add(value));
    if (!implementations.size && iface && !providerScope.parents.length && !providerScope.bindings.size && !renderedParents.has(providerScope)) (implementers.get(iface) ?? []).forEach(value => implementations.add(value));
    return { implementations: [...implementations], certain: false, token, runtime: true };
  }
  function receiver(expression: Node, context: Node, seen = new Set<Node>()): Receiver {
    if (seen.has(expression)) return {}; seen.add(expression);
    if (Node.isCallExpression(expression) && injectionCall(expression)) {
      const typed = typeInterface(expression.getTypeArguments()[0]);
      const contract = typed ?? tokenInterface(reference(expression.getArguments()[0]));
      return { contract, selection: select(expression.getArguments()[0], context, contract), injected: true };
    }
    const declaration = fieldDeclaration(expression);
    if (declaration) {
      const typed = typeInterface(typeNode(declaration));
      const initial = initialNode(declaration);
      if (initial) {
        const evidence = receiver(initial, context, seen);
        if (evidence.selection || evidence.contract) return { ...evidence, contract: typed ?? evidence.contract };
      }
      if (Node.isParameterDeclaration(declaration)) {
        const inject = declaration.getDecorators().find(dec => imported(dec.getCallExpression()?.getExpression() ?? dec.getExpression(), '@angular/core') === 'Inject');
        if (inject) return { contract: typed, selection: select(inject.getArguments()[0], context, typed), injected: true };
      }
      if (typed) return { contract: typed };
      const declaredType = reference(typeNode(declaration));
      if (declaredType && Node.isClassDeclaration(declaredType)) {
        const injected = isConstructorInjection(declaration, context);
        return { selection: injected ? resolveBinding(declaredType, scopeForContext(context)) : { implementations: [declaredType], certain: true }, injected };
      }
    }
    return {};
  }
  function isConstructorInjection(declaration: Node, context: Node): boolean {
    if (!Node.isParameterDeclaration(declaration) || !Node.isConstructorDeclaration(declaration.getParent()) || !Node.isClassDeclaration(context)) return false;
    const injector = scopes.get(context);
    return angular.some(item => item.declaration === context) || !!injector && (injector.bindings.size > 0 || injector.parents.length > 0);
  }
  function scopeForContext(context: Node): ProviderScope {
    return configScopes.get(context) ?? makeScope(context);
  }
  function dispatch(call: import('ts-morph').CallExpression, context: Node): void {
    const expression = call.getExpression();
    if (!Node.isPropertyAccessExpression(expression)) return;
    const evidence = receiver(expression.getExpression(), context);
    const member = evidence.contract ? members(evidence.contract).get(expression.getName()) : undefined;
    if (!member && (evidence.contract || !evidence.selection?.implementations.length)) return;
    const targets = evidence.selection?.implementations ?? [];
    const targetQNs = targets.map(target => implementationMethod(target, expression.getName())).filter((target): target is string => !!target);
    if (evidence.injected && targets.length) {
      const original = model.target(expression);
      if (original && !targetQNs.includes(original)) {
        const key = callKey(model.owner(call), original);
        if (provisionalCalls.has(key)) { redirectedCallSites.add(call); redirectedCallKeys.add(key); }
      }
    }
    const properties = {
      call_kind: evidence.contract ? 'interface_dispatch' : evidence.injected ? 'injected_dispatch' : 'typed_dispatch',
      ...(evidence.contract ? { interface_qn: qn(evidence.contract) } : {}),
      confidence_band: evidence.selection?.certain ? 'high' : 'medium', candidate: !evidence.selection?.certain,
      ...(evidence.selection?.token ? { token_qn: qn(evidence.selection.token) } : {}),
    };
    if (targets.length) {
      for (const targetQN of targetQNs) addEdge(model.owner(call), targetQN, 'CALLS', properties);
    } else if (member) {
      const owner = member.getFirstAncestorByKind(SyntaxKind.InterfaceDeclaration)!;
      addEdge(model.owner(call), `${qn(owner)}.${expression.getName()}`, 'CALLS', properties);
    }
  }
  for (const cls of classes) {
    const sites: { name: string; expression?: Node; contract?: InterfaceDeclaration }[] = [];
    for (const ctor of cls.getConstructors()) for (const parameter of ctor.getParameters()) {
      const inject = parameter.getDecorators().find(dec => imported(dec.getCallExpression()?.getExpression() ?? dec.getExpression(), '@angular/core') === 'Inject');
      if (inject) sites.push({ name: parameter.getName(), expression: inject.getArguments()[0], contract: typeInterface(parameter.getTypeNode()) });
      else if (isConstructorInjection(parameter, cls)) {
        const token = reference(parameter.getTypeNode());
        if (token && Node.isClassDeclaration(token)) sites.push({ name: parameter.getName(), expression: parameter.getTypeNode() });
      }
    }
    for (const field of cls.getProperties()) {
      const initial = field.getInitializer();
      if (initial && injectionCall(initial) && Node.isCallExpression(initial)) sites.push({ name: field.getName(), expression: initial.getArguments()[0], contract: typeInterface(field.getTypeNode()) ?? typeInterface(initial.getTypeArguments()[0]) });
    }
    for (const call of cls.getDescendantsOfKind(SyntaxKind.CallExpression)) {
      if (classOf(call) !== cls || !injectionCall(call) || Node.isPropertyDeclaration(call.getParent())) continue;
      sites.push({ name: model.owner(call), expression: call.getArguments()[0], contract: typeInterface(call.getTypeArguments()[0]) });
    }
    const injectionNames = new Set(sites.map(site => site.name));
    for (let index = edges.length - 1; index >= 0; index--) if (edges[index].type === 'INJECTS' && edges[index].sourceQN === qn(cls) && injectionNames.has(String(edges[index].properties?.parameter_name))) edges.splice(index, 1);
    for (const site of sites) {
      const selected = select(site.expression, cls, site.contract);
      for (const target of selected.implementations) addEdge(qn(cls), qn(target), 'INJECTS', {
        parameter_name: site.name, token_qn: selected.token ? qn(selected.token) : undefined, provider_scope: scopeForClass(cls).qn,
        confidence_band: selected.certain ? 'high' : 'medium', candidate: !selected.certain, runtime_token: !!selected.runtime, ...(selected.runtime && site.expression ? { runtime_token_expression: site.expression.getText() } : {}),
      });
      if (!selected.implementations.length && site.contract) addEdge(qn(cls), qn(site.contract), 'INJECTS', { parameter_name: site.name, confidence_band: 'medium', candidate: true });
    }
    for (const call of cls.getDescendantsOfKind(SyntaxKind.CallExpression)) {
      if (classOf(call) !== cls) continue;
      dispatch(call, cls);
    }
  }
  for (const file of model.files) for (const call of file.getDescendantsOfKind(SyntaxKind.CallExpression)) {
    if (classOf(call)) continue;
    dispatch(call, call.getAncestors().find(ancestor => configScopes.has(ancestor)) ?? file);
  }
  if (redirectedCallKeys.size) {
    const retainedKeys = new Set<string>();
    for (const file of model.files) for (const call of file.getDescendantsOfKind(SyntaxKind.CallExpression)) {
      if (redirectedCallSites.has(call)) continue;
      const target = model.target(call.getExpression());
      if (!target) continue;
      const key = callKey(model.owner(call), target);
      if (redirectedCallKeys.has(key)) retainedKeys.add(key);
    }
    const superseded = new Set<PendingEdgeDto>();
    for (const key of redirectedCallKeys) if (!retainedKeys.has(key)) for (const edge of provisionalCalls.get(key) ?? []) superseded.add(edge);
    for (let index = edges.length - 1; index >= 0; index--) if (superseded.has(edges[index])) edges.splice(index, 1);
  }
  diagnostic(`angularLinks: ${JSON.stringify({ components: components.length, interfaceMembers: [...interfaceMembers.values()].reduce((sum, values) => sum + values.size, 0), providerScopes: scopes.size, processedTemplates: processedTemplates.size, renders: edges.filter(edge => edge.type === 'RENDERS' && edge.properties?.framework === 'angular').length, injections: edges.filter(edge => edge.type === 'INJECTS').length, registrations: edges.filter(edge => edge.type === 'REGISTERS' && edge.properties?.framework === 'angular').length, dispatchCalls: edges.filter(edge => edge.type === 'CALLS' && edge.properties?.framework === 'angular').length })}`);
  return { complete, processedTemplates: [...processedTemplates].sort() };
}

function matchesSelector(selector: string, tag: string, attributes: Map<string, string>): boolean {
  const negatives: string[] = [];
  const positive = selector.replace(/:not\(([^)]+)\)/g, (_, value: string) => { negatives.push(value); return ''; });
  if (negatives.some(value => matchesSelector(value, tag, attributes))) return false;
  const element = positive.match(/^[\w-]+/);
  if (element && element[0].toLowerCase() !== tag.toLowerCase()) return false;
  for (const match of positive.matchAll(/\[([^\]=\s]+)(?:\s*=\s*["']?([^\]"']+)["']?)?\]/g)) if (!attributes.has(match[1]) || (match[2] !== undefined && attributes.get(match[1]) !== match[2])) return false;
  const classNames = new Set((attributes.get('class') ?? '').split(/\s+/));
  for (const match of positive.matchAll(/\.([\w-]+)/g)) if (!classNames.has(match[1])) return false;
  return !!positive && !positive.replace(/^[\w-]+/, '').replace(/\[[^\]]+\]/g, '').replace(/\.[\w-]+/g, '').trim();
}
