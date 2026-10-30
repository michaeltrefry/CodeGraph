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
    const roots = [...model.callables, ...model.files.map(file => [file, model.fileQN(file)])];
    const owners = new Map(roots.map(([node, qn]) => [qn, node]));
    const targets = new Map();
    const transports = new Map();
    const contracts = new Map();
    const dependents = new Map();
    const reachable = new Set();
    let callCount = 0;
    // Classify each admitted call once. Only bodies that can reach a real transport are
    // wrappers; traversing every UI/helper call creates exponential irrelevant work.
    for (const file of model.files)
        for (const call of file.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
            const owner = owners.get(model.owner(call));
            if (!owner)
                continue;
            callCount++;
            if (!calls.has(owner))
                calls.set(owner, []);
            calls.get(owner).push(call);
            const expression = call.getExpression();
            if (isFetch(expression, evaluator))
                transports.set(call, 'fetch');
            else if (ts_morph_1.Node.isPropertyAccessExpression(expression)) {
                const operation = expression.getName().toUpperCase();
                // Check the operation before asking the type checker about a receiver.
                if ((METHODS.has(operation) || operation === 'REQUEST') && isHttpClient(expression.getExpression(), model))
                    transports.set(call, 'httpclient');
                else if (operation === 'SEND') {
                    const args = call.getArguments();
                    const request = args.find(ts_morph_1.Node.isNewExpression) ?? args.flatMap(arg => arg.getDescendantsOfKind(ts_morph_1.SyntaxKind.NewExpression))[0];
                    const contract = request && ts_morph_1.Node.isNewExpression(request) ? model.resolve(request.getExpression()) : model.resolve(call.getTypeArguments()[0]);
                    if (contract && ts_morph_1.Node.isClassDeclaration(contract) && contract.getDecorator('ApiContract')) {
                        transports.set(call, 'apicontract');
                        contracts.set(call, contract);
                    }
                }
            }
            if (transports.has(call)) {
                reachable.add(owner);
                continue;
            }
            const target = model.resolve(expression);
            if (target && model.callables.has(target) && (0, semanticModel_1.isCallable)(target)) {
                targets.set(call, target);
                if (!dependents.has(target))
                    dependents.set(target, new Set());
                dependents.get(target).add(owner);
            }
        }
    const queue = [...reachable];
    for (let index = 0; index < queue.length; index++)
        for (const caller of dependents.get(queue[index]) ?? []) {
            if (!reachable.has(caller)) {
                reachable.add(caller);
                queue.push(caller);
            }
        }
    // A callee can see its own parameters and lexical captures, never arbitrary caller
    // parameters. Keeping unrelated caller bindings in keys defeats summary reuse.
    const scopeBindings = new Map();
    const parameters = (node) => ts_morph_1.Node.isFunctionDeclaration(node) || ts_morph_1.Node.isFunctionExpression(node) || ts_morph_1.Node.isArrowFunction(node) || ts_morph_1.Node.isMethodDeclaration(node) || ts_morph_1.Node.isConstructorDeclaration(node) ? node.getParameters() : [];
    const scopedEnvironment = (callable, env) => {
        let bindings = scopeBindings.get(callable);
        if (!bindings) {
            bindings = new Set();
            for (const owner of [callable, ...callable.getAncestors().filter(node => model.callables.has(node))])
                for (const parameter of parameters(owner)) {
                    bindings.add(parameter);
                    for (const binding of parameter.getDescendantsOfKind(ts_morph_1.SyntaxKind.BindingElement))
                        bindings.add(binding);
                }
            scopeBindings.set(callable, bindings);
        }
        // Root analysis of a nested callable has no invocation for its outer function.
        // Seed captures as unknown so an outer parameter's default is not treated as proof.
        return new Map([...bindings].map(declaration => [declaration, env.has(declaration) ? env.get(declaration) : (0, staticEvaluation_1.unknown)()]));
    };
    const existingEdges = new Map(edges.filter(e => e.type === 'HTTP_CALLS').map(e => [`${e.sourceQN}|${e.targetQN}`, e]));
    // Persistence has one edge per source/target/type. Accumulate matching evidence before
    // choosing metadata so duplicate normalized paths cannot silently pick the first origin.
    const emitted = new Map();
    const propertiesKey = (properties) => JSON.stringify(Object.entries(properties).sort(([a], [b]) => a.localeCompare(b)));
    const matchingEvidence = (properties) => Object.fromEntries(['http_method', 'origin', 'origin_pattern', 'service_name'].filter(key => properties[key] !== undefined).map(key => [key, properties[key]]));
    let samples = 0, summaries = 0, cacheHits = 0;
    const diag = (message) => { if (samples++ < 60)
        diagnostic(`httpUnresolved: ${message}`); };
    const value = (node, env) => evaluator.evaluate(node, env);
    const envKey = (env) => JSON.stringify([...env].map(([node, v]) => [`${model.relative(node)}:${node.getPos()}`, (0, staticEvaluation_1.staticKey)(v)]).sort(([a], [b]) => a.localeCompare(b)));
    const summarize = (callable, env, active) => {
        if (!reachable.has(callable))
            return [];
        if (active.has(callable) || active.size >= 16) {
            coverage.bounded++;
            diag(`wrapper bound at ${model.callables.get(callable)}`);
            return [];
        }
        env = scopedEnvironment(callable, env);
        const key = envKey(env);
        const prior = cache.get(callable)?.get(key);
        if (prior) {
            cacheHits++;
            return prior;
        }
        // Count actual uncached relevant expansions, not cache hits or unrelated roots.
        if (summaries++ >= 50000) {
            coverage.complete = false;
            coverage.bounded++;
            diag('hard summary budget exceeded');
            return [];
        }
        const next = new Set(active);
        next.add(callable);
        const facts = [];
        for (const call of calls.get(callable) ?? []) {
            const expr = call.getExpression();
            const args = call.getArguments();
            let terminal;
            if (transports.get(call) === 'fetch') {
                const options = value(args[1], env);
                const methodValue = options == null ? undefined : (0, staticEvaluation_1.isObject)(options)
                    ? options.properties.has('method') ? options.properties.get('method') : options.unknownKeys ? (0, staticEvaluation_1.unknown)('method') : undefined : (0, staticEvaluation_1.unknown)('method');
                const method = methodValue === undefined ? 'GET' : typeof methodValue === 'string' && METHODS.has(methodValue.toUpperCase()) ? methodValue.toUpperCase() : undefined;
                const url = value(args[0], env);
                terminal = { method, url: typeof url === 'string' ? url : undefined, kind: 'fetch', chain: [], reason: !method ? 'unknown method' : typeof url !== 'string' ? 'unknown URL' : undefined };
            }
            else if (transports.get(call) === 'httpclient' && ts_morph_1.Node.isPropertyAccessExpression(expr)) {
                const operation = expr.getName().toUpperCase();
                if (METHODS.has(operation) || operation === 'REQUEST') {
                    const methodValue = operation === 'REQUEST' ? value(args[0], env) : operation;
                    const method = typeof methodValue === 'string' && METHODS.has(methodValue.toUpperCase()) ? methodValue.toUpperCase() : undefined;
                    const url = value(args[operation === 'REQUEST' ? 1 : 0], env);
                    terminal = { method, url: typeof url === 'string' ? url : undefined, kind: 'httpclient', chain: [], reason: !method ? 'unknown method' : undefined };
                }
            }
            if (transports.get(call) === 'apicontract') {
                const target = contracts.get(call);
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
            const target = targets.get(call);
            if (!target || !reachable.has(target))
                continue;
            const bound = evaluator.bind(target, args.map(arg => value(arg, env)), env);
            for (const fact of summarize(target, bound, next))
                facts.push({ ...fact, chain: [model.callables.get(target), ...fact.chain] });
        }
        // Keep repeated calls to a shared helper from multiplying identical summaries.
        const uniqueFacts = new Map();
        for (const fact of facts) {
            const { chain, ...identity } = fact;
            const key = JSON.stringify(identity);
            const prior = uniqueFacts.get(key);
            // CALLS already retains every wrapper path. Equivalent transport facts need one
            // deterministic witness, otherwise a diamond call graph multiplies chain strings.
            if (!prior || chain.length < prior.chain.length || chain.length === prior.chain.length && JSON.stringify(chain) < JSON.stringify(prior.chain))
                uniqueFacts.set(key, fact);
        }
        const unique = [...uniqueFacts.values()];
        if (!cache.has(callable))
            cache.set(callable, new Map());
        cache.get(callable).set(key, unique);
        return unique;
    };
    for (const [callable, qn] of roots) {
        if (!reachable.has(callable))
            continue;
        const params = parameters(callable);
        const env = evaluator.bind(callable, params.map(p => (0, staticEvaluation_1.unknown)(p.getName())));
        for (const fact of summarize(callable, env, new Set())) {
            // ApiContract endpoints are root-relative route declarations even in the legacy
            // unslashed form. Browser fetch('PlaceBid') remains relative to navigation context.
            const endpoint = fact.url?.trim();
            const contractEndpoint = fact.kind === 'apicontract' && endpoint && !endpoint.startsWith('/') && !/^[a-z][\w+.-]*:/i.test(endpoint) ? `/${endpoint}` : endpoint;
            const url = contractEndpoint ? normalizeHttpUrl(contractEndpoint) : undefined;
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
    diagnostic(`HTTP analysis: ${JSON.stringify({ calls: callCount, relevant_roots: reachable.size, summaries, cache_hits: cacheHits })}`);
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
