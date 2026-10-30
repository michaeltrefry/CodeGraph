"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.analyzeHttp = analyzeHttp;
exports.normalizeHttpUrl = normalizeHttpUrl;
const ts_morph_1 = require("ts-morph");
const semanticModel_1 = require("./semanticModel");
const staticEvaluation_1 = require("./staticEvaluation");
const METHODS = new Set(['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS', 'TRACE', 'CONNECT']);
/** Source-derived HTTP summaries. A function name is never evidence of a wrapper: a
 * resolved admitted body must lead to a recognized transport. All caches are per run. */
function analyzeHttp(model, _nodes, edges, diagnostic, serviceName) {
    const evaluator = new staticEvaluation_1.StaticEvaluator(model, diagnostic);
    const coverage = { direct: 0, wrapped: 0, unresolved: 0, ambiguous: 0, bounded: 0, complete: true };
    const cache = new Map();
    const calls = new Map();
    const existingEdges = new Map(edges.filter(e => e.type === 'HTTP_CALLS').map(e => [`${e.sourceQN}|${e.targetQN}`, e]));
    // Persistence has one edge per source/target/type. Accumulate matching evidence before
    // choosing metadata so duplicate normalized paths cannot silently pick the first origin.
    const emitted = new Map();
    const propertiesKey = (properties) => JSON.stringify(Object.entries(properties).sort(([a], [b]) => a.localeCompare(b)));
    const matchingEvidence = (properties) => Object.fromEntries(['http_method', 'origin', 'origin_pattern', 'service_name'].filter(key => properties[key] !== undefined).map(key => [key, properties[key]]));
    let samples = 0, summaries = 0;
    const diag = (message) => { if (samples++ < 60)
        diagnostic(`httpUnresolved: ${message}`); };
    const value = (node, env) => evaluator.evaluate(node, env);
    const envKey = (env) => JSON.stringify([...env].map(([node, v]) => [`${model.relative(node)}:${node.getPos()}`, (0, staticEvaluation_1.staticKey)(v)]).sort(([a], [b]) => a.localeCompare(b)));
    const summarize = (callable, env, active) => {
        if (summaries++ >= 50000) {
            coverage.complete = false;
            coverage.bounded++;
            diag('hard summary budget exceeded');
            return [];
        }
        if (active.has(callable) || active.size >= 16) {
            coverage.bounded++;
            diag(`wrapper bound at ${model.callables.get(callable)}`);
            return [];
        }
        const key = envKey(env);
        const prior = cache.get(callable)?.get(key);
        if (prior)
            return prior;
        const next = new Set(active);
        next.add(callable);
        const qn = model.callables.get(callable) ?? model.fileQN(callable);
        let ownedCalls = calls.get(callable);
        if (!ownedCalls) {
            ownedCalls = callable.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression).filter(call => model.owner(call) === qn);
            calls.set(callable, ownedCalls);
        }
        const facts = [];
        for (const call of ownedCalls) {
            const expr = call.getExpression();
            const args = call.getArguments();
            let terminal;
            if (isFetch(expr, evaluator)) {
                const options = value(args[1], env);
                const methodValue = options == null ? undefined : (0, staticEvaluation_1.isObject)(options)
                    ? options.properties.has('method') ? options.properties.get('method') : options.unknownKeys ? (0, staticEvaluation_1.unknown)('method') : undefined : (0, staticEvaluation_1.unknown)('method');
                const method = methodValue === undefined ? 'GET' : typeof methodValue === 'string' && METHODS.has(methodValue.toUpperCase()) ? methodValue.toUpperCase() : undefined;
                const url = value(args[0], env);
                terminal = { method, url: typeof url === 'string' ? url : undefined, kind: 'fetch', chain: [], reason: !method ? 'unknown method' : typeof url !== 'string' ? 'unknown URL' : undefined };
            }
            else if (ts_morph_1.Node.isPropertyAccessExpression(expr) && isHttpClient(expr.getExpression(), model)) {
                const operation = expr.getName().toUpperCase();
                if (METHODS.has(operation) || operation === 'REQUEST') {
                    const methodValue = operation === 'REQUEST' ? value(args[0], env) : operation;
                    const method = typeof methodValue === 'string' && METHODS.has(methodValue.toUpperCase()) ? methodValue.toUpperCase() : undefined;
                    const url = value(args[operation === 'REQUEST' ? 1 : 0], env);
                    terminal = { method, url: typeof url === 'string' ? url : undefined, kind: 'httpclient', chain: [], reason: !method ? 'unknown method' : undefined };
                }
            }
            if (!terminal && ts_morph_1.Node.isPropertyAccessExpression(expr) && expr.getName() === 'send') {
                const request = args.find(arg => ts_morph_1.Node.isNewExpression(arg)) ?? args.flatMap(arg => arg.getDescendantsOfKind(ts_morph_1.SyntaxKind.NewExpression))[0];
                const target = request && ts_morph_1.Node.isNewExpression(request) ? model.resolve(request.getExpression()) : model.resolve(call.getTypeArguments()[0]);
                if (target && ts_morph_1.Node.isClassDeclaration(target)) {
                    const decorator = target.getDecorator('ApiContract');
                    if (decorator) {
                        const object = value(decorator.getArguments()[0], env);
                        const endpoint = (0, staticEvaluation_1.isObject)(object) ? object.properties.get('endpoint') : undefined;
                        let method = (0, staticEvaluation_1.isObject)(object) ? object.properties.get('httpMethod') : undefined;
                        // Preserve the legacy HttpMethods.post contract form without requiring its dependency.
                        if ((0, staticEvaluation_1.isUnknown)(method)) {
                            const methodNode = decorator.getArguments()[0];
                            const property = methodNode && ts_morph_1.Node.isObjectLiteralExpression(methodNode) ? methodNode.getProperty('httpMethod') : undefined;
                            const initializer = property && ts_morph_1.Node.isPropertyAssignment(property) ? property.getInitializer() : undefined;
                            if (initializer && ts_morph_1.Node.isPropertyAccessExpression(initializer))
                                method = initializer.getName();
                        }
                        const normalized = method === undefined ? 'POST' : typeof method === 'string' && METHODS.has(method.toUpperCase()) ? method.toUpperCase() : undefined;
                        terminal = { method: normalized, url: typeof endpoint === 'string' ? endpoint : undefined, kind: 'apicontract', requestDto: model.declarations.get(target), chain: [] };
                    }
                }
            }
            if (terminal) {
                facts.push(terminal);
                continue;
            }
            const target = model.resolve(expr);
            if (!target || !model.callables.has(target) || !(0, semanticModel_1.isCallable)(target))
                continue;
            const bound = evaluator.bind(target, args.map(arg => value(arg, env)), env);
            for (const fact of summarize(target, bound, next))
                facts.push({ ...fact, chain: [model.callables.get(target), ...fact.chain] });
        }
        // Keep repeated calls to a shared helper from multiplying identical summaries.
        const unique = [...new Map(facts.map(f => [JSON.stringify(f), f])).values()];
        if (summaries < 10000) {
            if (!cache.has(callable))
                cache.set(callable, new Map());
            cache.get(callable).set(key, unique);
        }
        return unique;
    };
    const roots = [...model.callables, ...model.files.map(file => [file, model.fileQN(file)])];
    for (const [callable, qn] of roots) {
        const params = (ts_morph_1.Node.isFunctionDeclaration(callable) || ts_morph_1.Node.isFunctionExpression(callable) || ts_morph_1.Node.isArrowFunction(callable) || ts_morph_1.Node.isMethodDeclaration(callable) || ts_morph_1.Node.isConstructorDeclaration(callable)) ? callable.getParameters() : [];
        const env = evaluator.bind(callable, params.map(p => (0, staticEvaluation_1.unknown)(p.getName())));
        for (const fact of summarize(callable, env, new Set())) {
            const url = fact.url ? normalizeHttpUrl(fact.url) : undefined;
            if (!fact.method || !url) {
                coverage.unresolved++;
                diag(`${qn}: ${fact.reason ?? 'URL has no static endpoint'} (${fact.kind})`);
                continue;
            }
            const targetQN = `route:*:${fact.method}:${url.path}`;
            const key = `${qn}|${targetQN}`;
            const properties = {
                http_method: fact.method, url_pattern: url.path, http_kind: fact.kind,
                call_kind: fact.chain.length ? 'wrapper' : 'direct',
                confidence_band: url.path.includes('{param}') || url.dynamicOrigin ? 'medium' : 'high',
                ...(url.origin ? { origin: url.origin } : {}), ...(url.dynamicOrigin ? { origin_pattern: '{param}' } : {}),
                ...(fact.chain.length ? { wrapper_chain: fact.chain } : {}), ...(fact.requestDto ? { request_dto: fact.requestDto, ...(serviceName ? { service_name: serviceName } : {}) } : {}),
            };
            const evidence = matchingEvidence(properties);
            const evidenceKey = propertiesKey(evidence);
            const representative = propertiesKey(properties);
            let aggregate = emitted.get(key);
            if (!aggregate) {
                const existing = existingEdges.get(key);
                const edge = existing ?? { sourceQN: qn, targetQN, type: 'HTTP_CALLS', properties };
                const priorProperties = existing?.properties ?? properties;
                edge.properties ?? (edge.properties = properties);
                aggregate = { edge, representative: propertiesKey(priorProperties), candidates: new Map(), existing: !!existing };
                if (existing) {
                    const priorCandidates = priorProperties.binding_candidates;
                    for (const candidate of Array.isArray(priorCandidates) ? priorCandidates : [matchingEvidence(priorProperties)]) {
                        if (candidate && typeof candidate === 'object')
                            aggregate.candidates.set(propertiesKey(candidate), candidate);
                    }
                }
                emitted.set(key, aggregate);
            }
            aggregate.candidates.set(evidenceKey, evidence);
            // Other annotations (e.g. wrapper chains) also have a deterministic representative.
            if (representative < aggregate.representative) {
                aggregate.edge.properties = properties;
                aggregate.representative = representative;
            }
        }
    }
    for (const aggregate of emitted.values()) {
        const { edge, candidates } = aggregate;
        const properties = edge.properties;
        if (candidates.size > 1) {
            properties.binding_ambiguous = true;
            properties.binding_candidate_count = candidates.size;
            properties.binding_candidates = [...candidates].sort(([a], [b]) => a.localeCompare(b)).slice(0, 8).map(([, evidence]) => evidence);
            properties.confidence_band = 'low';
            // None of the incompatible target hints is an authoritative scalar claim.
            delete properties.origin;
            delete properties.origin_pattern;
            delete properties.service_name;
            delete properties.request_dto;
            coverage.ambiguous++;
            coverage.unresolved++;
            diag(`${edge.sourceQN}: conflicting HTTP target evidence for ${edge.targetQN} (${candidates.size} candidates)`);
        }
        if (!aggregate.existing) {
            edges.push(edge);
            if (properties.call_kind === 'wrapper')
                coverage.wrapped++;
            else
                coverage.direct++;
        }
    }
    diagnostic(`HTTP coverage: ${JSON.stringify(coverage)}`);
    return coverage;
}
function normalizeHttpUrl(raw) {
    let route = raw.trim(), origin, dynamicOrigin = false;
    const absolute = route.match(/^((?:https?:)?\/\/[^/\s?#]+)(.*)$/i);
    if (absolute) {
        origin = absolute[1];
        route = absolute[2] || '/';
        if (/^[?#]/.test(route))
            route = '/' + route;
        if (origin.includes('{param}')) {
            origin = undefined;
            dynamicOrigin = true;
        }
    }
    else if (route.startsWith('{param}/')) {
        dynamicOrigin = true;
        route = route.slice('{param}'.length);
    }
    if (origin) {
        try {
            const relative = origin.startsWith('//');
            const parsed = new URL(relative ? `http:${origin}` : origin);
            // A scheme-relative origin has no known default port. Preserve an explicit port
            // rather than applying the temporary parser scheme's HTTP default.
            const explicitPort = relative ? origin.match(/:(\d+)$/)?.[1] : undefined;
            origin = relative ? `//${parsed.hostname}${explicitPort ? `:${Number(explicitPort)}` : ''}` : parsed.origin;
        }
        catch {
            return undefined;
        }
    }
    route = route.split(/[?#]/)[0];
    if (!route.startsWith('/') || route.includes('://'))
        return undefined;
    route = route.replace(/\/+/g, '/').replace(/\/$/, '') || '/';
    // A host/base/version alone is not an endpoint. Never turn a fully unknown URL into one.
    const meaningful = route.split('/').filter(p => p && !/^(?:api|admin|v\d+|\{param\})$/i.test(p) && p.replace(/\{param\}/g, '').length > 0);
    if (route.includes('{param}') && !meaningful.length)
        return undefined;
    return { path: route, ...(origin ? { origin } : {}), ...(dynamicOrigin ? { dynamicOrigin } : {}) };
}
function isFetch(node, evaluator, seen = new Set()) {
    if (seen.has(node) || seen.size > 20)
        return false;
    seen.add(node);
    if (evaluator.builtin(node, 'fetch'))
        return true;
    if (ts_morph_1.Node.isPropertyAccessExpression(node) && node.getName() === 'fetch' && ['window', 'globalThis', 'self'].some(name => evaluator.builtin(node.getExpression(), name)))
        return true;
    const declarations = node.getSymbol()?.getDeclarations() ?? [];
    if (declarations.length === 1 && ts_morph_1.Node.isVariableDeclaration(declarations[0]) && declarations[0].getVariableStatement()?.getDeclarationKind() === ts_morph_1.VariableDeclarationKind.Const) {
        const initializer = declarations[0].getInitializer();
        if (initializer)
            return isFetch(initializer, evaluator, seen);
    }
    return false;
}
/** Angular ownership from imports and declaration types works even without node_modules.
 * Common property names such as http are intentionally insufficient evidence. */
function isHttpClient(receiver, model) {
    const isAngularToken = (node) => {
        for (const imp of node.getSourceFile().getImportDeclarations()) {
            if (imp.getModuleSpecifierValue() !== '@angular/common/http')
                continue;
            if (imp.getNamedImports().some(spec => spec.getName() === 'HttpClient' && (0, semanticModel_1.sameSymbol)(node, spec.getAliasNode() ?? spec.getNameNode())))
                return true;
            if (ts_morph_1.Node.isPropertyAccessExpression(node) && node.getName() === 'HttpClient' && (0, semanticModel_1.sameSymbol)(node.getExpression(), imp.getNamespaceImport()))
                return true;
        }
        return false;
    };
    const check = (node) => {
        if (!node)
            return false;
        if (ts_morph_1.Node.isTypeReference(node))
            return isAngularToken(node.getTypeName());
        if (ts_morph_1.Node.isNewExpression(node))
            return isAngularToken(node.getExpression());
        if (ts_morph_1.Node.isCallExpression(node)) {
            const expression = node.getExpression();
            const inject = node.getSourceFile().getImportDeclarations().filter(imp => imp.getModuleSpecifierValue() === '@angular/core').some(imp => imp.getNamedImports().some(spec => spec.getName() === 'inject' && (0, semanticModel_1.sameSymbol)(expression, spec.getAliasNode() ?? spec.getNameNode())) ||
                ts_morph_1.Node.isPropertyAccessExpression(expression) && expression.getName() === 'inject' && (0, semanticModel_1.sameSymbol)(expression.getExpression(), imp.getNamespaceImport()));
            return inject && node.getArguments().some(isAngularToken);
        }
        return false;
    };
    const declarations = receiver.getSymbol()?.getDeclarations() ?? [];
    for (const declaration of declarations) {
        if ((ts_morph_1.Node.isParameterDeclaration(declaration) || ts_morph_1.Node.isPropertyDeclaration(declaration) || ts_morph_1.Node.isVariableDeclaration(declaration)) &&
            (check(declaration.getTypeNode()) || check(declaration.getInitializer())))
            return true;
    }
    // A typed receiver from a dependency declaration is safe only with Angular's symbol provenance.
    return receiver.getType().getSymbol()?.getDeclarations().some(d => d.getSourceFile().getFilePath().replace(/\\/g, '/').includes('/@angular/common/http/')) ?? false;
}
