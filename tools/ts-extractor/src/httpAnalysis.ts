import { CallExpression, Node, SyntaxKind, VariableDeclarationKind } from 'ts-morph';
import type { GraphNodeDto, PendingEdgeDto } from './protocol';
import { isCallable, sameSymbol, SemanticModel } from './semanticModel';
import { isObject, isUnknown, StaticEnvironment, StaticEvaluator, StaticValue, staticKey, unknown } from './staticEvaluation';

const METHODS = new Set(['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS', 'TRACE', 'CONNECT']);
interface HttpFact { method?: string; url?: string; kind: string; chain: string[]; requestDto?: string; reason?: string; }
export interface HttpCoverage { direct: number; wrapped: number; unresolved: number; bounded: number; complete: boolean; }

/** Source-derived HTTP summaries. A function name is never evidence of a wrapper: a
 * resolved admitted body must lead to a recognized transport. All caches are per run. */
export function analyzeHttp(model: SemanticModel, _nodes: GraphNodeDto[], edges: PendingEdgeDto[], diagnostic: (message: string) => void, serviceName?: string | null): HttpCoverage {
  const evaluator = new StaticEvaluator(model, diagnostic);
  const coverage: HttpCoverage = { direct: 0, wrapped: 0, unresolved: 0, bounded: 0, complete: true };
  const cache = new Map<Node, Map<string, HttpFact[]>>();
  const calls = new Map<Node, CallExpression[]>();
  const seenEdges = new Set(edges.filter(e => e.type === 'HTTP_CALLS').map(e => `${e.sourceQN}|${e.targetQN}`));
  let samples = 0, summaries = 0;
  const diag = (message: string) => { if (samples++ < 60) diagnostic(`httpUnresolved: ${message}`); };
  const value = (node: Node | undefined, env: StaticEnvironment): StaticValue => evaluator.evaluate(node, env);
  const envKey = (env: StaticEnvironment) => JSON.stringify([...env].map(([node, v]) =>
    [`${model.relative(node)}:${node.getPos()}`, staticKey(v)]).sort(([a], [b]) => a.localeCompare(b)));
  const summarize = (callable: Node, env: StaticEnvironment, active: Set<Node>): HttpFact[] => {
    if (summaries++ >= 50000) { coverage.complete = false; coverage.bounded++; diag('hard summary budget exceeded'); return []; }
    if (active.has(callable) || active.size >= 16) {
      coverage.bounded++; diag(`wrapper bound at ${model.callables.get(callable)}`); return [];
    }
    const key = envKey(env); const prior = cache.get(callable)?.get(key); if (prior) return prior;
    const next = new Set(active); next.add(callable);
    const qn = model.callables.get(callable) ?? model.fileQN(callable);
    let ownedCalls = calls.get(callable);
    if (!ownedCalls) { ownedCalls = callable.getDescendantsOfKind(SyntaxKind.CallExpression).filter(call => model.owner(call) === qn); calls.set(callable, ownedCalls); }
    const facts: HttpFact[] = [];
    for (const call of ownedCalls) {
      const expr = call.getExpression(); const args = call.getArguments();
      let terminal: HttpFact | undefined;
      if (isFetch(expr, evaluator)) {
        const options = value(args[1], env);
        const methodValue = options == null ? undefined : isObject(options)
          ? options.properties.has('method') ? options.properties.get('method') : options.unknownKeys ? unknown('method') : undefined : unknown('method');
        const method = methodValue === undefined ? 'GET' : typeof methodValue === 'string' && METHODS.has(methodValue.toUpperCase()) ? methodValue.toUpperCase() : undefined;
        const url = value(args[0], env);
        terminal = { method, url: typeof url === 'string' ? url : undefined, kind: 'fetch', chain: [], reason: !method ? 'unknown method' : typeof url !== 'string' ? 'unknown URL' : undefined };
      } else if (Node.isPropertyAccessExpression(expr) && isHttpClient(expr.getExpression(), model)) {
        const operation = expr.getName().toUpperCase();
        if (METHODS.has(operation) || operation === 'REQUEST') {
          const methodValue = operation === 'REQUEST' ? value(args[0], env) : operation;
          const method = typeof methodValue === 'string' && METHODS.has(methodValue.toUpperCase()) ? methodValue.toUpperCase() : undefined;
          const url = value(args[operation === 'REQUEST' ? 1 : 0], env);
          terminal = { method, url: typeof url === 'string' ? url : undefined, kind: 'httpclient', chain: [], reason: !method ? 'unknown method' : undefined };
        }
      }
      if (!terminal && Node.isPropertyAccessExpression(expr) && expr.getName() === 'send') {
        const request = args.find(arg => Node.isNewExpression(arg)) ?? args.flatMap(arg => arg.getDescendantsOfKind(SyntaxKind.NewExpression))[0];
        const target = request && Node.isNewExpression(request) ? model.resolve(request.getExpression()) : model.resolve(call.getTypeArguments()[0]);
        if (target && Node.isClassDeclaration(target)) {
          const decorator = target.getDecorator('ApiContract');
          if (decorator) {
            const object = value(decorator.getArguments()[0], env);
            const endpoint = isObject(object) ? object.properties.get('endpoint') : undefined;
            let method = isObject(object) ? object.properties.get('httpMethod') : undefined;
            // Preserve the legacy HttpMethods.post contract form without requiring its dependency.
            if (isUnknown(method)) {
              const methodNode = decorator.getArguments()[0];
              const property = methodNode && Node.isObjectLiteralExpression(methodNode) ? methodNode.getProperty('httpMethod') : undefined;
              const initializer = property && Node.isPropertyAssignment(property) ? property.getInitializer() : undefined;
              if (initializer && Node.isPropertyAccessExpression(initializer)) method = initializer.getName();
            }
            const normalized = method === undefined ? 'POST' : typeof method === 'string' && METHODS.has(method.toUpperCase()) ? method.toUpperCase() : undefined;
            terminal = { method: normalized, url: typeof endpoint === 'string' ? endpoint : undefined, kind: 'apicontract', requestDto: model.declarations.get(target), chain: [] };
          }
        }
      }
      if (terminal) { facts.push(terminal); continue; }
      const target = model.resolve(expr);
      if (!target || !model.callables.has(target) || !isCallable(target)) continue;
      const bound = evaluator.bind(target, args.map(arg => value(arg, env)), env);
      for (const fact of summarize(target, bound, next)) facts.push({ ...fact, chain: [model.callables.get(target)!, ...fact.chain] });
    }
    // Keep repeated calls to a shared helper from multiplying identical summaries.
    const unique = [...new Map(facts.map(f => [JSON.stringify(f), f])).values()];
    if (summaries < 10000) { if (!cache.has(callable)) cache.set(callable, new Map()); cache.get(callable)!.set(key, unique); }
    return unique;
  };
  const roots: Array<[Node, string]> = [...model.callables, ...model.files.map(file => [file, model.fileQN(file)] as [Node, string])];
  for (const [callable, qn] of roots) {
    const params = (Node.isFunctionDeclaration(callable) || Node.isFunctionExpression(callable) || Node.isArrowFunction(callable) || Node.isMethodDeclaration(callable) || Node.isConstructorDeclaration(callable)) ? callable.getParameters() : [];
    const env = evaluator.bind(callable, params.map(p => unknown(p.getName())));
    for (const fact of summarize(callable, env, new Set())) {
      const url = fact.url ? normalizeHttpUrl(fact.url) : undefined;
      if (!fact.method || !url) { coverage.unresolved++; diag(`${qn}: ${fact.reason ?? 'URL has no static endpoint'} (${fact.kind})`); continue; }
      const targetQN = `route:*:${fact.method}:${url.path}`;
      const key = `${qn}|${targetQN}`;
      if (seenEdges.has(key)) continue; seenEdges.add(key);
      if (fact.chain.length) coverage.wrapped++; else coverage.direct++;
      edges.push({ sourceQN: qn, targetQN, type: 'HTTP_CALLS', properties: {
        http_method: fact.method, url_pattern: url.path, http_kind: fact.kind,
        call_kind: fact.chain.length ? 'wrapper' : 'direct',
        confidence_band: url.path.includes('{param}') || url.dynamicOrigin ? 'medium' : 'high',
        ...(url.origin ? { origin: url.origin } : {}), ...(url.dynamicOrigin ? { origin_pattern: '{param}' } : {}),
        ...(fact.chain.length ? { wrapper_chain: fact.chain } : {}), ...(fact.requestDto ? { request_dto: fact.requestDto, ...(serviceName ? { service_name: serviceName } : {}) } : {}),
      } });
    }
  }
  diagnostic(`HTTP coverage: ${JSON.stringify(coverage)}`);
  return coverage;
}

