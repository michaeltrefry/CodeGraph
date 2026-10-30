import { Node, SyntaxKind } from 'ts-morph';
import type { GraphNodeDto, PendingEdgeDto } from './protocol';
import { SemanticModel } from './semanticModel';
import { lazyWrapperTargets } from './reactLazyWrappers';

interface Provider { qn: string; context: string; element: Node; values: Set<string>; dynamic: boolean; }
interface Render { owner: string; target: string; element: Node; uncertain?: boolean; }

/** Walk observed render trees with a provider environment; never flatten provider values into
 * a global registry. A provider returned by a component does not provide its own hooks. */
export function analyzeReact(model: SemanticModel, nodes: GraphNodeDto[], edges: PendingEdgeDto[], diagnostic: (message: string) => void): void {
  const work = model.work;
  const reactApi = (call: import('ts-morph').CallExpression): string | undefined => model.reactApi(call);
  const ownerAt = new Map<Node, string>();
  const jsxParent = new Map<Node, Node | undefined>();
  const jsxOrder: Node[] = []; const callsInSource: Node[] = []; const expressions: Node[] = []; const attributes: Node[] = [];
  const orderedElements: Node[] = [];
  // Immutable source facts: one charged, iterative AST walk, before any environment traversal.
  for (const file of model.files) {
    const elements: Node[] = []; const selfClosing: Node[] = [];
    const pending: Array<{ node: Node; owner: string; jsx?: Node; depth: number }> = [{ node: file, owner: model.fileQN(file), depth: 0 }];
    while (pending.length) {
      work.charge(); const frame = pending.pop()!; const node = frame.node;
      ownerAt.set(node, frame.owner);
      const isElement = Node.isJsxElement(node) || Node.isJsxSelfClosingElement(node);
      const depth = frame.depth + (isElement ? 1 : 0); work.checkDepth(depth, 256);
      if (isElement) { jsxParent.set(node, frame.jsx); jsxOrder.push(node); (Node.isJsxElement(node) ? elements : selfClosing).push(node); }
      if (Node.isCallExpression(node)) callsInSource.push(node);
      if (Node.isJsxExpression(node)) { expressions.push(node); jsxParent.set(node, frame.jsx); }
      if (Node.isJsxAttribute(node)) attributes.push(node);
      const owner = model.callables.get(node) ?? frame.owner;
      const children: Node[] = []; node.forEachChild(child => { work.charge(); children.push(child); });
      for (let index = children.length - 1; index >= 0; index--) { work.charge(); pending.push({ node: children[index], owner, jsx: isElement ? node : frame.jsx, depth }); }
    }
    // Retain the old per-file element/self-closing order for stable external-symbol ownership.
    work.charge(elements.length + selfClosing.length + 1);
    for (const element of elements) orderedElements.push(element);
    for (const element of selfClosing) orderedElements.push(element);
  }
  const ownerOf = (node: Node): string => ownerAt.get(node) ?? model.owner(node);
  const screenMetadata = declaredScreenMetadata(model, attributes, ownerOf);
  const lazyTargets = lazyWrapperTargets(model, diagnostic);
  const contexts = new Map<string, { declaration: Node; defaults: Set<string> }>();
  const providers = new Map<Node, Provider>();
  const consumers = new Map<string, Set<string>>();
  const renders: Render[] = [];
  const contextUses: { owner: string; context: string }[] = [];
  const providerCounts = new Map<string, number>();
  const external = new Set<string>(); const renderable = new Map<string, GraphNodeDto>();
  for (const node of nodes) { work.text(node.qualifiedName); external.add(node.qualifiedName); if (['Component','Function','Class','Method'].includes(node.label)) renderable.set(node.qualifiedName,node); }
  const callableQNs = new Set<string>(); for (const qn of model.callables.values()) { work.text(qn); callableQNs.add(qn); }
  const chargeProperties = (properties: unknown): void => {
    const pending: Array<{value: unknown; depth: number}> = [{value: properties,depth:0}];
    while (pending.length) {
      const {value,depth} = pending.pop()!; work.checkDepth(depth,64);
      if (typeof value === 'string') { work.charge(value.length * 6 + 2); continue; }
      if (Array.isArray(value)) { work.charge(value.length + 2); for (const item of value) { work.charge(); pending.push({value:item,depth:depth+1}); } continue; }
      if (value !== null && typeof value === 'object') {
        const keys = Object.keys(value); work.charge(keys.length + 2);
        for (const key of keys) { work.charge(key.length * 6 + 4); pending.push({value:(value as Record<string,unknown>)[key],depth:depth+1}); }
      } else work.charge(16);
    }
  };
  const emitEdge = (edge: PendingEdgeDto): void => { work.charge((edge.sourceQN.length + edge.targetQN.length + edge.type.length) * 6 + 8); chargeProperties(edge.properties); edges.push(edge); };
  const emitNode = (node: GraphNodeDto): void => { work.charge((node.name.length + node.qualifiedName.length + node.filePath.length) * 6 + 8); chargeProperties(node.properties); nodes.push(node); };
  const memoReturns = new Map<Node, import('ts-morph').ReturnStatement[]>();
  const returnsIn = (callback: Node, body: Node): import('ts-morph').ReturnStatement[] => {
    work.charge(); const cached = memoReturns.get(callback); if (cached) return cached;
    const result: import('ts-morph').ReturnStatement[] = []; const pending: Node[] = [body];
    while (pending.length) { work.charge(); const node = pending.pop()!; if (Node.isReturnStatement(node)) result.push(node);
      if (node !== body && model.callables.has(node)) continue;
      node.forEachChild(child => { work.charge(); pending.push(child); }); }
    memoReturns.set(callback,result); return result;
  };
  const callableValues = (expression: Node | undefined, seen = new Set<Node>()): Set<string> => {
    work.charge(seen.size + 1); work.checkDepth(seen.size, 128);
    const result = new Set<string>(); const add = (value: string): void => { work.text(value); result.add(value); };
    const history = (): Set<Node> => { work.charge(seen.size + 1); return new Set(seen); };
    if (!expression || seen.has(expression)) return result; seen.add(expression);
    const resolved = model.resolve(expression) ?? expression;
    if (Node.isCallExpression(resolved) && reactApi(resolved) === 'useMemo') {
      const callback = resolved.getArguments()[0];
      if (callback && (Node.isArrowFunction(callback) || Node.isFunctionExpression(callback))) {
        const body = callback.getBody();
        if (!Node.isBlock(body)) return callableValues(body, seen);
        for (const returned of returnsIn(callback, body)) {
          work.charge();
          if (ownerOf(returned) !== model.callables.get(callback)) continue;
          for (const value of callableValues(returned.getExpression(), history())) add(value);
        }
      }
      return result;
    }
    if (Node.isConditionalExpression(resolved)) {
      for (const branch of [resolved.getWhenTrue(), resolved.getWhenFalse()]) for (const value of callableValues(branch, history())) add(value);
      work.text(ownerOf(resolved)); diagnostic(`contextValueAlternatives: ${ownerOf(resolved)}: conditional provided value`);
      return result;
    }
    if (Node.isVariableDeclaration(resolved) || Node.isPropertyDeclaration(resolved) || Node.isPropertyAssignment(resolved)) return callableValues(resolved.getInitializer(), seen);
    const qn = model.declarations.get(resolved);
    if (qn && callableQNs.has(qn)) add(qn);
    if (Node.isObjectLiteralExpression(resolved)) {
      work.charge(resolved.compilerNode.properties.length + 1);
      for (const prop of resolved.getProperties()) {
        work.charge();
        if (Node.isPropertyAssignment(prop)) for (const value of callableValues(prop.getInitializer(), history())) add(value);
        if (Node.isShorthandPropertyAssignment(prop)) {
          const symbol = prop.getValueSymbol(); work.charge((symbol?.compilerSymbol.declarations?.length ?? 0) + 1);
          const declaration = symbol?.getDeclarations()[0];
          for (const value of callableValues(declaration, history())) add(value);
        }
        if (Node.isSpreadAssignment(prop)) for (const value of callableValues(prop.getExpression(), history())) add(value);
        if (Node.isMethodDeclaration(prop)) { const target = model.declarations.get(prop); if (target) add(target); }
      }
    }
    return result;
  };
  {
    for (const candidate of callsInSource) {
      if (!Node.isCallExpression(candidate)) continue; const call = candidate; work.charge();
      const api = reactApi(call);
      if (api === 'createContext') {
        const declaration = call.getParent();
        if (!declaration || !Node.isVariableDeclaration(declaration)) continue;
        const qn = model.declarations.get(declaration)!;
        contexts.set(qn, { declaration, defaults: callableValues(call.getArguments()[0]) });
        emitNode({ label: 'Property', name: declaration.getName(), qualifiedName: qn, filePath: model.relative(call),
          startLine: declaration.getStartLineNumber(), endLine: declaration.getEndLineNumber(), properties: { framework: 'react', context_kind: 'context' } });
        emitEdge({ sourceQN: model.fileQN(call), targetQN: qn, type: 'DEFINES' });
      }
      if (api === 'useContext' || api === 'use') {
        const context = model.target(call.getArguments()[0]);
        if (!context) { work.charge(ownerOf(call).length + call.getFullWidth() + 1); diagnostic(`contextUnresolved: ${ownerOf(call)}: ${call.getText().slice(0, 120)}`); continue; }
        contextUses.push({ owner: ownerOf(call), context });
      }
    }
  }
  for (const { owner, context } of contextUses) {
    work.charge(owner.length + context.length + 1);
    if (!contexts.has(context)) { diagnostic(`contextUnresolved: ${owner}: ${context} is not a statically known context`); continue; }
    if (!consumers.has(owner)) consumers.set(owner, new Set()); consumers.get(owner)!.add(context);
    emitEdge({ sourceQN: owner, targetQN: context, type: 'INJECTS', properties: { framework: 'react', context_kind: 'consumer' } });
  }
  const contextTag = (tag: Node): string | undefined => {
    const expression = Node.isPropertyAccessExpression(tag) && tag.getName() === 'Provider' ? tag.getExpression() : tag;
    const target = model.target(expression); return target && contexts.has(target) ? target : undefined;
  };
  {
    for (const element of orderedElements) {
      work.charge(); if (!(Node.isJsxElement(element) || Node.isJsxSelfClosingElement(element))) continue; const file = element.getSourceFile();
      const opening = Node.isJsxElement(element) ? element.getOpeningElement() : element;
      const tag = opening.getTagNameNode(); work.charge(tag.getFullWidth() + 1); const text = tag.getText();
      const owner = ownerOf(element); const context = contextTag(tag);
      if (context) {
        work.charge(opening.compilerNode.attributes.properties.length + 1);
        const value = opening.getAttributes().find(attribute => Node.isJsxAttribute(attribute) && attribute.getNameNode().getText() === 'value');
        const init = value && Node.isJsxAttribute(value) ? value.getInitializer() : undefined;
        const expression = init && Node.isJsxExpression(init) ? init.getExpression() : init;
        const values = callableValues(expression);
        const ordinal = providerCounts.get(owner) ?? 0; providerCounts.set(owner, ordinal + 1);
        work.charge(owner.length + context.length + 32);
        const qn = `${owner}.$provider${ordinal}.${context.split(/[.#]/).pop()}`;
        if (expression) work.charge(expression.getFullWidth() + 1);
        const provider: Provider = { qn, context, element, values, dynamic: !!expression && values.size === 0 && !['null', 'undefined'].includes(expression.getText()) };
        providers.set(element, provider);
        emitNode({ label: 'Property', name: `${context.split(/[.#]/).pop()} provider`, qualifiedName: qn, filePath: model.relative(element),
          startLine: element.getStartLineNumber(), endLine: element.getEndLineNumber(), properties: { framework: 'react', context_kind: 'provider', context_qn: context, value_resolution: provider.dynamic ? 'unresolved' : 'static' } });
        emitEdge({ sourceQN: owner, targetQN: qn, type: 'DEFINES' }); emitEdge({ sourceQN: qn, targetQN: context, type: 'REGISTERS', properties: { framework: 'react', context_kind: 'provider' } });
        for (const value of values) emitEdge({ sourceQN: qn, targetQN: value, type: 'CALLS', properties: { call_kind: 'context_value' } });
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
        const reference = model.externalReference(tag);
        if (reference) {
          target = reference.qualifiedName;
          if (!external.has(target)) {
            emitNode({ label: 'ExternalSymbol', name: [reference.properties.export_name, reference.properties.member_path].filter(Boolean).join('.'),
              qualifiedName: target, filePath: model.relative(file), startLine: 0, endLine: 0,
              properties: { ...reference.properties, framework: 'react' } });
            external.add(target);
          }
        }
      }
      if (target) { work.charge(owner.length + target.length + 1); renders.push({ owner, target, element }); emitEdge({ sourceQN: owner, targetQN: target, type: 'RENDERS', properties: { framework: 'react', ...screenMetadata(element) } }); }
      else { work.charge(owner.length + text.length + 1); diagnostic(`renderUnresolved: ${owner}: ${text}`); }
      for (const lazy of lazyTargets(tag)) {
        if (lazy.qualifiedName === target || !renderable.has(lazy.qualifiedName)) continue;
        work.charge(owner.length + lazy.qualifiedName.length + lazy.filePath.length + lazy.wrapperChain.reduce((size, name) => size + name.length, 0) + 1);
        renders.push({ owner, target: lazy.qualifiedName, element, uncertain: true });
        emitEdge({ sourceQN: owner, targetQN: lazy.qualifiedName, type: 'RENDERS', properties: { framework: 'react', ...screenMetadata(element),
          lazy_wrapper: true, lazy_target_file: lazy.filePath, lazy_wrapper_chain: lazy.wrapperChain,
          confidence_band: 'medium', candidate: true, lazy_evidence: 'source_initializer_to_react_lazy' } });
      }
    }
  }
  const calls = new Map<string, Set<string>>();
  for (const edge of edges) {
    work.charge(); if (edge.type !== 'CALLS') continue;
    work.charge(edge.sourceQN.length + edge.targetQN.length + 1);
    if (!calls.has(edge.sourceQN)) calls.set(edge.sourceQN, new Set()); calls.get(edge.sourceQN)!.add(edge.targetQN);
  }
  const renderAt = new Map<Node, Render[]>();
  for (const render of renders) { work.charge(); if (!renderAt.has(render.element)) renderAt.set(render.element, []); renderAt.get(render.element)!.push(render); }
  const rootsByOwner = new Map<string, Render[]>(); const childrenByRender = new Map<Render, Render[]>();
  const nearestRender = new Map<Node, Render | undefined>();
  interface ProviderPath { provider: Provider; parent?: ProviderPath; }
  const providerPaths = new Map<Node, ProviderPath | undefined>();
  const ancestorPaths = new Map<Node, ProviderPath | undefined>();
  for (const element of jsxOrder) {
    work.charge(); const parent = jsxParent.get(element); const owner = ownerOf(element);
    const prior = parent ? nearestRender.get(parent) : undefined;
    const ancestor = prior?.owner === owner ? prior : undefined;
    const records = renderAt.get(element) ?? [];
    for (const render of records) {
      work.charge();
      if (ancestor) { if (!childrenByRender.has(ancestor)) childrenByRender.set(ancestor, []); childrenByRender.get(ancestor)!.push(render); }
      else { work.text(owner); if (!rootsByOwner.has(owner)) rootsByOwner.set(owner, []); rootsByOwner.get(owner)!.push(render); }
    }
    nearestRender.set(element, records[0] ?? ancestor);
    const path = parent ? providerPaths.get(parent) : undefined; ancestorPaths.set(element, path);
    const provider = providers.get(element); providerPaths.set(element, provider ? { provider, parent: path } : path);
  }
  const slotsByOwner = new Map<string, Node[]>(); const slotPathsByOwner = new Map<string, Set<ProviderPath | undefined>>();
  const parameterBinding = (declaration: Node): boolean => {
    if (!Node.isBindingElement(declaration)) return false;
    for (let parent: Node | undefined = declaration.getParent(); parent; parent = parent.getParent()) { work.charge(); if (Node.isParameterDeclaration(parent)) return true; }
    return false;
  };
  for (const slot of expressions) {
    work.charge(); if (!Node.isJsxExpression(slot)) continue; const expression = slot.getExpression(); if (!expression) continue;
    let childSlot = false;
    if (Node.isIdentifier(expression)) work.charge(expression.getFullWidth() + 1);
    if (Node.isIdentifier(expression) && expression.getText() === 'children') {
      const symbol = expression.getSymbol(); work.charge((symbol?.compilerSymbol.declarations?.length ?? 0) + 1);
      childSlot = symbol?.getDeclarations().some(declaration => parameterBinding(declaration)) ?? false;
    }
    if (Node.isPropertyAccessExpression(expression)) work.charge(expression.getNameNode().getFullWidth() + 1);
    if (Node.isPropertyAccessExpression(expression) && expression.getName() === 'children') {
      const symbol = expression.getExpression().getSymbol(); work.charge((symbol?.compilerSymbol.declarations?.length ?? 0) + 1);
      childSlot = symbol?.getDeclarations().some(Node.isParameterDeclaration) ?? false;
    }
    if (!childSlot) continue;
    const owner = ownerOf(slot); work.text(owner);
    const parent = jsxParent.get(slot); const path = parent ? providerPaths.get(parent) : undefined; ancestorPaths.set(slot, path);
    if (!slotPathsByOwner.has(owner)) slotPathsByOwner.set(owner,new Set());
    // Equal lexical paths apply the same overrides to every incoming environment.
    if (slotPathsByOwner.get(owner)!.has(path)) continue; slotPathsByOwner.get(owner)!.add(path);
    if (!slotsByOwner.has(owner)) slotsByOwner.set(owner, []); slotsByOwner.get(owner)!.push(slot);
  }
  const overrides = new Map<ProviderPath, ReadonlyMap<string, Provider>>();
  const withProviders = (element: Node, inherited: Map<string, Provider>): Map<string, Provider> => {
    work.charge(); const path = ancestorPaths.get(element); if (!path) return inherited;
    let values = overrides.get(path);
    if (!values) {
      const chain: Provider[] = []; for (let current: ProviderPath | undefined = path; current; current = current.parent) { work.charge(); chain.push(current.provider); }
      const next = new Map<string, Provider>();
      for (let index = chain.length - 1; index >= 0; index--) { work.text(chain[index].context); next.set(chain[index].context, chain[index]); }
      values = next; overrides.set(path, values);
    }
    work.charge(inherited.size + values.size + 1); const next = new Map(inherited);
    for (const [context, provider] of values) { work.text(context); next.set(context, provider); }
    return next;
  };
  const contextPredecessors = new Map<string, Set<string>>();
  const predecessor = (target: string, owner: string): void => { work.charge(target.length + owner.length + 1); if (!contextPredecessors.has(target)) contextPredecessors.set(target,new Set()); contextPredecessors.get(target)!.add(owner); };
  for (const [owner, targets] of calls) for (const target of targets) predecessor(target,owner);
  for (const render of renders) if (!render.uncertain) predecessor(render.target,render.owner);
  const relevant = new Set<string>(consumers.keys()); const reachable = [...consumers.keys()]; work.charge(reachable.length * 2 + 1);
  for (let cursor=0;cursor<reachable.length;cursor++) {
    const owner=reachable[cursor]; work.text(owner);
    for (const parent of contextPredecessors.get(owner) ?? []) { work.text(parent); if (!relevant.has(parent)) { relevant.add(parent); reachable.push(parent); } }
  }
  const bindings = new Map<string, Map<string, Set<string>>>(); const visited = new Set<string>();
  type Step = { kind: 'owner'; owner: string; environment: Map<string, Provider>; depth: number }
    | { kind: 'render'; render: Render; environment: Map<string, Provider>; depth: number; nesting: number }
    | { kind: 'children'; slots: readonly Node[]; children: readonly Render[]; inherited: Map<string, Provider>; environment?: Map<string, Provider>; slot: number; child: number; depth: number; nesting: number };
  const pending: Step[] = [];
  const targets = new Set<string>(); for (const render of renders) { work.text(render.target); targets.add(render.target); }
  for (let index = nodes.length - 1; index >= 0; index--) { const node = nodes[index]; work.charge(); if (node.label === 'Component' && relevant.has(node.qualifiedName) && !targets.has(node.qualifiedName)) pending.push({ kind: 'owner', owner: node.qualifiedName, environment: new Map(), depth: 0 }); }
  // Provider selections remain per path. Only immutable ancestry/slot facts are cached.
  while (pending.length && consumers.size) {
    work.charge(); const step = pending.pop()!; if (step.kind === 'owner' && !relevant.has(step.owner)) continue; work.checkDepth(step.depth, 100);
    if (step.kind === 'children') {
      work.checkDepth(step.nesting,256);
      const environment = step.environment ?? withProviders(step.slots[step.slot],step.inherited);
      const child = step.children[step.child];
      // A continuation keeps only one slot/child pair on the frontier, preserving DFS order.
      if (step.child + 1 < step.children.length) { work.charge(); pending.push({ ...step, environment, child: step.child + 1 }); }
      else if (step.slot + 1 < step.slots.length) { work.charge(); pending.push({ ...step, environment: undefined, slot: step.slot + 1, child: 0 }); }
      work.charge(); pending.push({ kind:'render', render:child, environment, depth:step.depth, nesting:step.nesting }); continue;
    }
    if (step.kind === 'render') {
      work.checkDepth(step.nesting, 256); const render = step.render; if (render.uncertain) continue;
      const next = withProviders(render.element, step.environment); const children = childrenByRender.get(render) ?? [];
      if (children.length) {
        const slots = slotsByOwner.get(render.target) ?? [];
        if (!slots.length) { work.charge(render.owner.length + render.target.length + 1); diagnostic(`contextChildrenUnresolved: ${render.owner} -> ${render.target}`); }
        if (slots.length) { work.charge(); pending.push({ kind:'children', slots, children, inherited:next, slot:0, child:0, depth:step.depth, nesting:step.nesting + 1 }); }
      }
      work.charge(); pending.push({ kind: 'owner', owner: render.target, environment: next, depth: step.depth + 1 }); continue;
    }
    let size = step.owner.length + 1; for (const [context, provider] of step.environment) size += context.length + provider.qn.length + 2;
    work.charge(size + step.environment.size + 1);
    const entries = [...step.environment].map(([context, provider]) => `${context}=${provider.qn}`);
    entries.sort((left, right) => { work.charge(left.length + right.length + 1); return left < right ? -1 : left > right ? 1 : 0; });
    const key = `${step.owner}|${entries.join('|')}`; if (visited.has(key)) continue; visited.add(key);
    for (const context of consumers.get(step.owner) ?? []) {
      const provider = step.environment.get(context); work.charge(step.owner.length + context.length + (provider?.qn.length ?? context.length + 8) + 1);
      if (!bindings.has(step.owner)) bindings.set(step.owner, new Map()); const byContext = bindings.get(step.owner)!;
      if (!byContext.has(context)) byContext.set(context, new Set()); byContext.get(context)!.add(provider?.qn ?? `${context}#default`);
    }
    const owned = rootsByOwner.get(step.owner) ?? [];
    for (let index = owned.length - 1; index >= 0; index--) { work.charge(); pending.push({ kind: 'render', render: owned[index], environment: step.environment, depth: step.depth, nesting: 0 }); }
    const targets = calls.get(step.owner); work.charge((targets?.size ?? 0) + 1); const callees = [...(targets ?? [])];
    for (let index = callees.length - 1; index >= 0; index--) { work.charge(); pending.push({ kind: 'owner', owner: callees[index], environment: step.environment, depth: step.depth + 1 }); }
  }
  for (const owner of consumers.keys()) { work.text(owner); if (!bindings.has(owner)) diagnostic(`contextScopeUnresolved: ${owner} has no observed render/call path`); }
  const providerByQN = new Map<string, Provider>(); for (const provider of providers.values()) { work.text(provider.qn); providerByQN.set(provider.qn,provider); }
  for (const [owner, byContext] of bindings) for (const [context, alternatives] of byContext) {
    work.charge(owner.length + context.length + alternatives.size + 1);
    for (const alternative of alternatives) work.text(alternative);
    const ambiguous = alternatives.size > 1;
    if (ambiguous) diagnostic(`contextAlternatives: ${owner}: ${context}: ${[...alternatives].sort((left,right)=>{work.charge(left.length+right.length+1);return left<right?-1:left>right?1:0;}).join(', ')}`);
    for (const alternative of alternatives) {
      const properties = { framework: 'react', context_qn: context, context_kind: 'binding', confidence_band: ambiguous ? 'medium' : 'high', candidate: ambiguous };
      const provider = providerByQN.get(alternative);
      if (provider) emitEdge({ sourceQN: owner, targetQN: provider.qn, type: 'INJECTS', properties });
      else for (const target of contexts.get(context)?.defaults ?? []) emitEdge({ sourceQN: owner, targetQN: target, type: 'CALLS', properties: { ...properties, call_kind: 'context_default' } });
    }
  }
  canonicalizeRenderEdges(edges, model.work);
}

