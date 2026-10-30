"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.analyzeTauri = analyzeTauri;
const ts_morph_1 = require("ts-morph");
const semanticModel_1 = require("./semanticModel");
const staticEvaluation_1 = require("./staticEvaluation");
/** Tauri is recognized by import provenance, never a function's spelling. Wrapper summaries
 * substitute declaration-scoped arguments; their browser fallbacks stay ordinary call edges.
 * Native app ownership and registration are resolved by the pipeline, after Rust extraction. */
function analyzeTauri(model, nodes, edges, diagnostic) {
    const evaluator = new staticEvaluation_1.StaticEvaluator(model, diagnostic);
    const memo = new Map();
    let expanded = 0;
    const importedApi = (module, name) => /^@tauri-apps\/api\/(?:core|tauri)$/.test(module) && name === 'invoke' ? 'invoke' :
        module === '@tauri-apps/api/event' && ['listen', 'once', 'emit', 'emitTo'].includes(name) ? name : undefined;
    const apiFor = (node, seen = new Set()) => {
        if (seen.has(node) || seen.size > 40)
            return undefined;
        seen.add(node);
        if (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isNonNullExpression(node))
            return apiFor(node.getExpression(), seen);
        for (const declaration of node.getSymbol()?.getDeclarations() ?? []) {
            if (ts_morph_1.Node.isImportSpecifier(declaration)) {
                const module = declaration.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration).getModuleSpecifierValue();
                const name = declaration.getName();
                const direct = importedApi(module, name);
                if (direct)
                    return direct;
                const target = model.module(node, module);
                if (target) {
                    const exported = exportedApi(target, name, new Set(seen));
                    if (exported)
                        return exported;
                }
            }
            if (ts_morph_1.Node.isVariableDeclaration(declaration) && declaration.getInitializer())
                return apiFor(declaration.getInitializer(), seen);
        }
        if (ts_morph_1.Node.isPropertyAccessExpression(node)) {
            const reference = node.getExpression();
            const namespace = reference.getSymbol()?.getDeclarations().find(ts_morph_1.Node.isNamespaceImport);
            if (namespace) {
                const module = namespace.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration).getModuleSpecifierValue();
                const direct = importedApi(module, node.getName());
                if (direct)
                    return direct;
                const target = model.module(node, module);
                if (target)
                    return exportedApi(target, node.getName(), new Set(seen));
            }
        }
        const resolved = model.resolve(node);
        return resolved && resolved !== node ? apiFor(resolved, seen) : undefined;
    };
    const exportedApi = (file, name, seen) => {
        if (seen.has(file) || seen.size > 40)
            return undefined;
        seen.add(file);
        const candidates = new Set();
        const declaration = model.exported(file, name);
        if (declaration)
            return apiFor(declaration, new Set(seen));
        const explicit = file.getExportDeclarations().filter(exp => exp.getNamedExports().some(item => (item.getAliasNode()?.getText() ?? item.getName()) === name));
        for (const exp of explicit.length ? explicit : file.getExportDeclarations()) {
            const specifier = exp.getModuleSpecifierValue();
            if (!specifier)
                continue;
            const names = exp.getNamedExports();
            const item = names.find(item => (item.getAliasNode()?.getText() ?? item.getName()) === name);
            if (names.length && !item)
                continue;
            const imported = item?.getName() ?? name;
            const direct = importedApi(specifier, imported);
            if (direct)
                candidates.add(direct);
            const target = model.module(file, specifier);
            if (target) {
                const api = exportedApi(target, imported, new Set(seen));
                if (api)
                    candidates.add(api);
            }
        }
        return candidates.size === 1 ? [...candidates][0] : undefined;
    };
    const ownedCalls = (owner) => owner.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)
        .filter(call => model.owner(call) === model.callables.get(owner));
    const callsByOwner = new Map([...model.callables.keys()].map(owner => [owner, ownedCalls(owner)]));
    const reachable = new Set();
    const dependents = new Map();
    for (const [owner, calls] of callsByOwner)
        for (const call of calls) {
            if (apiFor(call.getExpression()))
                reachable.add(owner);
            const target = model.resolve(call.getExpression());
            if (target && model.callables.has(target)) {
                if (!dependents.has(target))
                    dependents.set(target, new Set());
                dependents.get(target).add(owner);
            }
        }
    const queue = [...reachable];
    for (let cursor = 0; cursor < queue.length; cursor++)
        for (const caller of dependents.get(queue[cursor]) ?? [])
            if (!reachable.has(caller)) {
                reachable.add(caller);
                queue.push(caller);
            }
    function operations(call, env, active) {
        if (!ts_morph_1.Node.isCallExpression(call))
            return [];
        const api = apiFor(call.getExpression());
        if (api) {
            const nameIndex = api === 'emitTo' ? 1 : 0;
            const value = evaluator.evaluate(call.getArguments()[nameIndex], env);
            // Partial strings are useful for HTTP, but cannot identify a native command/event.
            const name = typeof value === 'string' && value.length > 0 && !value.includes('{param}') ? value : undefined;
            const callback = ['listen', 'once'].includes(api) ? model.target(call.getArguments()[nameIndex + 1]) : undefined;
            return [{ api, name, site: call, callback, chain: [] }];
        }
        const target = model.resolve(call.getExpression());
        if (!target || !(0, semanticModel_1.isCallable)(target) || !reachable.has(target) || active.has(target))
            return [];
        const values = call.getArguments().map(argument => evaluator.evaluate(argument, env));
        const key = `${model.callables.get(target)}:${values.map(staticEvaluation_1.staticKey).join('|')}`;
        const cached = memo.get(key);
        if (cached)
            return cached;
        if (active.size >= 16 || ++expanded > 20000) {
            diagnostic(`tauriTraversal: bounded wrapper expansion at ${model.relative(call)}:${call.getStartLineNumber()}`);
            return [];
        }
        const next = evaluator.bind(target, values, env);
        const chain = new Set(active);
        chain.add(target);
        const result = (callsByOwner.get(target) ?? []).flatMap(inner => operations(inner, next, chain))
            .map(operation => ({ ...operation, chain: [model.callables.get(target), ...operation.chain] }));
        memo.set(key, result);
        return result;
    }
    const keys = new Set();
    let commandCount = 0, eventCount = 0, unresolved = 0;
    for (const [callable, qn] of model.callables) {
        if (!reachable.has(callable))
            continue;
        const env = evaluator.bind(callable, 'getParameters' in callable
            ? callable.getParameters().map(parameter => (0, staticEvaluation_1.unknown)(parameter.getName())) : []);
        for (const call of callsByOwner.get(callable) ?? [])
            for (const operation of operations(call, env, new Set([callable]))) {
                const key = `${qn}:${operation.api}:${operation.name ?? `dynamic:${call.getStart()}`}:${operation.callback ?? ''}`;
                if (keys.has(key))
                    continue;
                keys.add(key);
                const command = operation.api === 'invoke';
                const qualifiedName = `tauri-reference:${encodeURIComponent(model.repository)}:${encodeURIComponent(qn)}:${operation.api}:${encodeURIComponent(operation.name ?? `dynamic-${call.getStart()}`)}`;
                const filePath = model.relative(callable);
                nodes.push({ label: command ? 'Function' : 'Event', name: operation.name ?? '<dynamic>', qualifiedName, filePath,
                    startLine: call.getStartLineNumber(), endLine: call.getEndLineNumber(), properties: {
                        kind: command ? 'tauri_command_reference' : 'tauri_event_reference', transport: 'tauri',
                        tauri_api: operation.api, tauri_name: operation.name ?? '', tauri_resolution: operation.name ? 'unbound' : 'dynamic',
                        tauri_caller: qn, ...(operation.callback ? { tauri_callback: operation.callback } : {}),
                        wrapper_chain: operation.chain, extraction_source_file: filePath,
                        // Static dependency evidence remains possible when desktop guards have web fallbacks.
                        dependency_kind: 'possible_desktop', confidence: operation.name ? 'high' : 'low',
                    } });
                edges.push({ sourceQN: qn, targetQN: qualifiedName,
                    type: command ? 'CALLS' : ['listen', 'once'].includes(operation.api) ? 'SUBSCRIBES' : 'PUBLISHES',
                    properties: { transport: 'tauri', dependency_kind: 'possible_desktop' } });
                if (operation.callback)
                    edges.push({ sourceQN: qualifiedName, targetQN: operation.callback, type: 'HANDLES', properties: { transport: 'tauri' } });
                if (!operation.name) {
                    unresolved++;
                    diagnostic(`tauriUnresolved: dynamic ${operation.api} at ${filePath}:${call.getStartLineNumber()}`);
                }
                else if (command)
                    commandCount++;
                else
                    eventCount++;
            }
    }
    diagnostic(`tauriCoverage: ${JSON.stringify({ commands: commandCount, events: eventCount, unresolved })}`);
}