export function normalizeHttpUrl(raw: string): { path: string; origin?: string; dynamicOrigin?: boolean } | undefined {
  let route = raw.trim(), origin: string | undefined, dynamicOrigin = false;
  const absolute = route.match(/^((?:https?:)?\/\/[^/\s?#]+)(.*)$/i);
  if (absolute) { origin = absolute[1]; route = absolute[2] || '/'; if (/^[?#]/.test(route)) route='/' + route; if (origin.includes('{param}')) { origin = undefined; dynamicOrigin = true; } }
  else if (route.startsWith('{param}/')) { dynamicOrigin = true; route = route.slice('{param}'.length); }
  route = route.split(/[?#]/)[0];
  if (!route.startsWith('/') || route.includes('://')) return undefined;
  route = route.replace(/\/+/g, '/').replace(/\/$/, '') || '/';
  // A host/base/version alone is not an endpoint. Never turn a fully unknown URL into one.
  const meaningful = route.split('/').filter(p => p && !/^(?:api|admin|v\d+|\{param\})$/i.test(p) && p.replace(/\{param\}/g, '').length > 0);
  if (route.includes('{param}') && !meaningful.length) return undefined;
  return { path: route, ...(origin ? { origin } : {}), ...(dynamicOrigin ? { dynamicOrigin } : {}) };
}

function isFetch(node: Node, evaluator: StaticEvaluator, seen = new Set<Node>()): boolean {
  if (seen.has(node) || seen.size > 20) return false; seen.add(node);
  if (evaluator.builtin(node, 'fetch')) return true;
  if (Node.isPropertyAccessExpression(node) && node.getName() === 'fetch' && ['window', 'globalThis', 'self'].some(name => evaluator.builtin(node.getExpression(), name))) return true;
  const declarations=node.getSymbol()?.getDeclarations() ?? [];
  if (declarations.length === 1 && Node.isVariableDeclaration(declarations[0]) && declarations[0].getVariableStatement()?.getDeclarationKind() === VariableDeclarationKind.Const) {
    const initializer=declarations[0].getInitializer();
    if (initializer) return isFetch(initializer,evaluator,seen);
  }
  return false;
}

/** Angular ownership from imports and declaration types works even without node_modules.
 * Common property names such as http are intentionally insufficient evidence. */
function isHttpClient(receiver: Node, model: SemanticModel): boolean {
  const isAngularToken = (node: Node): boolean => {
    for (const imp of node.getSourceFile().getImportDeclarations()) {
      if (imp.getModuleSpecifierValue() !== '@angular/common/http') continue;
      if (imp.getNamedImports().some(spec => spec.getName() === 'HttpClient' && sameSymbol(node, spec.getAliasNode() ?? spec.getNameNode()))) return true;
      if (Node.isPropertyAccessExpression(node) && node.getName() === 'HttpClient' && sameSymbol(node.getExpression(), imp.getNamespaceImport())) return true;
    }
    return false;
  };
  const check = (node: Node | undefined): boolean => {
    if (!node) return false;
    if (Node.isTypeReference(node)) return isAngularToken(node.getTypeName());
    if (Node.isNewExpression(node)) return isAngularToken(node.getExpression());
    if (Node.isCallExpression(node)) {
      const expression=node.getExpression();
      const inject=node.getSourceFile().getImportDeclarations().filter(imp=>imp.getModuleSpecifierValue()==='@angular/core').some(imp=>
        imp.getNamedImports().some(spec=>spec.getName()==='inject' && sameSymbol(expression,spec.getAliasNode() ?? spec.getNameNode())) ||
        Node.isPropertyAccessExpression(expression) && expression.getName()==='inject' && sameSymbol(expression.getExpression(),imp.getNamespaceImport()));
      return inject && node.getArguments().some(isAngularToken);
    }
    return false;
  };
  const declarations = receiver.getSymbol()?.getDeclarations() ?? [];
  for (const declaration of declarations) {
    if ((Node.isParameterDeclaration(declaration) || Node.isPropertyDeclaration(declaration) || Node.isVariableDeclaration(declaration)) &&
      (check(declaration.getTypeNode()) || check(declaration.getInitializer()))) return true;
  }
  // A typed receiver from a dependency declaration is safe only with Angular's symbol provenance.
  return receiver.getType().getSymbol()?.getDeclarations().some(d => d.getSourceFile().getFilePath().replace(/\\/g, '/').includes('/@angular/common/http/')) ?? false;
}
