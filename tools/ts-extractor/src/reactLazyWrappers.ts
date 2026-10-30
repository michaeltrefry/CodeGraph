import { Node, SourceFile, SyntaxKind } from 'ts-morph';
import { isCallable, SemanticModel } from './semanticModel';

type Environment = Map<Node, Value>;
type Value = string | number | boolean | undefined |
  { kind: 'function'; node: Node; environment: Environment; factoryReturn?: boolean } |
  { kind: 'component'; node: Node } |
  { kind: 'module'; file: SourceFile } |
  { kind: 'object'; properties: Map<string, Value> } |
  { kind: 'array'; elements: Value[] } |
  { kind: 'lazy'; target: Node; chain: string[] };
export interface LazyRenderTarget { qualifiedName: string; filePath: string; wrapperChain: string[]; }

/** Source-only initializer flow for a returned React wrapper. This preserves possible lazy
 * targets through local factories, captures and React's declared useState initializer. It never
 * executes application code, evaluates setter updates or treats a helper name as React evidence.
 * A target requires an actual react-imported lazy call and its resolved module/export payload.
 */
export function lazyWrapperTargets(model: SemanticModel, diagnostic: (message: string) => void): (tag: Node) => LazyRenderTarget[] {
  const cache = new Map<Node, LazyRenderTarget[]>();
  const mutated = new Set<Node>();
  const mutationFiles = new Set<SourceFile>();
  let expansions = 0;
  const isMutated = (node: Node): boolean => {
    if (!(Node.isVariableDeclaration(node) || Node.isParameterDeclaration(node) || Node.isFunctionDeclaration(node))) return false;
    const file = node.getSourceFile();
    if (mutationFiles.has(file)) return mutated.has(node); mutationFiles.add(file);
    for (const binary of file.getDescendantsOfKind(SyntaxKind.BinaryExpression)) {
      const operator = binary.getOperatorToken().getKind();
      if (operator >= SyntaxKind.FirstAssignment && operator <= SyntaxKind.LastAssignment && Node.isIdentifier(binary.getLeft()))
        for (const declaration of binary.getLeft().getSymbol()?.getDeclarations() ?? []) mutated.add(declaration);
    }
    for (const operation of [...file.getDescendantsOfKind(SyntaxKind.PrefixUnaryExpression), ...file.getDescendantsOfKind(SyntaxKind.PostfixUnaryExpression)]) {
      if (![SyntaxKind.PlusPlusToken, SyntaxKind.MinusMinusToken].includes(operation.getOperatorToken())) continue;
      for (const declaration of operation.getOperand().getSymbol()?.getDeclarations() ?? []) mutated.add(declaration);
    }
    return mutated.has(node);
  };
  const declarations = (node: Node) => node.getSymbol()?.getDeclarations().filter(declaration => model.byPath.has(model.relative(declaration))) ?? [];
  const component = (value: Value): Node | undefined => typeof value === 'object' && value && (value.kind === 'function' || value.kind === 'component') ? value.node : undefined;
  const signature = (value: Value, seen = new Set<object>()): string => {
    if (!value || typeof value !== 'object') return JSON.stringify(value) ?? '?';
    if (seen.has(value)) return '?cycle'; const next = new Set(seen); next.add(value);
    if (value.kind === 'component') return model.declarations.get(value.node) ?? '?';
    if (value.kind === 'function') return JSON.stringify([model.declarations.get(value.node), [...value.environment].map(([node, bound]) => [`${model.relative(node)}:${node.getPos()}`, signature(bound, next)])]);
    if (value.kind === 'lazy') return `lazy:${model.declarations.get(value.target)}`;
    if (value.kind === 'module') return `module:${model.relative(value.file)}`;
    if (value.kind === 'array') return JSON.stringify(value.elements.map(value => signature(value, next)));
    return JSON.stringify([...value.properties].map(([key, value]) => [key, signature(value, next)]));
  };
  const invoke = (callable: Value, args: Value[], active: Set<Node>): Value => {
    if (!callable || typeof callable !== 'object' || callable.kind !== 'function') return undefined;
    const declaration = callable.node;
    if (active.has(declaration)) return undefined;
    if (active.size >= 16) { diagnostic(`reactLazyTraversal: wrapper depth limit at ${model.relative(declaration)}`); return undefined; }
    const next = new Set(active); next.add(declaration);
    const environment = new Map(callable.environment);
    if (!(Node.isFunctionDeclaration(declaration) || Node.isFunctionExpression(declaration) || Node.isArrowFunction(declaration) || Node.isMethodDeclaration(declaration))) return undefined;
    declaration.getParameters().forEach((parameter, index) => environment.set(parameter, args[index]));
    const body = declaration.getBody();
    const returned = !body ? [] : Node.isBlock(body) ? body.getDescendantsOfKind(SyntaxKind.ReturnStatement)
      .filter(statement => model.owner(statement) === model.callables.get(declaration)).map(statement => statement.getExpression()) : [body];
    const values = returned.map(expression => evaluate(expression, environment, next, new Set()));
    if (values.length && values.every(value => signature(value) === signature(values[0]))) {
      const value = values[0]; return value && typeof value === 'object' && value.kind === 'function' ? { ...value, factoryReturn: true } : value;
    }
    if (values.some(value => value !== undefined)) diagnostic(`reactLazyUnresolved: differing returned values in ${model.callables.get(declaration)}`);
    return undefined;
  };
  const evaluate = (node: Node | undefined, environment: Environment, active: Set<Node>, seen: Set<Node>): Value => {
    if (!node || seen.has(node)) return undefined;
    if (seen.size > 60) { diagnostic(`reactLazyTraversal: initializer alias depth limit in ${model.relative(node)}`); return undefined; }
    if (++expansions > 100000) { if (expansions === 100001) diagnostic('reactLazyTraversal: initializer expansion budget exceeded'); return undefined; }
    if (isMutated(node)) return undefined;
    if (environment.has(node)) return environment.get(node);
    const next = new Set(seen); next.add(node);
    const ev = (expression: Node | undefined) => evaluate(expression, environment, active, next);
    if (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isNonNullExpression(node) || Node.isAwaitExpression(node)) return ev(node.getExpression());
    if (Node.isStringLiteral(node) || Node.isNoSubstitutionTemplateLiteral(node)) return node.getLiteralText();
    if (Node.isNumericLiteral(node)) return node.getLiteralValue();
    if (node.getKind() === SyntaxKind.TrueKeyword) return true;
    if (node.getKind() === SyntaxKind.FalseKeyword) return false;
    if (isCallable(node)) return { kind: 'function', node, environment: new Map(environment) };
    if (Node.isClassDeclaration(node)) return { kind: 'component', node };
    if (Node.isIdentifier(node)) {
      const declared = declarations(node);
      if (declared.length === 1) return ev(declared[0]);
      return undefined;
    }
    if (Node.isImportSpecifier(node) || Node.isImportClause(node)) {
      const declaration = node.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
      const file = model.module(node, declaration.getModuleSpecifierValue());
      return file ? ev(model.exported(file, Node.isImportSpecifier(node) ? node.getName() : 'default')) : undefined;
    }
    if (Node.isParameterDeclaration(node)) return undefined;
    if (Node.isVariableDeclaration(node)) {
      if (node.getParentIfKind(SyntaxKind.VariableDeclarationList)?.getDeclarationKind() !== 'const') return undefined;
      return ev(node.getInitializer());
    }
    if (Node.isBindingElement(node)) {
      const pattern = node.getParent(); const value = ev(pattern?.getParent());
      if (Node.isArrayBindingPattern(pattern) && value && typeof value === 'object' && value.kind === 'array') return value.elements[pattern.getElements().indexOf(node)];
      if (Node.isObjectBindingPattern(pattern) && value && typeof value === 'object' && value.kind === 'object') return value.properties.get(node.getPropertyNameNode()?.getText() ?? node.getName());
      return undefined;
    }
    if (Node.isObjectLiteralExpression(node)) {
      const properties = new Map<string, Value>();
      for (const property of node.getProperties()) {
        if (Node.isPropertyAssignment(property)) properties.set(property.getName(), ev(property.getInitializer()));
        else if (Node.isShorthandPropertyAssignment(property)) properties.set(property.getName(), ev(property.getValueSymbol()?.getDeclarations()[0]));
        else if (Node.isSpreadAssignment(property)) return undefined;
      }
      return { kind: 'object', properties };
    }
    if (Node.isPropertyAccessExpression(node) || Node.isElementAccessExpression(node)) {
      const value = ev(node.getExpression());
      const key = Node.isPropertyAccessExpression(node) ? node.getName() : ev(node.getArgumentExpression());
      if (typeof key !== 'string' || !value || typeof value !== 'object') return undefined;
      if (value.kind === 'object') return value.properties.get(key);
      if (value.kind === 'module') return ev(model.exported(value.file, key));
      return undefined;
    }
    if (Node.isConditionalExpression(node)) {
      const condition = ev(node.getCondition());
      if (typeof condition === 'boolean') return ev(condition ? node.getWhenTrue() : node.getWhenFalse());
      const yes = ev(node.getWhenTrue()); const no = ev(node.getWhenFalse());
      if (signature(yes) === signature(no)) return yes;
      diagnostic(`reactLazyUnresolved: conditional initializer in ${model.relative(node)}`); return undefined;
    }
    if (Node.isCallExpression(node)) {
      const expression = node.getExpression();
      if (expression.getKind() === SyntaxKind.ImportKeyword) {
        const specifier = ev(node.getArguments()[0]);
        const file = typeof specifier === 'string' ? model.module(node, specifier) : undefined;
        return file ? { kind: 'module', file } : undefined;
      }
      const api = model.reactApi(node);
      if (api === 'lazy') {
        const payload = invoke(ev(node.getArguments()[0]), [], active);
        const target = payload && typeof payload === 'object' && payload.kind === 'module' ? model.exported(payload.file, 'default') :
          payload && typeof payload === 'object' && payload.kind === 'object' ? component(payload.properties.get('default')) : undefined;
        if (!target || !model.declarations.has(target)) { diagnostic(`reactLazyUnresolved: loader does not select one admitted export in ${model.relative(node)}`); return undefined; }
        return { kind: 'lazy', target, chain: [...active].map(node => model.callables.get(node)).filter((qn): qn is string => !!qn) };
      }
      if (api === 'useState') {
        const initial = ev(node.getArguments()[0]);
        return { kind: 'array', elements: [initial && typeof initial === 'object' && initial.kind === 'function' ? invoke(initial, [], active) : initial, undefined] };
      }
      if (Node.isPropertyAccessExpression(expression) && expression.getName() === 'then') {
        const source = ev(expression.getExpression());
        if (source && typeof source === 'object' && source.kind === 'module') return invoke(ev(node.getArguments()[0]), [source], active);
        return undefined;
      }
      return invoke(ev(expression), node.getArguments().map(ev), active);
    }
    return undefined;
  };
  const candidates = new Map<Node, boolean>();
  const provenLazyFactories = new Set<Node>();
  const sourceCalls = new Map<Node, Node[]>();
  const hasLazySource = (callable: Node, seen = new Set<Node>()): boolean => {
    if (provenLazyFactories.has(callable)) return true;
    if (seen.has(callable)) return false;
    if (seen.size >= 64) { diagnostic(`reactLazyUnresolved: source factory discovery bound at ${model.relative(callable)}`); return false; }
    const next = new Set(seen); next.add(callable);
    let targets = sourceCalls.get(callable);
    if (!targets) {
      targets = [];
      for (const call of callable.getDescendantsOfKind(SyntaxKind.CallExpression)) {
        if (model.reactApi(call) === 'lazy') { provenLazyFactories.add(callable); return true; }
        const target = model.resolve(call.getExpression());
        if (target && model.callables.has(target)) targets.push(target);
      }
      sourceCalls.set(callable, targets);
    }
    if (targets.some(target => hasLazySource(target, next))) { provenLazyFactories.add(callable); return true; }
    return false;
  };
  const isFactoryInitializer = (declaration: Node, seen = new Set<Node>()): boolean => {
    if (seen.has(declaration)) return false;
    if (seen.size > 16) { diagnostic(`reactLazyTraversal: factory alias depth limit in ${model.relative(declaration)}`); return false; }
    const cached = candidates.get(declaration); if (cached !== undefined) return cached;
    seen.add(declaration);
    let result = false;
    if (Node.isVariableDeclaration(declaration)) {
      let initializer = declaration.getInitializer();
      while (initializer && (Node.isParenthesizedExpression(initializer) || Node.isAsExpression(initializer) || Node.isNonNullExpression(initializer))) initializer = initializer.getExpression();
      if (initializer && Node.isCallExpression(initializer)) {
        const api = model.reactApi(initializer);
        const target = api ? undefined : model.resolve(initializer.getExpression());
        result = api === 'lazy' || !!target && model.callables.has(target) && hasLazySource(target);
      } else if (initializer && Node.isIdentifier(initializer)) {
        const declared = declarations(initializer); result = declared.length === 1 && isFactoryInitializer(declared[0], seen);
      } else if (initializer && Node.isConditionalExpression(initializer)) {
        result = [initializer.getWhenTrue(), initializer.getWhenFalse()].some(branch => {
          if (Node.isIdentifier(branch)) { const declared = declarations(branch); return declared.length === 1 && isFactoryInitializer(declared[0], new Set(seen)); }
          if (!Node.isCallExpression(branch)) return false;
          const api = model.reactApi(branch); const target = api ? undefined : model.resolve(branch.getExpression());
          return api === 'lazy' || !!target && model.callables.has(target) && hasLazySource(target);
        });
      }
    } else if (Node.isImportSpecifier(declaration) || Node.isImportClause(declaration)) {
      const imported = declaration.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
      const file = model.module(declaration, imported.getModuleSpecifierValue());
      const target = file ? model.exported(file, Node.isImportSpecifier(declaration) ? declaration.getName() : 'default') : undefined;
      result = !!target && isFactoryInitializer(target, seen);
    }
    candidates.set(declaration, result); return result;
  };
  return tag => {
    const declared = declarations(tag); const key = declared.length === 1 ? declared[0] : tag;
    const cached = cache.get(key); if (cached) return cached;
    // Existing semantic extraction already handles plain functions/classes/JSX aliases. Only
    // source factory initializers need closure-aware lazy traversal; avoid expanding icon maps
    // and other unrelated object-valued UI declarations in large applications.
    if (declared.length !== 1 || !isFactoryInitializer(key)) { cache.set(key, []); return []; }
    const value = evaluate(tag, new Map(), new Set(), new Set());
    const targets = new Map<string, LazyRenderTarget>();
    const add = (value: Value) => {
      if (!value || typeof value !== 'object' || value.kind !== 'lazy') return;
      const qualifiedName = model.declarations.get(value.target); if (!qualifiedName) return;
      targets.set(qualifiedName, { qualifiedName, filePath: model.relative(value.target), wrapperChain: value.chain });
    };
    add(value);
    if (value && typeof value === 'object' && value.kind === 'function' && value.factoryReturn) {
      for (const jsx of [...value.node.getDescendantsOfKind(SyntaxKind.JsxSelfClosingElement), ...value.node.getDescendantsOfKind(SyntaxKind.JsxOpeningElement)]) {
        if (model.owner(jsx) !== model.callables.get(value.node)) continue;
        add(evaluate(jsx.getTagNameNode(), value.environment, new Set(), new Set()));
      }
    }
    const result = [...targets.values()]; cache.set(key, result); return result;
  };
}
