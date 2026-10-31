import { ts, Node, SourceFile, SyntaxKind, Project } from 'ts-morph';
import * as path from 'path';
import { BoundedTextReader } from './boundedTextReader';
import { SemanticModel, sameSymbol } from './semanticModel';
import { DiscoveryScope } from './discoveryScope';
import type { GraphNodeDto, PendingEdgeDto } from './protocol';

/** Browser routes are distinct from backend endpoints. Resolve modules through the admitted model,
 * parse configuration as source, and give every synthetic fact a stable source slice owner. */
export function analyzeRoutes(model: SemanticModel, nodes: GraphNodeDto[], edges: PendingEdgeDto[],
  scope?: DiscoveryScope, diagnostic: (message: string) => void = model.diagnostic,
  reader = new BoundedTextReader(model.root, scope ? file => scope.includesFile(file) : undefined, {}, diagnostic)): { complete: boolean } {
  return new RouteAnalysis(model, nodes, edges, scope, diagnostic, reader).run();
}
class RouteAnalysis {
  private complete = true;
  private readonly emitted = new Map<string, GraphNodeDto>();
  private readonly edgeKeys = new Set<string>();
  private readonly jsxCache = new Map<Node, { targets: string[]; navigate?: Node }>();
  private readonly inputs: string[];
  private readonly appRoots = new Map<string, string>();
  private readonly markerDirectories = new Set<string>();
  private readonly frameworkImports = new Map<SourceFile, Map<string, { named: Map<string, Node[]>; namespaces: Node[] }>>();
  private readonly parsedConfigs = new Project({ useInMemoryFileSystem: true });
  constructor(private readonly model: SemanticModel, private readonly nodes: GraphNodeDto[],
    private readonly edges: PendingEdgeDto[], private readonly scope: DiscoveryScope | undefined,
    private readonly diagnostic: (message: string) => void, private readonly reader: BoundedTextReader) {
    this.inputs = scope ? scope.absoluteFiles().map(file => path.relative(model.root, file).replace(/\\/g, '/')) : model.files.map(file => model.relative(file));
    if (!scope) {
      const knownInputs=new Set(this.inputs); const visitedDirectories=new Set<string>();
      for (const file of model.files) {
        model.work.charge(model.relative(file).length+1); let directory=path.posix.dirname(model.relative(file));
        while(!visitedDirectories.has(directory)) {
          model.work.charge(directory.length+1); visitedDirectories.add(directory);
          for(const name of ['package.json','angular.json','tsconfig.json','next.config.ts','next.config.js','next.config.mjs','next.config.cjs']) {
            model.work.charge(directory.length+name.length+2); const input=directory==='.'?name:`${directory}/${name}`;
            if(!knownInputs.has(input)&&reader.exists(path.join(model.root,input))) { knownInputs.add(input); this.inputs.push(input); }
          }
          if(directory==='.') break; directory=path.posix.dirname(directory);
        }
      }
    }
    for (const input of this.inputs) {
      model.work.charge(input.length + 1);
      if (/^(package\.json|angular\.json|tsconfig[^/]*\.json|next\.config\.[^/]+)$/.test(path.posix.basename(input))) this.markerDirectories.add(path.posix.dirname(input));
    }
    for (const node of nodes) if (node.properties.route_kind === 'ui') this.emitted.set(node.qualifiedName, node);
  }
  run(): { complete: boolean } { this.angular(); this.react(); this.next(); return { complete: this.complete && this.reader.complete }; }
  private relative(node: Node): string { return this.model.relative(node); }
  private report(node: Node, reason: string, incomplete = false): void { if (incomplete) this.complete = false; this.diagnostic(`routeUnresolved: ${this.relative(node)}:${node.getStartLineNumber()} ${reason}`); }
  private appRoot(file: string): string {
    this.model.work.charge(file.length + 1);
    const cached = this.appRoots.get(file); if (cached !== undefined) return cached;
    let directory = path.posix.dirname(file);
    while (directory !== '.') {
      this.model.work.charge(directory.length + 1);
      if (this.markerDirectories.has(directory)) { this.appRoots.set(file, directory); return directory; }
      directory = path.posix.dirname(directory);
    }
    this.appRoots.set(file, '.');
    return '.';
  }
  private read(file: string): string | undefined {
    const absolute = path.join(this.model.root, file);
    if (this.scope && !this.scope.includesFile(absolute)) return undefined;
    const source = this.model.byPath.get(file);
    if (source) return source.getFullText();
    return this.reader.read(absolute, true);
  }
  private value(node: Node | undefined, seen = new Set<Node>()): Node | undefined {
    this.model.work.charge(seen.size + 1);
    if (!node || seen.has(node) || seen.size > 80) return undefined;
    seen.add(node);
    if (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isSatisfiesExpression(node) || Node.isTypeAssertion(node)) return this.value(node.getExpression(), seen);
    if (Node.isVariableDeclaration(node) || Node.isPropertyAssignment(node) || Node.isPropertyDeclaration(node)) return this.value(node.getInitializer(), seen);
    if (Node.isIdentifier(node) || Node.isPropertyAccessExpression(node)) {
      const resolved = this.model.resolve(node);
      return resolved && resolved !== node && this.model.byPath.has(this.relative(resolved)) ? this.value(resolved, seen) : undefined;
    }
    return node;
  }
  private text(node: Node | undefined): string | undefined {
    const value = this.value(node);
    if (!value || !(Node.isStringLiteral(value) || Node.isNoSubstitutionTemplateLiteral(value))) return undefined;
    this.model.work.charge(value.getFullWidth() + 1);
    return value.getLiteralText();
  }
  private prop(node: Node, name: string, seen = new Set<Node>()): Node | undefined {
    this.model.work.charge(seen.size + name.length + 1);
    const object = this.value(node);
    if (!object || seen.has(object) || !Node.isObjectLiteralExpression(object)) return undefined;
    seen.add(object); let found: Node | undefined;
    this.model.work.charge(object.compilerNode.properties.length + 1);
    for (const prop of object.getProperties()) {
      this.model.work.charge(prop.getFullWidth() + 1);
      if (Node.isSpreadAssignment(prop)) { const candidate = this.prop(prop.getExpression(), name, this.history(seen)); if (candidate) found = candidate; }
      if (Node.isPropertyAssignment(prop) && prop.getName().replace(/^['"]|['"]$/g, '') === name) found = prop.getInitializer();
      if (Node.isShorthandPropertyAssignment(prop) && prop.getName() === name) found = prop.getNameNode();
    }
    return found;
  }
  private array(node: Node | undefined, seen = new Set<Node>(), strict = false): Node[] {
    this.model.work.charge(seen.size + 1);
    const value = this.value(node);
    if (!value || seen.has(value) || seen.size > 80 || !Node.isArrayLiteralExpression(value)) {
      if (strict && node) this.report(node, 'route array/spread is dynamic or cyclic', true);
      return [];
    }
    seen.add(value);
    this.model.work.charge(value.compilerNode.elements.length * 8 + 16);
    const result: Node[] = [];
    for (const item of value.getElements()) {
      this.model.work.charge(8);
      if (Node.isSpreadElement(item)) {
        const expanded = this.array(item.getExpression(), this.history(seen), strict);
        // Charge before copying an expanded shared/spread array, not after allocating its output.
        this.model.work.charge(expanded.length * 8 + 16);
        for (const child of expanded) result.push(child);
      } else result.push(item);
    }
    return result;
  }
  private history(seen: Set<Node>): Set<Node> { this.model.work.charge(seen.size + 1); return new Set(seen); }
  private layouts(ancestors: string[], handlers: string[]): string[] {
    this.model.work.charge(ancestors.length + handlers.length + 1); return [...ancestors, ...handlers];
  }
  private joinPath(prefix: string, segment: string): string {
    this.model.work.charge((prefix.length + segment.length) * 2 + 8); return joinPath(prefix, segment);
  }
  private imported(node: Node, packageName: string, name: string): boolean {
    this.model.work.charge(packageName.length + name.length + 1);
    const file = node.getSourceFile(); let packages = this.frameworkImports.get(file);
    if (!packages) {
      packages = new Map(); this.frameworkImports.set(file, packages);
      for (const imp of file.getImportDeclarations()) {
        this.model.work.charge(imp.getModuleSpecifier().getFullWidth() + 1);
        const specifier = imp.getModuleSpecifierValue();
        let bindings = packages.get(specifier);
        if (!bindings) { bindings = { named: new Map(), namespaces: [] }; packages.set(specifier, bindings); }
        for (const item of imp.getNamedImports()) {
          this.model.work.charge(item.getFullWidth() + 1);
          const exportedName = item.getName();
          if (!bindings.named.has(exportedName)) bindings.named.set(exportedName, []);
          bindings.named.get(exportedName)!.push(item.getAliasNode() ?? item.getNameNode());
        }
        const namespace = imp.getNamespaceImport(); if (namespace) bindings.namespaces.push(namespace);
      }
    }
    const bindings = packages.get(packageName); if (!bindings) return false;
    if (Node.isIdentifier(node)) for (const binding of bindings.named.get(name) ?? []) { this.model.work.charge(); if (sameSymbol(node, binding)) return true; }
    if (Node.isPropertyAccessExpression(node)) {
      this.model.work.charge(node.getNameNode().getFullWidth() + 1);
      if (node.getName() === name) for (const namespace of bindings.namespaces) { this.model.work.charge(); if (sameSymbol(node.getExpression(), namespace)) return true; }
    }
    return false;
  }
  private edge(source: string, target: string, type: string, owner: Node, extra: Record<string, unknown> = {}): void {
    this.model.work.charge();
    if (source === target) return;
    // Traversal may discover an edge while visiting a descendant page or route literal. Persist
    // outgoing facts with their actual source slice so signature changes replace that source's
    // edges, including shared layout chains whose endpoints both outlive the visited page.
    const declaration = this.model.byQN.get(source);
    const sourceFile = declaration ? this.relative(declaration) : this.emitted.get(source)?.filePath ?? this.relative(owner);
    // Reserve worst-case JSON escaping before allocating the deduplication key.
    this.model.work.charge((source.length + target.length + sourceFile.length) * 6 + type.length + 128);
    this.properties(extra);
    const properties = { extraction_source_file: sourceFile, route_kind: 'ui', ...extra };
    const key = JSON.stringify([source, target, type, properties]);
    if (this.edgeKeys.has(key)) return;
    this.edgeKeys.add(key); this.edges.push(this.model.work.createEdge(()=>({ sourceQN: source, targetQN: target, type, properties })));
  }
  private properties(extra: Record<string, unknown>): void {
    for (const key in extra) { const value = extra[key]; this.model.work.charge(key.length + (typeof value === 'string' ? value.length * 6 : 1) + 8); }
  }
  private route(owner: Node, framework: string, fullPath: string, extra: Record<string, unknown> = {}, appRoot = this.appRoot(this.relative(owner))): string {
    this.model.work.charge(this.model.repository.length + appRoot.length + framework.length + fullPath.length * 2 + this.relative(owner).length + 192);
    this.properties(extra);
    const qualifiedName = `ui-route:${this.model.repository}:${appRoot}:${framework}:${fullPath}`;
    if (!this.emitted.has(qualifiedName)) {
      const sourceOwner = this.model.owner(owner);
      this.model.work.charge(sourceOwner.length * 6 + 1);
      const node: GraphNodeDto = this.model.work.createNode(()=>({ label: 'Route', name: fullPath, qualifiedName, filePath: this.relative(owner),
        startLine: owner.getStartLineNumber(), endLine: owner.getEndLineNumber(), properties: {
          route_kind: 'ui', framework, full_path: fullPath, app_root: appRoot,
          extraction_source_file: this.relative(owner), source_owner: sourceOwner, ...extra } }));
      this.nodes.push(node); this.emitted.set(qualifiedName, node);
    } else Object.assign(this.emitted.get(qualifiedName)!.properties, extra);
    return qualifiedName;
  }
  private angularRegistration(call: Node): boolean {
    if (!Node.isCallExpression(call)) return false;
    const expression = call.getExpression();
    return this.imported(expression, '@angular/router', 'provideRouter') ||
      (Node.isPropertyAccessExpression(expression) && ['forRoot', 'forChild'].includes(expression.getName()) && this.imported(expression.getExpression(), '@angular/router', 'RouterModule'));
  }
  private angular(): void {
    const registrations = this.model.files.flatMap(file => {
      this.model.work.charge(file.getFullWidth() + 1);
      return file.getDescendantsOfKind(SyntaxKind.CallExpression).filter(call => this.angularRegistration(call));
    });
    const consumed = new Set<Node>();
    const rootsInModule = (module: Node | undefined, seen = new Set<Node>()): Node[] => {
      this.model.work.charge(seen.size + 1);
      if (!module || seen.has(module) || seen.size > 60) return [];
      seen.add(module);
      const value = this.value(module);
      if (value && Node.isArrayLiteralExpression(value)) return [value];
      if (!Node.isClassDeclaration(module)) return [];
      const roots: Node[] = [];
      for (const decorator of module.getDecorators()) {
        const call = decorator.getCallExpression();
        if (!call || !this.imported(call.getExpression(), '@angular/core', 'NgModule')) continue;
        const metadata = call.getArguments()[0]; if (!metadata) continue;
        for (const imp of this.array(this.prop(metadata, 'imports'))) {
          if (Node.isCallExpression(imp) && this.angularRegistration(imp)) {
            const root = this.value(imp.getArguments()[0]); if (root) roots.push(root);
          } else {
            const nested = rootsInModule(this.model.resolve(imp), this.history(seen));
            this.model.work.charge(nested.length + 1); for (const root of nested) roots.push(root);
          }
        }
      }
      return roots;
    };
    const lazy = (node: Node): { target?: Node; arrays: Node[] } => {
      const literal = this.text(node);
      if (literal !== undefined) {
        const [specifier, name = 'default'] = literal.split('#');
        const file = this.model.module(node, specifier);
        const target = file ? this.model.exported(file, name) : undefined;
        if (!target) this.report(node, `unresolved legacy lazy module ${literal}`, true);
        return { target, arrays: rootsInModule(target) };
      }
      const loader = this.value(node) ?? node;
      this.model.work.charge(loader.getFullWidth() + 1);
      const imports = [loader, ...loader.getDescendants()].filter(Node.isCallExpression).filter(call => call.getExpression().getKind() === SyntaxKind.ImportKeyword);
      const load = imports.length === 1 ? imports[0] : undefined;
      const specifier = load ? this.text(load.getArguments()[0]) : undefined;
      const file = specifier && load ? this.model.module(load, specifier) : undefined;
      let exportName = 'default';
      const then = loader.getDescendantsOfKind(SyntaxKind.CallExpression).find(call => Node.isPropertyAccessExpression(call.getExpression()) && call.getExpression().getText().endsWith('.then'));
      // Both .then(m => m.Named) and async () => (await import(...)).Named are static.
      const callback = then?.getArguments()[0] ?? (Node.isArrowFunction(loader) || Node.isFunctionExpression(loader) ? loader : undefined);
      if (callback && (Node.isArrowFunction(callback) || Node.isFunctionExpression(callback))) {
        const body = callback.getBody();
        let returned = Node.isBlock(body) ? body.getDescendantsOfKind(SyntaxKind.ReturnStatement)[0]?.getExpression() : body;
        while (returned && (Node.isParenthesizedExpression(returned) || Node.isAwaitExpression(returned))) returned = returned.getExpression();
        if (returned && Node.isPropertyAccessExpression(returned)) exportName = returned.getName();
        else if (returned && Node.isIdentifier(returned)) {
          const parameter = callback.getParameters()[0]?.getNameNode();
          if (parameter && Node.isObjectBindingPattern(parameter)) exportName = parameter.getElements().find(item => item.getName() === returned.getText())?.getPropertyNameNode()?.getText() ?? returned.getText();
        } else if (returned && Node.isElementAccessExpression(returned)) exportName = this.text(returned.getArgumentExpression()) ?? 'default';
      }
      const target = file ? this.model.exported(file, exportName) ?? (exportName === 'default' ? this.value(file.getExportAssignments()[0]?.getExpression()) : undefined) : undefined;
      if (!target) this.report(node, `unresolved lazy export ${specifier ?? '(dynamic)'}#${exportName}`, node.getParentIfKind(SyntaxKind.PropertyAssignment)?.getName() === 'loadChildren');
      return { target, arrays: rootsInModule(target) };
    };
    const discover = (array: Node, seen = new Set<Node>()): void => {
      this.model.work.charge(seen.size + 1);
      const value = this.value(array); if (!value || seen.has(value) || seen.size > 80) return; seen.add(value);
      for (const item of this.array(value, new Set(), true)) {
        const child = this.value(this.prop(item, 'children'));
        if (child) { consumed.add(child); discover(child, this.history(seen)); }
        const load = this.prop(item, 'loadChildren');
        if (load) for (const nested of lazy(load).arrays) { consumed.add(nested); discover(nested, this.history(seen)); }
      }
    };
    for (const registration of registrations) { const array = registration.getArguments()[0]; if (array) discover(array); }
    const walk = (array: Node, prefix: string, parent: string | undefined, app: string, seen = new Set<Node>(), layouts: string[] = []): void => {
      this.model.work.charge(seen.size + 1);
      const value = this.value(array); if (!value || seen.has(value) || seen.size > 80) { this.report(array, 'Angular route cycle or traversal limit', true); return; }
      if (!Node.isArrayLiteralExpression(value)) { this.report(array, 'Angular route array is not statically enumerable', true); return; }
      seen.add(value);
      for (const raw of this.array(value, new Set(), true)) {
        const item = this.value(raw); if (!item || !Node.isObjectLiteralExpression(item)) continue;
        const pathNode = this.prop(item, 'path'); const segment = pathNode ? this.text(pathNode) : '';
        if (segment === undefined) { this.report(item, 'dynamic Angular route path', true); continue; }
        const fullPath = this.joinPath(prefix, segment); const redirectNode = this.prop(item, 'redirectTo');
        const route = this.route(item, 'angular', fullPath, { ...(redirectNode ? { redirect_to: this.text(redirectNode) ?? '(dynamic)' } : {}), ...(segment === '' ? { empty_path: true } : {}) }, app);
        if (parent) this.edge(parent, route, 'RENDERS', item);
        const component = this.model.target(this.prop(item, 'component'));
        const handlers = component ? [component] : [];
        const lazyChildren: Node[] = [];
        for (const key of ['loadComponent', 'loadChildren']) {
          const load = this.prop(item, key); if (!load) continue;
          const resolved = lazy(load); const target = resolved.target && this.model.declarations.get(resolved.target);
          if (target) this.edge(route, target, 'HANDLES', item, { lazy: true });
          if (key === 'loadComponent' && target) handlers.push(target);
          this.model.work.charge(resolved.arrays.length + 1);
          for (const child of resolved.arrays) lazyChildren.push(child);
        }
        const children = this.prop(item, 'children');
        for (const handler of handlers) {
          if (handler === component) this.edge(route, handler, 'HANDLES', item);
          if (children || lazyChildren.length) this.edge(route, handler, 'RENDERS', item, { layout: true });
          for (const layout of layouts) this.edge(layout, handler, 'RENDERS', item, { angular_route_outlet: true });
        }
        const ancestors = this.layouts(layouts, handlers);
        if (children) walk(children, fullPath, route, app, this.history(seen), ancestors);
        for (const child of lazyChildren) walk(child, fullPath, route, app, this.history(seen), ancestors);
      }
    };
    for (const registration of registrations) {
      const array = this.value(registration.getArguments()[0]);
      const expression = registration.getExpression();
      const root = this.imported(expression, '@angular/router', 'provideRouter') || Node.isPropertyAccessExpression(expression) && expression.getName() === 'forRoot';
      if (array && (root || !consumed.has(array))) walk(array, '/', undefined, this.appRoot(this.relative(registration)));
      else if (!array) this.report(registration, 'Angular route registration is not statically enumerable', true);
    }
  }
  private jsxName(node: Node): Node | undefined {
    return Node.isJsxElement(node) ? node.getOpeningElement().getTagNameNode() : Node.isJsxSelfClosingElement(node) || Node.isJsxOpeningElement(node) ? node.getTagNameNode() : undefined;
  }
  private routerTag(node: Node, name: string): boolean {
    const tag = this.jsxName(node); return !!tag && ['react-router', 'react-router-dom'].some(pkg => this.imported(tag, pkg, name));
  }
  private jsxProp(node: Node, name: string): Node | undefined {
    const opening = Node.isJsxElement(node) ? node.getOpeningElement() : node;
    if (!Node.isJsxOpeningElement(opening) && !Node.isJsxSelfClosingElement(opening)) return undefined;
    const attr = opening.getAttribute(name); if (!attr || !Node.isJsxAttribute(attr)) return undefined;
    const init = attr.getInitializer(); return init && Node.isJsxExpression(init) ? init.getExpression() : init;
  }
  private jsxFacts(expression: Node | undefined): { targets: string[]; navigate?: Node } {
    const value = this.value(expression); if (!value) return { targets: [] };
    const cached = this.jsxCache.get(value); if (cached) return cached;
    const targets = new Set<string>(); let navigate: Node | undefined;
    this.model.work.charge(8); const pending = [value];
    while (pending.length) {
      this.model.work.charge(); const child = pending.pop()!;
      if (Node.isJsxElement(child) || Node.isJsxSelfClosingElement(child)) {
        const target = this.model.target(this.jsxName(child));
        if (target) { this.model.work.charge(target.length + 1); targets.add(target); }
        if (!navigate && this.routerTag(child, 'Navigate')) navigate = child;
      }
      // Preserve source order, charging each reference before materializing/copying children.
      const children: Node[] = [];
      child.forEachChild(item => { this.model.work.charge(8); children.push(item); });
      for (let index = children.length - 1; index >= 0; index--) { this.model.work.charge(8); pending.push(children[index]); }
    }
    this.model.work.charge(targets.size + 1);
    const result = { targets: [...targets], navigate }; this.jsxCache.set(value, result); return result;
  }
  private renderTargets(expression: Node | undefined): string[] {
    const targets = this.jsxFacts(expression).targets;
    this.model.work.charge(targets.length + 1); return [...targets];
  }
  private react(): void {
    const routerApi = (node: Node, name: string): boolean => ['react-router', 'react-router-dom'].some(pkg => this.imported(node, pkg, name));
    for (const file of this.model.files) {
      this.model.work.charge(file.getFullWidth() + 1);
      const tags = file.getDescendants().filter(node => (Node.isJsxElement(node) || Node.isJsxSelfClosingElement(node)) && this.routerTag(node, 'Route'));
      const walk = (node: Node, prefix: string, parent?: string, layouts: string[] = [], mount = prefix): void => {
      this.model.work.charge(layouts.length + 1);
        const segmentNode = this.jsxProp(node, 'path'); const segment = segmentNode ? this.text(segmentNode) : '';
        if (segment === undefined) { this.report(node, 'dynamic React Router path', true); return; }
        const fullPath = segment.startsWith('/') ? this.joinPath(mount, segment.slice(1)) : this.joinPath(prefix, segment); const element = this.jsxProp(node, 'element');
        const navigate = this.jsxFacts(element).navigate;
        const targets = this.renderTargets(element); const component = this.model.target(this.jsxProp(node, 'Component')); if (component) targets.push(component);
        const opening = Node.isJsxElement(node) ? node.getOpeningElement() : node;
        const index = (Node.isJsxOpeningElement(opening) || Node.isJsxSelfClosingElement(opening)) && !!opening.getAttribute('index');
        const route = this.route(node, 'react-router', fullPath, { ...(index ? { index: true } : {}), ...(navigate ? { redirect_to: this.text(this.jsxProp(navigate, 'to')) ?? '(dynamic)' } : {}) });
        if (parent) this.edge(parent, route, 'RENDERS', node);
        for (const target of targets) { this.edge(route, target, 'HANDLES', node); for (const layout of layouts) this.edge(layout, target, 'RENDERS', node); }
        if (Node.isJsxElement(node)) {
          this.model.work.charge(node.getFullWidth() + 1);
          const nested = node.getDescendants().filter(item => (Node.isJsxElement(item) || Node.isJsxSelfClosingElement(item)) && this.routerTag(item, 'Route') && item.getAncestors().find(ancestor => this.routerTag(ancestor, 'Route')) === node);
          for (const child of nested) walk(child, fullPath, route, this.layouts(layouts, targets), mount);
        }
      };
      for (const tag of tags) {
        if (tag.getAncestors().some(ancestor => this.routerTag(ancestor, 'Route'))) continue;
        const router = tag.getAncestors().find(ancestor => ['BrowserRouter', 'HashRouter', 'MemoryRouter'].some(name => this.routerTag(ancestor, name)));
        const routerCall = tag.getAncestors().find(Node.isCallExpression);
        const factory = routerCall?.getAncestors().find(node => Node.isCallExpression(node) && ['createBrowserRouter', 'createHashRouter', 'createMemoryRouter'].some(name => routerApi(node.getExpression(), name)));
        const options = factory && Node.isCallExpression(factory) ? factory.getArguments()[1] : undefined;
        const baseNode = router ? this.jsxProp(router, 'basename') : options && this.prop(options, 'basename'); const base = baseNode ? this.text(baseNode) : '/';
        if (base === undefined) this.report(tag, 'dynamic React Router basename', true); else walk(tag, base);
      }
      this.model.work.charge(file.getFullWidth() + 1);
      for (const call of file.getDescendantsOfKind(SyntaxKind.CallExpression)) {
        if (!['createBrowserRouter', 'createHashRouter', 'createMemoryRouter', 'useRoutes'].some(name => routerApi(call.getExpression(), name))) continue;
        const argument = call.getArguments()[0];
        // JSX declarations are composed by the same route walker above, including their factory basename.
        if (argument && Node.isCallExpression(argument) && routerApi(argument.getExpression(), 'createRoutesFromElements')) continue;
        const baseNode = call.getArguments()[1] && this.prop(call.getArguments()[1], 'basename'); const base = baseNode ? this.text(baseNode) : '/';
        if (base === undefined) { this.report(call, 'dynamic React Router basename', true); continue; }
        const walkObject = (array: Node | undefined, prefix: string, parent?: string, layouts: string[] = [], seen = new Set<Node>()): void => {
      this.model.work.charge(seen.size + 1);
          if (!array) return;
          const value = this.value(array);
          if (!value || seen.has(value) || seen.size > 80) { this.report(array, 'React Router object array is dynamic or cyclic', true); return; }
          seen.add(value);
          for (const item of this.array(value, new Set(), true)) {
            const pathNode = this.prop(item, 'path'); const segment = pathNode ? this.text(pathNode) : '';
            if (segment === undefined) { this.report(item, 'dynamic React Router object path', true); continue; }
            const fullPath = segment.startsWith('/') ? this.joinPath(base, segment.slice(1)) : this.joinPath(prefix, segment); const element = this.prop(item, 'element'); const targets = this.renderTargets(element);
            const target = this.model.target(this.prop(item, 'Component')); if (target) targets.push(target);
            const navigate = this.jsxFacts(element).navigate;
            const route = this.route(item, 'react-router', fullPath, { ...(this.prop(item, 'index')?.getText() === 'true' ? { index: true } : {}), ...(navigate ? { redirect_to: this.text(this.jsxProp(navigate, 'to')) ?? '(dynamic)' } : {}) });
            if (parent) this.edge(parent, route, 'RENDERS', item);
            for (const target of targets) { this.edge(route, target, 'HANDLES', item); for (const layout of layouts) this.edge(layout, target, 'RENDERS', item); }
            walkObject(this.prop(item, 'children'), fullPath, route, this.layouts(layouts, targets), this.history(seen));
          }
        };
        walkObject(call.getArguments()[0], base);
      }
    }
  }
  private next(): void {
    const configs = this.inputs.filter(file => /(?:^|\/)next\.config\.(?:ts|js|mjs|cjs)$/.test(file));
    const configByRoot = new Map<string, { base?: string; file: string }>();
    for (const config of configs) {
      const text = this.read(config); if (text === undefined) continue;
      const source = this.model.byPath.get(config) ?? this.model.work.parse(text,()=>this.parsedConfigs.createSourceFile(`/${config}`,text,{overwrite:true}),source=>source.compilerNode,(node,visit)=>ts.forEachChild(node as ts.Node,child=>{visit(child);}));
      let expression: Node | undefined = source.getExportAssignments()[0]?.getExpression();
      if (!expression) expression = source.getDescendantsOfKind(SyntaxKind.BinaryExpression).find(node => node.getLeft().getText() === 'module.exports')?.getRight();
      const variables=new Map<string,Node>(); this.model.work.charge(source.compilerNode.statements.length+1);
      for(const declaration of source.getVariableDeclarations()) { this.model.work.charge(declaration.getNameNode().getFullWidth()+1); const name=declaration.getName(); if(!variables.has(name)) variables.set(name,declaration); }
      const localValue=(initial:Node|undefined):Node|undefined=>{
        const seen=new Set<Node>(); let node=initial;
        while(node) {
          this.model.work.charge(); this.model.work.checkDepth(seen.size,128); if(seen.has(node)) return undefined; seen.add(node);
          if(Node.isAsExpression(node)||Node.isSatisfiesExpression(node)||Node.isParenthesizedExpression(node)) { node=node.getExpression(); continue; }
          if(Node.isIdentifier(node)) { this.model.work.charge(node.getFullWidth()+1); const declaration=variables.get(node.getText()); node=declaration&&Node.isVariableDeclaration(declaration)?declaration.getInitializer():undefined; continue; }
          return node;
        }
        return undefined;
      };
      const object = localValue(expression); let base: string | undefined;
      if (object && Node.isObjectLiteralExpression(object)) {
        const property = object.getProperty('basePath');
        const value = property && Node.isPropertyAssignment(property) ? localValue(property.getInitializer()) : undefined;
        if (!property && !object.getProperties().some(Node.isSpreadAssignment)) base = '/';
        else if (value && (Node.isStringLiteral(value) || Node.isNoSubstitutionTemplateLiteral(value))) base = value.getLiteralText();
      }
      if (base === undefined) { this.complete = false; this.diagnostic(`routeUnresolved: ${config} dynamic/unsupported Next.js configuration; basePath unknown`); }
      configByRoot.set(path.posix.dirname(config), { base, file: config });
    }
    const nextRoots = new Set(configByRoot.keys());
    for (const input of this.inputs.filter(file => path.posix.basename(file) === 'package.json')) {
      const text = this.read(input); if (!text) continue;
      this.model.work.charge(text.length + 1);
      try { const pkg = JSON.parse(text); if (pkg.dependencies?.next || pkg.devDependencies?.next) nextRoots.add(path.posix.dirname(input)); } catch { /* Workspace discovery owns malformed manifests. */ }
    }
    for (const file of this.model.files) if (file.getImportDeclarations().some(imp => /^next(?:\/|$)/.test(imp.getModuleSpecifierValue()))) nextRoots.add(this.appRoot(this.relative(file)));
    const rootAtDirectory=new Map<string,string|undefined>();
    const nearestRoot=(relative:string):string|undefined=>{
      this.model.work.charge(relative.length+1); let directory=path.posix.dirname(relative); const trail:string[]=[];
      let found:string|undefined;
      while(true) {
        this.model.work.charge(directory.length+1);
        if(rootAtDirectory.has(directory)) { found=rootAtDirectory.get(directory); break; }
        trail.push(directory); if(nextRoots.has(directory)) { found=directory; break; }
        if(directory==='.') break; directory=path.posix.dirname(directory);
      }
      for(const item of trail) { this.model.work.charge(item.length+1); rootAtDirectory.set(item,found); } return found;
    };
    for (const file of this.model.files) {
      const relative = this.relative(file); if (!/(?:^|\/)page\.(?:jsx?|tsx?)$/.test(relative)) continue;
      const appRoot = nearestRoot(relative); if (appRoot === undefined) continue;
      const within = appRoot === '.' ? relative : relative.slice(appRoot.length + 1);
      const match = /^(?:src\/)?app\/(.*)page\.(?:jsx?|tsx?)$/.exec(within); if (!match) continue;
      const configuration = configByRoot.get(appRoot); if (configuration && configuration.base === undefined) continue;
      this.model.work.charge(match[1].length + 1);
      const segments = match[1].split('/').filter(Boolean);
      const urlSegments = segments.filter(segment => !/^\(.*\)$/.test(segment) && !segment.startsWith('@'));
      this.model.work.charge(match[1].length + urlSegments.length + 1);
      const fullPath = this.joinPath(configuration?.base ?? '/', urlSegments.join('/'));
      const page = this.model.exported(file, 'default'); const pageQN = page && this.model.declarations.get(page);
      if (!pageQN) { this.report(file, 'Next.js page default export unresolved'); continue; }
      const route = this.route(file, 'nextjs', fullPath, { ...(configuration ? { configuration_file: configuration.file } : {}) }, appRoot);
      this.edge(route, pageQN, 'HANDLES', file);
      const appDirectory = relative.slice(0, relative.length - match[1].length - path.posix.basename(relative).length);
      let previous: string | undefined;
      for (let depth = 0; depth <= segments.length; depth++) {
        this.model.work.charge(appDirectory.length + match[1].length + depth + 1);
        const directory = `${appDirectory}${segments.slice(0, depth).join('/')}`.replace(/\/$/, '');
        this.model.work.charge((directory.length + 32) * 4);
        const layoutFile = ['tsx', 'ts', 'jsx', 'js'].map(extension => this.model.byPath.get(`${directory}/layout.${extension}`)).find(Boolean);
        const layout = layoutFile && this.model.exported(layoutFile, 'default'); const layoutQN = layout && this.model.declarations.get(layout); if (!layoutQN) continue;
        this.edge(route, layoutQN, 'RENDERS', file, { layout: true });
        if (previous) this.edge(previous, layoutQN, 'RENDERS', file, { layout: true });
        previous = layoutQN;
      }
      if (previous) this.edge(previous, pageQN, 'RENDERS', file);
    }
  }
}
function joinPath(prefix: string, segment: string): string {
  if (segment.startsWith('/')) return `/${segment.split('/').filter(Boolean).join('/')}`;
  return `/${[...prefix.split('/'), ...segment.split('/')].filter(Boolean).join('/')}`;
}
