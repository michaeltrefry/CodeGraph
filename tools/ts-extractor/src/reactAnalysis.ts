import { Node, SyntaxKind } from 'ts-morph';
import type { GraphNodeDto, PendingEdgeDto } from './protocol';
import { SemanticModel, sameSymbol } from './semanticModel';
import { lazyWrapperTargets } from './reactLazyWrappers';

interface Provider { qn: string; context: string; element: Node; values: Set<string>; dynamic: boolean; }
interface Render { owner: string; target: string; element: Node; uncertain?: boolean; }

/** Walk observed render trees with a provider environment; never flatten provider values into
 * a global registry. A provider returned by a component does not provide its own hooks. */
export function analyzeReact(model: SemanticModel, nodes: GraphNodeDto[], edges: PendingEdgeDto[], diagnostic: (message: string) => void): void {
  const screenMetadata = declaredScreenMetadata(model);
  const lazyTargets = lazyWrapperTargets(model, diagnostic);
  const contexts = new Map<string, { declaration: Node; defaults: Set<string> }>();
  const providers = new Map<Node, Provider>();
  const consumers = new Map<string, Set<string>>();
  const renders: Render[] = [];
  const contextUses: { owner: string; context: string }[] = [];
  const providerCounts = new Map<string, number>();
  const external = new Set(nodes.map(node => node.qualifiedName));
  const renderable = new Map(nodes.filter(node => ['Component', 'Function', 'Class', 'Method'].includes(node.label)).map(node => [node.qualifiedName, node]));
  const callableQNs = new Set(model.callables.values());
  const callableValues = (expression: Node | undefined, seen = new Set<Node>()): Set<string> => {
    const result = new Set<string>(); if (!expression || seen.has(expression)) return result; seen.add(expression);
    const resolved = model.resolve(expression) ?? expression;
    if (Node.isCallExpression(resolved) && model.reactApi(resolved) === 'useMemo') {
      const callback = resolved.getArguments()[0];
      if (callback && (Node.isArrowFunction(callback) || Node.isFunctionExpression(callback))) {
        const body = callback.getBody();
        if (!Node.isBlock(body)) return callableValues(body, seen);
        for (const returned of body.getDescendantsOfKind(SyntaxKind.ReturnStatement)) {
          if (model.owner(returned) !== model.callables.get(callback)) continue;
          for (const value of callableValues(returned.getExpression(), new Set(seen))) result.add(value);
        }
      }
      return result;
    }
    if (Node.isConditionalExpression(resolved)) {
      for (const branch of [resolved.getWhenTrue(), resolved.getWhenFalse()]) for (const value of callableValues(branch, new Set(seen))) result.add(value);
      diagnostic(`contextValueAlternatives: ${model.owner(resolved)}: conditional provided value`);
      return result;
    }
    if (Node.isVariableDeclaration(resolved) || Node.isPropertyDeclaration(resolved) || Node.isPropertyAssignment(resolved)) return callableValues(resolved.getInitializer(), seen);
    const qn = model.declarations.get(resolved);
    if (qn && callableQNs.has(qn)) result.add(qn);
    if (Node.isObjectLiteralExpression(resolved)) {
      for (const prop of resolved.getProperties()) {
        if (Node.isPropertyAssignment(prop)) for (const value of callableValues(prop.getInitializer(), new Set(seen))) result.add(value);
        if (Node.isShorthandPropertyAssignment(prop)) {
          const declaration = prop.getValueSymbol()?.getDeclarations()[0];
          for (const value of callableValues(declaration, new Set(seen))) result.add(value);
        }
        if (Node.isSpreadAssignment(prop)) for (const value of callableValues(prop.getExpression(), new Set(seen))) result.add(value);
        if (Node.isMethodDeclaration(prop)) { const target = model.declarations.get(prop); if (target) result.add(target); }
      }
    }
    return result;
  };
  for (const file of model.files) {
    for (const call of file.getDescendantsOfKind(SyntaxKind.CallExpression)) {
      const api = model.reactApi(call);
      if (api === 'createContext') {
        const declaration = call.getParent();
        if (!declaration || !Node.isVariableDeclaration(declaration)) continue;
        const qn = model.declarations.get(declaration)!;
        contexts.set(qn, { declaration, defaults: callableValues(call.getArguments()[0]) });
        nodes.push({ label: 'Property', name: declaration.getName(), qualifiedName: qn, filePath: model.relative(call),
          startLine: declaration.getStartLineNumber(), endLine: declaration.getEndLineNumber(), properties: { framework: 'react', context_kind: 'context' } });
        edges.push({ sourceQN: model.fileQN(call), targetQN: qn, type: 'DEFINES' });
      }
      if (api === 'useContext' || api === 'use') {
        const context = model.target(call.getArguments()[0]);
        if (!context) { diagnostic(`contextUnresolved: ${model.owner(call)}: ${call.getText().slice(0, 120)}`); continue; }
        contextUses.push({ owner: model.owner(call), context });
      }
    }
  }
  for (const { owner, context } of contextUses) {
    if (!contexts.has(context)) { diagnostic(`contextUnresolved: ${owner}: ${context} is not a statically known context`); continue; }
    if (!consumers.has(owner)) consumers.set(owner, new Set()); consumers.get(owner)!.add(context);
    edges.push({ sourceQN: owner, targetQN: context, type: 'INJECTS', properties: { framework: 'react', context_kind: 'consumer' } });
  }
  const contextTag = (tag: Node): string | undefined => {
    const expression = Node.isPropertyAccessExpression(tag) && tag.getName() === 'Provider' ? tag.getExpression() : tag;
    const target = model.target(expression); return target && contexts.has(target) ? target : undefined;
  };
  for (const file of model.files) {
    for (const element of [...file.getDescendantsOfKind(SyntaxKind.JsxElement), ...file.getDescendantsOfKind(SyntaxKind.JsxSelfClosingElement)]) {
      const opening = Node.isJsxElement(element) ? element.getOpeningElement() : element;
      const tag = opening.getTagNameNode(); const text = tag.getText();
      const owner = model.owner(element); const context = contextTag(tag);
      if (context) {
        const value = opening.getAttributes().find(attribute => Node.isJsxAttribute(attribute) && attribute.getNameNode().getText() === 'value');
        const init = value && Node.isJsxAttribute(value) ? value.getInitializer() : undefined;
        const expression = init && Node.isJsxExpression(init) ? init.getExpression() : init;
        const values = callableValues(expression);
        const ordinal = providerCounts.get(owner) ?? 0; providerCounts.set(owner, ordinal + 1);
        const qn = `${owner}.$provider${ordinal}.${context.split(/[.#]/).pop()}`;
        const provider: Provider = { qn, context, element, values, dynamic: !!expression && values.size === 0 && !['null', 'undefined'].includes(expression.getText()) };
        providers.set(element, provider);
        nodes.push({ label: 'Property', name: `${context.split(/[.#]/).pop()} provider`, qualifiedName: qn, filePath: model.relative(element),
          startLine: element.getStartLineNumber(), endLine: element.getEndLineNumber(), properties: { framework: 'react', context_kind: 'provider', context_qn: context, value_resolution: provider.dynamic ? 'unresolved' : 'static' } });
        edges.push({ sourceQN: owner, targetQN: qn, type: 'DEFINES' }, { sourceQN: qn, targetQN: context, type: 'REGISTERS', properties: { framework: 'react', context_kind: 'provider' } });
        for (const value of values) edges.push({ sourceQN: qn, targetQN: value, type: 'CALLS', properties: { call_kind: 'context_value' } });
        if (provider.dynamic) diagnostic(`contextValueUnresolved: ${qn}`);
        continue;
      }
      if (/^[a-z]/.test(text) && !text.includes('.')) continue;
      if (Node.isPropertyAccessExpression(tag) && ['Provider', 'Consumer'].includes(tag.getName())) continue;
      let target = model.target(tag);
      if (target && !renderable.has(target)) target = undefined;
      if (target) {
        const declaration = renderable.get(target)!;
        if (declaration.label === 'Function' || declaration.label === 'Class') { declaration.label = 'Component'; declaration.properties.framework = 'react'; }
      }
      if (!target) {
        for (const imp of file.getImportDeclarations()) {
          const named = imp.getNamedImports().find(item => sameSymbol(tag, item.getAliasNode() ?? item.getNameNode()));
          const namespace = imp.getNamespaceImport()?.getText();
          const defaultImport = sameSymbol(tag, imp.getDefaultImport());
          const exportName = named?.getName() ?? (defaultImport ? 'default' : namespace && Node.isPropertyAccessExpression(tag) && sameSymbol(tag.getExpression(), imp.getNamespaceImport()) ? tag.getName() : undefined);
          if (!exportName || model.module(tag, imp.getModuleSpecifierValue())) continue;
          const resolved = model.imports.find(item => item.filePath === model.relative(file) && item.importedNamespace === imp.getModuleSpecifierValue() && item.classification === 'external_npm_package');
          if (!resolved?.externalPackageName) continue;
          target = `npm-symbol:${imp.getModuleSpecifierValue()}#${exportName}`;
          if (!external.has(target)) {
            nodes.push({ label: 'ExternalSymbol', name: exportName, qualifiedName: target, filePath: model.relative(file), startLine: 0, endLine: 0,
              properties: { package_name: resolved.externalPackageName, import_path: imp.getModuleSpecifierValue(), export_name: exportName, framework: 'react' } }); external.add(target);
          }
        }
      }
      if (target) { renders.push({ owner, target, element }); edges.push({ sourceQN: owner, targetQN: target, type: 'RENDERS', properties: { framework: 'react', ...screenMetadata(element) } }); }
      else diagnostic(`renderUnresolved: ${owner}: ${text}`);
      for (const lazy of lazyTargets(tag)) {
        if (lazy.qualifiedName === target || !renderable.has(lazy.qualifiedName)) continue;
        renders.push({ owner, target: lazy.qualifiedName, element, uncertain: true });
        edges.push({ sourceQN: owner, targetQN: lazy.qualifiedName, type: 'RENDERS', properties: { framework: 'react', ...screenMetadata(element),
          lazy_wrapper: true, lazy_target_file: lazy.filePath, lazy_wrapper_chain: lazy.wrapperChain,
          confidence_band: 'medium', candidate: true, lazy_evidence: 'source_initializer_to_react_lazy' } });
      }
    }
  }
  const calls = new Map<string, Set<string>>();
  for (const edge of edges.filter(edge => edge.type === 'CALLS')) {
    if (!calls.has(edge.sourceQN)) calls.set(edge.sourceQN, new Set()); calls.get(edge.sourceQN)!.add(edge.targetQN);
  }
  const bindings = new Map<string, Map<string, Set<string>>>();
  const visited = new Set<string>();
  const walk = (owner: string, environment: Map<string, Provider>, depth: number): void => {
    if (depth > 100) { diagnostic(`contextTraversal: depth limit at ${owner}`); return; }
    const key = `${owner}|${[...environment].map(([context, provider]) => `${context}=${provider.qn}`).sort().join('|')}`;
    if (visited.has(key)) return; visited.add(key);
    for (const context of consumers.get(owner) ?? []) {
      const provider = environment.get(context);
      if (!bindings.has(owner)) bindings.set(owner, new Map());
      const byContext = bindings.get(owner)!;
      if (!byContext.has(context)) byContext.set(context, new Set());
      byContext.get(context)!.add(provider?.qn ?? `${context}#default`);
    }
    for (const target of calls.get(owner) ?? []) walk(target, environment, depth + 1);
    const owned = renders.filter(render => render.owner === owner);
    const parentOf = (render: Render): Render | undefined => {
      for (const ancestor of render.element.getAncestors()) {
        const parent = owned.find(candidate => candidate.element === ancestor);
        if (parent) return parent;
      }
      return undefined;
    };
    const withProviders = (element: Node, inherited: Map<string, Provider>): Map<string, Provider> => {
      const next = new Map(inherited);
      for (const ancestor of element.getAncestors().reverse()) {
        const provider = providers.get(ancestor); if (provider) next.set(provider.context, provider);
      }
      return next;
    };
    const visitRender = (render: Render, inherited: Map<string, Provider>): void => {
      // Source-initializer lazy candidates preserve impact, but do not prove the provider
      // environment inside a factory wrapper. Keep its consumers explicitly unresolved.
      if (render.uncertain) return;
      const next = withProviders(render.element, inherited);
      walk(render.target, next, depth + 1);
      const children = owned.filter(child => parentOf(child) === render);
      if (!children.length) return;
      const callable = model.byQN.get(render.target);
      // Explicit children slots preserve provider scope across ordinary provider/layout wrappers.
      const slots = callable?.getDescendantsOfKind(SyntaxKind.JsxExpression).filter(slot => {
        if (model.owner(slot) !== render.target) return false;
        const expression = slot.getExpression();
        if (!expression) return false;
        if (Node.isIdentifier(expression) && expression.getText() === 'children') return expression.getSymbol()?.getDeclarations().some(declaration => Node.isBindingElement(declaration) && !!declaration.getFirstAncestorByKind(SyntaxKind.Parameter));
        if (Node.isPropertyAccessExpression(expression) && expression.getName() === 'children') return expression.getExpression().getSymbol()?.getDeclarations().some(Node.isParameterDeclaration);
        return false;
      }) ?? [];
      if (!slots.length) { diagnostic(`contextChildrenUnresolved: ${owner} -> ${render.target}`); return; }
      for (const slot of slots) for (const child of children) visitRender(child, withProviders(slot, next));
    };
    for (const render of owned.filter(render => !parentOf(render))) visitRender(render, environment);
  };
  const targets = new Set(renders.map(render => render.target));
  for (const node of nodes.filter(node => node.label === 'Component' && !targets.has(node.qualifiedName))) walk(node.qualifiedName, new Map(), 0);
  for (const owner of consumers.keys()) if (!bindings.has(owner)) diagnostic(`contextScopeUnresolved: ${owner} has no observed render/call path`);
  const providerByQN = new Map([...providers.values()].map(provider => [provider.qn, provider]));
  for (const [owner, byContext] of bindings) for (const [context, alternatives] of byContext) {
    const ambiguous = alternatives.size > 1;
    if (ambiguous) diagnostic(`contextAlternatives: ${owner}: ${context}: ${[...alternatives].sort().join(', ')}`);
    for (const alternative of alternatives) {
      const properties = { framework: 'react', context_qn: context, context_kind: 'binding', confidence_band: ambiguous ? 'medium' : 'high', candidate: ambiguous };
      const provider = providerByQN.get(alternative);
      if (provider) edges.push({ sourceQN: owner, targetQN: provider.qn, type: 'INJECTS', properties });
      else for (const target of contexts.get(context)?.defaults ?? []) edges.push({ sourceQN: owner, targetQN: target, type: 'CALLS', properties: { ...properties, call_kind: 'context_default' } });
    }
  }
  canonicalizeScreenSelections(edges);
}

