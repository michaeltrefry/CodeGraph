import { Node, SourceFile, SyntaxKind, Project } from 'ts-morph';
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
  private readonly inputs: string[];
  private readonly appRoots = new Map<string, string>();
  private readonly parsedConfigs = new Project({ useInMemoryFileSystem: true });
  constructor(private readonly model: SemanticModel, private readonly nodes: GraphNodeDto[],
    private readonly edges: PendingEdgeDto[], private readonly scope: DiscoveryScope | undefined,
    private readonly diagnostic: (message: string) => void, private readonly reader: BoundedTextReader) {
    this.inputs = scope ? scope.absoluteFiles().map(file => path.relative(model.root, file).replace(/\\/g, '/')) : model.files.map(file => model.relative(file));
    if (!scope) for (const file of model.files) {
      let directory = path.posix.dirname(model.relative(file));
      while (true) {
        for (const name of ['package.json', 'angular.json', 'tsconfig.json', 'next.config.ts', 'next.config.js', 'next.config.mjs', 'next.config.cjs']) {
          const input = directory === '.' ? name : `${directory}/${name}`;
          if (!this.inputs.includes(input) && reader.exists(path.join(model.root, input))) this.inputs.push(input);
        }
        if (directory === '.') break;
        directory = path.posix.dirname(directory);
      }
    }
    for (const node of nodes) if (node.properties.route_kind === 'ui') this.emitted.set(node.qualifiedName, node);
  }
  run(): { complete: boolean } { this.angular(); this.react(); this.next(); return { complete: this.complete && this.reader.complete }; }
  private relative(node: Node): string { return this.model.relative(node); }
  private report(node: Node, reason: string, incomplete = false): void { if (incomplete) this.complete = false; this.diagnostic(`routeUnresolved: ${this.relative(node)}:${node.getStartLineNumber()} ${reason}`); }
  private appRoot(file: string): string {
    const cached = this.appRoots.get(file); if (cached !== undefined) return cached;
    let directory = path.posix.dirname(file);
    while (directory !== '.') {
      if (this.inputs.some(input => path.posix.dirname(input) === directory && /^(package\.json|angular\.json|tsconfig[^/]*\.json|next\.config\.[^/]+)$/.test(path.posix.basename(input)))) { this.appRoots.set(file, directory); return directory; }
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
    return value && (Node.isStringLiteral(value) || Node.isNoSubstitutionTemplateLiteral(value)) ? value.getLiteralText() : undefined;
  }
  private prop(node: Node, name: string, seen = new Set<Node>()): Node | undefined {
    const object = this.value(node);
    if (!object || seen.has(object) || !Node.isObjectLiteralExpression(object)) return undefined;
    seen.add(object); let found: Node | undefined;
    for (const prop of object.getProperties()) {
      if (Node.isSpreadAssignment(prop)) { const candidate = this.prop(prop.getExpression(), name, new Set(seen)); if (candidate) found = candidate; }
      if (Node.isPropertyAssignment(prop) && prop.getName().replace(/^['"]|['"]$/g, '') === name) found = prop.getInitializer();
      if (Node.isShorthandPropertyAssignment(prop) && prop.getName() === name) found = prop.getNameNode();
    }
    return found;
  }
  private array(node: Node | undefined, seen = new Set<Node>(), strict = false): Node[] {
    const value = this.value(node);
    if (!value || seen.has(value) || seen.size > 80 || !Node.isArrayLiteralExpression(value)) {
      if (strict && node) this.report(node, 'route array/spread is dynamic or cyclic', true);
      return [];
    }
    seen.add(value);
    return value.getElements().flatMap(item => Node.isSpreadElement(item) ? this.array(item.getExpression(), new Set(seen), strict) : [item]);
  }
  private imported(node: Node, packageName: string, name: string): boolean {
    for (const imp of node.getSourceFile().getImportDeclarations()) {
      if (imp.getModuleSpecifierValue() !== packageName) continue;
      if (Node.isIdentifier(node)) {
        if (imp.getNamedImports().some(item => item.getName() === name && sameSymbol(node, item.getAliasNode() ?? item.getNameNode()))) return true;
      } else if (Node.isPropertyAccessExpression(node) && node.getName() === name && sameSymbol(node.getExpression(), imp.getNamespaceImport())) return true;
    }
    return false;
  }
  private edge(source: string, target: string, type: string, owner: Node, extra: Record<string, unknown> = {}): void {
    if (source === target) return;
    // Traversal may discover an edge while visiting a descendant page or route literal. Persist
    // outgoing facts with their actual source slice so signature changes replace that source's
    // edges, including shared layout chains whose endpoints both outlive the visited page.
    const declaration = this.model.byQN.get(source);
    const sourceFile = declaration ? this.relative(declaration) : this.emitted.get(source)?.filePath ?? this.relative(owner);
    const properties = { extraction_source_file: sourceFile, route_kind: 'ui', ...extra };
    const key = JSON.stringify([source, target, type, properties]);
    if (this.edgeKeys.has(key)) return;
    this.edgeKeys.add(key); this.edges.push({ sourceQN: source, targetQN: target, type, properties });
  }
  private route(owner: Node, framework: string, fullPath: string, extra: Record<string, unknown> = {}, appRoot = this.appRoot(this.relative(owner))): string {
    const qualifiedName = `ui-route:${this.model.repository}:${appRoot}:${framework}:${fullPath}`;
    if (!this.emitted.has(qualifiedName)) {
      const node: GraphNodeDto = { label: 'Route', name: fullPath, qualifiedName, filePath: this.relative(owner),
        startLine: owner.getStartLineNumber(), endLine: owner.getEndLineNumber(), properties: {
          route_kind: 'ui', framework, full_path: fullPath, app_root: appRoot,
          extraction_source_file: this.relative(owner), source_owner: this.model.owner(owner), ...extra } };
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
    const registrations = this.model.files.flatMap(file => file.getDescendantsOfKind(SyntaxKind.CallExpression).filter(call => this.angularRegistration(call)));
    const consumed = new Set<Node>();
    const rootsInModule = (module: Node | undefined, seen = new Set<Node>()): Node[] => {
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
          } else roots.push(...rootsInModule(this.model.resolve(imp), new Set(seen)));
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
      const value = this.value(array); if (!value || seen.has(value) || seen.size > 80) return; seen.add(value);
      for (const item of this.array(value, new Set(), true)) {
        const child = this.value(this.prop(item, 'children'));
        if (child) { consumed.add(child); discover(child, new Set(seen)); }
        const load = this.prop(item, 'loadChildren');
        if (load) for (const nested of lazy(load).arrays) { consumed.add(nested); discover(nested, new Set(seen)); }
      }
    };
    for (const registration of registrations) { const array = registration.getArguments()[0]; if (array) discover(array); }
    const walk = (array: Node, prefix: string, parent: string | undefined, app: string, seen = new Set<Node>(), layouts: string[] = []): void => {
      const value = this.value(array); if (!value || seen.has(value) || seen.size > 80) { this.report(array, 'Angular route cycle or traversal limit', true); return; }
      if (!Node.isArrayLiteralExpression(value)) { this.report(array, 'Angular route array is not statically enumerable', true); return; }
      seen.add(value);
      for (const raw of this.array(value, new Set(), true)) {
        const item = this.value(raw); if (!item || !Node.isObjectLiteralExpression(item)) continue;
        const pathNode = this.prop(item, 'path'); const segment = pathNode ? this.text(pathNode) : '';
        if (segment === undefined) { this.report(item, 'dynamic Angular route path', true); continue; }
        const fullPath = joinPath(prefix, segment); const redirectNode = this.prop(item, 'redirectTo');
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
          lazyChildren.push(...resolved.arrays);
        }
        const children = this.prop(item, 'children');
        for (const handler of handlers) {
          if (handler === component) this.edge(route, handler, 'HANDLES', item);
          if (children || lazyChildren.length) this.edge(route, handler, 'RENDERS', item, { layout: true });
          for (const layout of layouts) this.edge(layout, handler, 'RENDERS', item, { angular_route_outlet: true });
        }
        const ancestors = [...layouts, ...handlers];
        if (children) walk(children, fullPath, route, app, new Set(seen), ancestors);
        for (const child of lazyChildren) walk(child, fullPath, route, app, new Set(seen), ancestors);
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
  private renderTargets(expression: Node | undefined): string[] {
    const value = this.value(expression); if (!value) return [];
    const targets = new Set<string>();
    for (const child of [value, ...value.getDescendants()]) {
      if (!Node.isJsxElement(child) && !Node.isJsxSelfClosingElement(child)) continue;
      const target = this.model.target(this.jsxName(child)); if (target) targets.add(target);
    }
    return [...targets];
  }
  private react(): void {
    const routerApi = (node: Node, name: string): boolean => ['react-router', 'react-router-dom'].some(pkg => this.imported(node, pkg, name));
    for (const file of this.model.files) {
      const tags = file.getDescendants().filter(node => (Node.isJsxElement(node) || Node.isJsxSelfClosingElement(node)) && this.routerTag(node, 'Route'));
      const walk = (node: Node, prefix: string, parent?: string, layouts: string[] = [], mount = prefix): void => {
        const segmentNode = this.jsxProp(node, 'path'); const segment = segmentNode ? this.text(segmentNode) : '';
        if (segment === undefined) { this.report(node, 'dynamic React Router path', true); return; }
        const fullPath = segment.startsWith('/') ? joinPath(mount, segment.slice(1)) : joinPath(prefix, segment); const element = this.jsxProp(node, 'element');
        const navigate = element && [element, ...element.getDescendants()].find(item => this.routerTag(item, 'Navigate'));
        const targets = this.renderTargets(element); const component = this.model.target(this.jsxProp(node, 'Component')); if (component) targets.push(component);
        const opening = Node.isJsxElement(node) ? node.getOpeningElement() : node;
        const index = (Node.isJsxOpeningElement(opening) || Node.isJsxSelfClosingElement(opening)) && !!opening.getAttribute('index');
        const route = this.route(node, 'react-router', fullPath, { ...(index ? { index: true } : {}), ...(navigate ? { redirect_to: this.text(this.jsxProp(navigate, 'to')) ?? '(dynamic)' } : {}) });
        if (parent) this.edge(parent, route, 'RENDERS', node);
        for (const target of targets) { this.edge(route, target, 'HANDLES', node); for (const layout of layouts) this.edge(layout, target, 'RENDERS', node); }
        if (Node.isJsxElement(node)) {
          const nested = node.getDescendants().filter(item => (Node.isJsxElement(item) || Node.isJsxSelfClosingElement(item)) && this.routerTag(item, 'Route') && item.getAncestors().find(ancestor => this.routerTag(ancestor, 'Route')) === node);
          for (const child of nested) walk(child, fullPath, route, [...layouts, ...targets], mount);
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
      for (const call of file.getDescendantsOfKind(SyntaxKind.CallExpression)) {
        if (!['createBrowserRouter', 'createHashRouter', 'createMemoryRouter', 'useRoutes'].some(name => routerApi(call.getExpression(), name))) continue;
        const argument = call.getArguments()[0];
        // JSX declarations are composed by the same route walker above, including their factory basename.
        if (argument && Node.isCallExpression(argument) && routerApi(argument.getExpression(), 'createRoutesFromElements')) continue;
        const baseNode = call.getArguments()[1] && this.prop(call.getArguments()[1], 'basename'); const base = baseNode ? this.text(baseNode) : '/';
        if (base === undefined) { this.report(call, 'dynamic React Router basename', true); continue; }
        const walkObject = (array: Node | undefined, prefix: string, parent?: string, layouts: string[] = [], seen = new Set<Node>()): void => {
          if (!array) return;
          const value = this.value(array);
          if (!value || seen.has(value) || seen.size > 80) { this.report(array, 'React Router object array is dynamic or cyclic', true); return; }
          seen.add(value);
          for (const item of this.array(value, new Set(), true)) {
            const pathNode = this.prop(item, 'path'); const segment = pathNode ? this.text(pathNode) : '';
            if (segment === undefined) { this.report(item, 'dynamic React Router object path', true); continue; }
            const fullPath = segment.startsWith('/') ? joinPath(base, segment.slice(1)) : joinPath(prefix, segment); const element = this.prop(item, 'element'); const targets = this.renderTargets(element);
            const target = this.model.target(this.prop(item, 'Component')); if (target) targets.push(target);
            const navigate = element && [element, ...element.getDescendants()].find(node => this.routerTag(node, 'Navigate'));
            const route = this.route(item, 'react-router', fullPath, { ...(this.prop(item, 'index')?.getText() === 'true' ? { index: true } : {}), ...(navigate ? { redirect_to: this.text(this.jsxProp(navigate, 'to')) ?? '(dynamic)' } : {}) });
            if (parent) this.edge(parent, route, 'RENDERS', item);
            for (const target of targets) { this.edge(route, target, 'HANDLES', item); for (const layout of layouts) this.edge(layout, target, 'RENDERS', item); }
            walkObject(this.prop(item, 'children'), fullPath, route, [...layouts, ...targets], new Set(seen));
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
      const source = this.parsedConfigs.createSourceFile(`/${config}`, text, { overwrite: true });
      let expression: Node | undefined = source.getExportAssignments()[0]?.getExpression();
      if (!expression) expression = source.getDescendantsOfKind(SyntaxKind.BinaryExpression).find(node => node.getLeft().getText() === 'module.exports')?.getRight();
      const localValue = (node: Node | undefined, seen = new Set<Node>()): Node | undefined => {
        if (!node || seen.has(node)) return undefined; seen.add(node);
        if (Node.isAsExpression(node) || Node.isSatisfiesExpression(node) || Node.isParenthesizedExpression(node)) return localValue(node.getExpression(), seen);
        if (Node.isIdentifier(node)) return localValue(source.getVariableDeclaration(node.getText())?.getInitializer(), seen);
        return node;
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
      try { const pkg = JSON.parse(text); if (pkg.dependencies?.next || pkg.devDependencies?.next) nextRoots.add(path.posix.dirname(input)); } catch { /* Workspace discovery owns malformed manifests. */ }
    }
    for (const file of this.model.files) if (file.getImportDeclarations().some(imp => /^next(?:\/|$)/.test(imp.getModuleSpecifierValue()))) nextRoots.add(this.appRoot(this.relative(file)));
    for (const file of this.model.files) {
      const relative = this.relative(file); if (!/(?:^|\/)page\.(?:jsx?|tsx?)$/.test(relative)) continue;
      const appRoot = [...nextRoots].filter(root => root === '.' || relative.startsWith(`${root}/`)).sort((a, b) => b.length - a.length)[0]; if (appRoot === undefined) continue;
      const within = appRoot === '.' ? relative : relative.slice(appRoot.length + 1);
      const match = /^(?:src\/)?app\/(.*)page\.(?:jsx?|tsx?)$/.exec(within); if (!match) continue;
      const configuration = configByRoot.get(appRoot); if (configuration && configuration.base === undefined) continue;
      const segments = match[1].split('/').filter(Boolean);
      const urlSegments = segments.filter(segment => !/^\(.*\)$/.test(segment) && !segment.startsWith('@'));
      const fullPath = joinPath(configuration?.base ?? '/', urlSegments.join('/'));
      const page = this.model.exported(file, 'default'); const pageQN = page && this.model.declarations.get(page);
      if (!pageQN) { this.report(file, 'Next.js page default export unresolved'); continue; }
      const route = this.route(file, 'nextjs', fullPath, { ...(configuration ? { configuration_file: configuration.file } : {}) }, appRoot);
      this.edge(route, pageQN, 'HANDLES', file);
      const appDirectory = relative.slice(0, relative.length - match[1].length - path.posix.basename(relative).length);
      let previous: string | undefined;
      for (let depth = 0; depth <= segments.length; depth++) {
        const directory = `${appDirectory}${segments.slice(0, depth).join('/')}`.replace(/\/$/, '');
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