/** Persistence identifies an edge by source/target/type. Preserve canonical evidence once
 * per identity, including several lazy factories or screen selections sharing one declaration. */
function canonicalizeRenderEdges(edges: PendingEdgeDto[], work: import('./extractionWork').ExtractionWorkBudget): void {
  const canonical = (value: unknown, depth = 0): unknown => {
    work.checkDepth(depth, 64);
    if (typeof value === 'string') { work.charge(value.length * 6 + 1); return value; }
    if (Array.isArray(value)) { work.charge(value.length + 1); return value.map(item => canonical(item, depth + 1)); }
    if (value !== null && typeof value === 'object') {
      const entries = Object.entries(value); work.charge(entries.length + 1);
      return Object.fromEntries(entries.sort(([left],[right])=>{work.charge(left.length+right.length+1);return left.localeCompare(right);}).map(([key,item])=>{work.text(key);return [key,canonical(item,depth+1)];}));
    }
    return value;
  };
  const groups = new Map<string, PendingEdgeDto[]>();
  for (const edge of edges) {
    work.charge(); if (edge.type !== 'RENDERS' || edge.properties?.framework !== 'react') continue;
    work.charge(edge.sourceQN.length + edge.targetQN.length + 8); const key = JSON.stringify([edge.sourceQN, edge.targetQN]);
    if (!groups.has(key)) groups.set(key, []); groups.get(key)!.push(edge);
  }
  const discard = new Set<PendingEdgeDto>();
  for (const group of groups.values()) {
    work.charge(group.length + 1); if (group.length < 2) continue;
    const selections = new Map<string, Record<string, unknown>>();
    const witnesses = new Map<string, Record<string, unknown>>();
    for (const edge of group) {
      work.charge(); const properties = edge.properties ?? {};
      const priorSelections = Array.isArray(properties.screen_selections) ? properties.screen_selections as Record<string, unknown>[] :
        properties.screen_key !== undefined ? [Object.fromEntries(Object.entries(properties).filter(([key]) => key.startsWith('screen_')))] : [];
      for (const selection of priorSelections) {
        const normalized = canonical(selection) as Record<string, unknown>; selections.set(JSON.stringify(normalized), normalized);
      }
      const witness = canonical(Object.fromEntries(Object.entries(properties).filter(([key]) => !key.startsWith('screen_')))) as Record<string, unknown>;
      witnesses.set(JSON.stringify(witness), witness);
    }
    const ordered = [...witnesses].sort(([left], [right]) => { work.charge(left.length + right.length + 1); return left.localeCompare(right); }).map(([, value]) => value);
    // A direct declaration reference is a stronger primary witness when a lazy candidate
    // also reaches the same target; preserve the candidate as alternative source evidence.
    const primary = ordered.find(witness => witness.lazy_wrapper !== true) ?? ordered[0];
    const first = group[0]; first.properties = { ...primary };
    if (ordered.length > 1) first.properties.render_evidence = ordered;
    const screenSelections = [...selections].sort(([left], [right]) => { work.charge(left.length + right.length + 1); return left.localeCompare(right); }).map(([, value]) => value);
    if (screenSelections.length === 1) Object.assign(first.properties, screenSelections[0]);
    else if (screenSelections.length > 1) first.properties.screen_selections = screenSelections;
    for (let index=1;index<group.length;index++) { work.charge(); discard.add(group[index]); }
  }
  if (discard.size) { let write=0; for (const edge of edges) { work.charge(); if (!discard.has(edge)) edges[write++]=edge; } edges.length=write; }
}