/** Persistence identifies an edge by source/target/type. Factory instances can share a wrapper
 * declaration, so keep each declared selection canonically instead of allowing last-write wins. */
function canonicalizeScreenSelections(edges: PendingEdgeDto[]): void {
  const groups = new Map<string, PendingEdgeDto[]>();
  for (const edge of edges) {
    if (edge.type !== 'RENDERS' || edge.properties?.framework !== 'react') continue;
    const key = JSON.stringify([edge.sourceQN, edge.targetQN]);
    if (!groups.has(key)) groups.set(key, []); groups.get(key)!.push(edge);
  }
  const discard = new Set<PendingEdgeDto>();
  for (const group of groups.values()) {
    if (group.length < 2 || !group.some(edge => edge.properties?.screen_key !== undefined)) continue;
    const selections = new Map<string, Record<string, unknown>>();
    for (const edge of group) {
      if (edge.properties?.screen_key === undefined) continue;
      const selection = Object.fromEntries(Object.entries(edge.properties).filter(([key]) => key.startsWith('screen_')));
      selections.set(JSON.stringify(selection), selection);
    }
    const canonical = [...selections].sort(([left], [right]) => left.localeCompare(right)).map(([, value]) => value);
    const first = group[0]; first.properties = { ...first.properties };
    for (const key of Object.keys(first.properties)) if (key.startsWith('screen_')) delete first.properties[key];
    if (canonical.length === 1) Object.assign(first.properties, canonical[0]);
    else first.properties.screen_selections = canonical;
    for (const duplicate of group.slice(1)) discard.add(duplicate);
  }
  if (discard.size) edges.splice(0, edges.length, ...edges.filter(edge => !discard.has(edge)));
}

/** Preserve declared screen selection, not arbitrary conditional visibility. A selector must
 * first be used by an actual navigation landmark's aria-current=page declaration. Only positive
 * literal guards using that same symbol (or immutable boolean aliases) become screen metadata.
 * Keep-alive wrappers can pass the declared selection through their explicit active prop.
 */
function declaredScreenMetadata(model: SemanticModel): (element: Node) => Record<string, unknown> {
  interface Selection { key: string; selector: string; owner: string; }
  const selectors = new Map<Node, { name: string; owner: string }>();
  const unwrap = (node: Node): Node => {
    while (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isNonNullExpression(node)) node = node.getExpression();
    return node;
  };
  const declarationOf = (node: Node): Node | undefined => {
    if (!Node.isIdentifier(node)) return undefined;
    const declarations = node.getSymbol()?.getDeclarations() ?? [];
    return declarations.length === 1 ? declarations[0] : undefined;
  };
  const literal = (node: Node): string | undefined => Node.isStringLiteral(node) || Node.isNoSubstitutionTemplateLiteral(node) ? node.getLiteralText() : undefined;
  for (const file of model.files) for (const attribute of file.getDescendantsOfKind(SyntaxKind.JsxAttribute)) {
    if (attribute.getNameNode().getText() !== 'aria-current') continue;
    const navigation = attribute.getAncestors().some(ancestor => Node.isJsxElement(ancestor) && ancestor.getOpeningElement().getTagNameNode().getText() === 'nav');
    if (!navigation) continue;
    const initializer = attribute.getInitializer();
    const expression = initializer && Node.isJsxExpression(initializer) ? initializer.getExpression() : undefined;
    if (!expression) continue;
    const conditional = unwrap(expression);
    if (!Node.isConditionalExpression(conditional) || literal(unwrap(conditional.getWhenTrue())) !== 'page') continue;
    const condition = unwrap(conditional.getCondition());
    if (!Node.isBinaryExpression(condition) || condition.getOperatorToken().getKind() !== SyntaxKind.EqualsEqualsEqualsToken) continue;
    for (const operand of [condition.getLeft(), condition.getRight()]) {
      const reference = unwrap(operand); const declaration = declarationOf(reference);
      if (declaration) selectors.set(declaration, { name: reference.getText(), owner: model.owner(declaration) });
    }
  }
  const selection = (expression: Node, seen = new Set<Node>()): Selection | undefined => {
    const node = unwrap(expression);
    if (seen.has(node) || seen.size > 8) return undefined; seen.add(node);
    if (Node.isIdentifier(node)) {
      const declaration = declarationOf(node);
      if (!declaration || !Node.isVariableDeclaration(declaration) || declaration.getParentIfKind(SyntaxKind.VariableDeclarationList)?.getDeclarationKind() !== 'const') return undefined;
      const initializer = declaration.getInitializer(); return initializer ? selection(initializer, seen) : undefined;
    }
    if (!Node.isBinaryExpression(node)) return undefined;
    if (node.getOperatorToken().getKind() === SyntaxKind.AmpersandAmpersandToken) {
      const left = selection(node.getLeft(), new Set(seen)); const right = selection(node.getRight(), new Set(seen));
      if (left && right && (left.owner !== right.owner || left.selector !== right.selector || left.key !== right.key)) return undefined;
      return left ?? right;
    }
    if (node.getOperatorToken().getKind() !== SyntaxKind.EqualsEqualsEqualsToken) return undefined;
    for (const [reference, value] of [[node.getLeft(), node.getRight()], [node.getRight(), node.getLeft()]]) {
      const declaration = declarationOf(unwrap(reference)); const key = literal(unwrap(value));
      const selector = declaration ? selectors.get(declaration) : undefined;
      if (selector && key !== undefined) return { key, selector: selector.name, owner: selector.owner };
    }
    return undefined;
  };
  return element => {
    const ancestors = element.getAncestors(); const contained = new Set<Node>([element, ...ancestors]);
    const metadata = (value: Selection, evidence: string) => ({ screen_key: value.key, screen_selector: value.selector,
      screen_selector_owner: value.owner, screen_evidence: evidence, screen_selection_evidence: 'navigation_aria_current_page' });
    for (const ancestor of [element, ...ancestors]) {
      if (model.callables.has(ancestor)) break;
      if (Node.isJsxElement(ancestor) || Node.isJsxSelfClosingElement(ancestor)) {
        const opening = Node.isJsxElement(ancestor) ? ancestor.getOpeningElement() : ancestor;
        const active = opening.getAttributes().find(attribute => Node.isJsxAttribute(attribute) && attribute.getNameNode().getText() === 'active');
        const initializer = active && Node.isJsxAttribute(active) ? active.getInitializer() : undefined;
        const expression = initializer && Node.isJsxExpression(initializer) ? initializer.getExpression() : undefined;
        const value = expression ? selection(expression) : undefined;
        if (value) return metadata(value, 'active_prop');
        // A surrounding component owns its children rendering. Do not infer its nested JSX to
        // be another screen from an outer guard unless it explicitly passes an active selector.
        if (ancestor !== element && /^[A-Z]/.test(opening.getTagNameNode().getText())) break;
      }
      if (Node.isConditionalExpression(ancestor) && contained.has(ancestor.getWhenTrue())) {
        const value = selection(ancestor.getCondition()); if (value) return metadata(value, 'conditional_guard');
      }
      if (Node.isBinaryExpression(ancestor) && ancestor.getOperatorToken().getKind() === SyntaxKind.AmpersandAmpersandToken && contained.has(ancestor.getRight())) {
        const value = selection(ancestor.getLeft()); if (value) return metadata(value, 'conditional_guard');
      }
    }
    return {};
  };
}