/** Preserve declared screen selection, not arbitrary conditional visibility. A selector must
 * first be used by an actual navigation landmark's aria-current=page declaration. Only positive
 * literal guards using that same symbol (or immutable boolean aliases) become screen metadata.
 * Keep-alive wrappers can pass the declared selection through their explicit active prop.
 */
function declaredScreenMetadata(model: SemanticModel, attributes: readonly Node[], ownerOf: (node: Node) => string): (element: Node) => Record<string, unknown> {
  interface Selection { key: string; selector: string; owner: string; }
  const work = model.work;
  const selectors = new Map<Node, { name: string; owner: string }>();
  const unwrap = (node: Node): Node => {
    while (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isNonNullExpression(node)) { work.charge(); node = node.getExpression(); }
    return node;
  };
  const declarationOf = (node: Node): Node | undefined => {
    if (!Node.isIdentifier(node)) return undefined;
    const symbol = node.getSymbol(); work.charge((symbol?.compilerSymbol.declarations?.length ?? 0) + 1);
    const declarations = symbol?.getDeclarations() ?? [];
    return declarations.length === 1 ? declarations[0] : undefined;
  };
  const literal = (node: Node): string | undefined => { work.charge(node.getFullWidth() + 1); return Node.isStringLiteral(node) || Node.isNoSubstitutionTemplateLiteral(node) ? node.getLiteralText() : undefined; };
  const navigationAt = new Map<Node, boolean>();
  const inNavigation = (node: Node): boolean => {
    const trail: Node[] = []; let result = false;
    for (let current: Node | undefined = node; current; current = current.getParent()) {
      work.charge(); if (navigationAt.has(current)) { result = navigationAt.get(current)!; break; }
      trail.push(current);
      if (Node.isJsxElement(current)) { const tag = current.getOpeningElement().getTagNameNode(); work.charge(tag.getFullWidth() + 1); if (tag.getText() === 'nav') { result = true; break; } }
    }
    for (const ancestor of trail) { work.charge(); navigationAt.set(ancestor,result); } return result;
  };
  for (const attribute of attributes) {
    model.work.charge(); if (!Node.isJsxAttribute(attribute)) continue;
    work.charge(attribute.getNameNode().getFullWidth() + 1);
    if (attribute.getNameNode().getText() !== 'aria-current') continue;
    const navigation = inNavigation(attribute);
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
      if (declaration) { work.charge(reference.getFullWidth() + ownerOf(declaration).length + 1); selectors.set(declaration, { name: reference.getText(), owner: ownerOf(declaration) }); }
    }
  }
  const selection = (expression: Node, seen = new Set<Node>()): Selection | undefined => {
    work.charge(seen.size + 1); const node = unwrap(expression);
    if (seen.has(node) || seen.size > 8) return undefined; seen.add(node);
    if (Node.isIdentifier(node)) {
      const declaration = declarationOf(node);
      if (!declaration || !Node.isVariableDeclaration(declaration) || declaration.getParentIfKind(SyntaxKind.VariableDeclarationList)?.getDeclarationKind() !== 'const') return undefined;
      const initializer = declaration.getInitializer(); return initializer ? selection(initializer, seen) : undefined;
    }
    if (!Node.isBinaryExpression(node)) return undefined;
    if (node.getOperatorToken().getKind() === SyntaxKind.AmpersandAmpersandToken) {
      work.charge(seen.size * 2 + 1); const left = selection(node.getLeft(), new Set(seen)); const right = selection(node.getRight(), new Set(seen));
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
  if (!selectors.size) return () => ({});
  const activeAt = new Map<Node, Selection | undefined>();
  const activeSelection = (element: import('ts-morph').JsxElement | import('ts-morph').JsxSelfClosingElement): Selection | undefined => {
    work.charge(); if (activeAt.has(element)) return activeAt.get(element);
    const opening = Node.isJsxElement(element) ? element.getOpeningElement() : element;
    work.charge(opening.compilerNode.attributes.properties.length + 1);
    let active: import('ts-morph').JsxAttribute | undefined;
    for (const attribute of opening.getAttributes()) {
      work.charge(); if (!Node.isJsxAttribute(attribute)) continue; work.charge(attribute.getNameNode().getFullWidth() + 1);
      if (attribute.getNameNode().getText() === 'active') { active=attribute; break; }
    }
    const initializer = active?.getInitializer(); const expression = initializer && Node.isJsxExpression(initializer) ? initializer.getExpression() : undefined;
    const value = expression ? selection(expression) : undefined; activeAt.set(element,value); return value;
  };
  const cached = new Map<Node, Record<string, unknown>>();
  const lookup = (element: Node): Record<string, unknown> => {
    const ancestors: Node[] = []; for (let current = element.getParent(); current; current = current.getParent()) { work.charge(); ancestors.push(current); if (model.callables.has(current)) break; } work.charge(ancestors.length * 3 + 1); const contained = new Set<Node>([element, ...ancestors]);
    const metadata = (value: Selection, evidence: string) => { work.charge(value.key.length + value.selector.length + value.owner.length + evidence.length + 8); return ({ screen_key: value.key, screen_selector: value.selector,
      screen_selector_owner: value.owner, screen_evidence: evidence, screen_selection_evidence: 'navigation_aria_current_page' }); };
    for (const ancestor of [element, ...ancestors]) {
      work.charge();
      if (model.callables.has(ancestor)) break;
      if (Node.isJsxElement(ancestor) || Node.isJsxSelfClosingElement(ancestor)) {
        const opening = Node.isJsxElement(ancestor) ? ancestor.getOpeningElement() : ancestor;
        const value = activeSelection(ancestor);
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
  return element => { work.charge(); if (!cached.has(element)) cached.set(element, lookup(element)); return cached.get(element)!; };
}
