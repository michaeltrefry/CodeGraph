"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.analyzeReact = analyzeReact;
const ts_morph_1 = require("ts-morph");
const semanticModel_1 = require("./semanticModel");
/** Walk observed render trees with a provider environment; never flatten provider values into
 * a global registry. A provider returned by a component does not provide its own hooks. */
function analyzeReact(model, nodes, edges, diagnostic) {
    const contexts = new Map();
    const providers = new Map();
    const consumers = new Map();
    const renders = [];
    const contextUses = [];
    const providerCounts = new Map();
    const external = new Set(nodes.map(node => node.qualifiedName));
    const renderable = new Map(nodes.filter(node => ['Component', 'Function', 'Class', 'Method'].includes(node.label)).map(node => [node.qualifiedName, node]));
    const callableQNs = new Set(model.callables.values());
    const callableValues = (expression, seen = new Set()) => {
        const result = new Set();
        if (!expression || seen.has(expression))
            return result;
        seen.add(expression);
        const resolved = model.resolve(expression) ?? expression;
        if (ts_morph_1.Node.isCallExpression(resolved) && model.reactApi(resolved) === 'useMemo') {
            const callback = resolved.getArguments()[0];
            if (callback && (ts_morph_1.Node.isArrowFunction(callback) || ts_morph_1.Node.isFunctionExpression(callback))) {
                const body = callback.getBody();
                if (!ts_morph_1.Node.isBlock(body))
                    return callableValues(body, seen);
                for (const returned of body.getDescendantsOfKind(ts_morph_1.SyntaxKind.ReturnStatement)) {
                    if (model.owner(returned) !== model.callables.get(callback))
                        continue;
                    for (const value of callableValues(returned.getExpression(), new Set(seen)))
                        result.add(value);
                }
            }
            return result;
        }
        if (ts_morph_1.Node.isConditionalExpression(resolved)) {
            for (const branch of [resolved.getWhenTrue(), resolved.getWhenFalse()])
                for (const value of callableValues(branch, new Set(seen)))
                    result.add(value);
            diagnostic(`contextValueAlternatives: ${model.owner(resolved)}: conditional provided value`);
            return result;
        }
        if (ts_morph_1.Node.isVariableDeclaration(resolved) || ts_morph_1.Node.isPropertyDeclaration(resolved) || ts_morph_1.Node.isPropertyAssignment(resolved))
            return callableValues(resolved.getInitializer(), seen);
        const qn = model.declarations.get(resolved);
        if (qn && callableQNs.has(qn))
            result.add(qn);
        if (ts_morph_1.Node.isObjectLiteralExpression(resolved)) {
            for (const prop of resolved.getProperties()) {
                if (ts_morph_1.Node.isPropertyAssignment(prop))
                    for (const value of callableValues(prop.getInitializer(), new Set(seen)))
                        result.add(value);
                if (ts_morph_1.Node.isShorthandPropertyAssignment(prop)) {
                    const declaration = prop.getValueSymbol()?.getDeclarations()[0];
                    for (const value of callableValues(declaration, new Set(seen)))
                        result.add(value);
                }
                if (ts_morph_1.Node.isSpreadAssignment(prop))
                    for (const value of callableValues(prop.getExpression(), new Set(seen)))
                        result.add(value);
                if (ts_morph_1.Node.isMethodDeclaration(prop)) {
                    const target = model.declarations.get(prop);
                    if (target)
                        result.add(target);
                }
            }
        }
        return result;
    };
    for (const file of model.files) {
        for (const call of file.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
            const api = model.reactApi(call);
            if (api === 'createContext') {
                const declaration = call.getParent();
                if (!declaration || !ts_morph_1.Node.isVariableDeclaration(declaration))
                    continue;
                const qn = model.declarations.get(declaration);
                contexts.set(qn, { declaration, defaults: callableValues(call.getArguments()[0]) });
                nodes.push({ label: 'Property', name: declaration.getName(), qualifiedName: qn, filePath: model.relative(call),
                    startLine: declaration.getStartLineNumber(), endLine: declaration.getEndLineNumber(), properties: { framework: 'react', context_kind: 'context' } });
                edges.push({ sourceQN: model.fileQN(call), targetQN: qn, type: 'DEFINES' });
            }
            if (api === 'useContext' || api === 'use') {
                const context = model.target(call.getArguments()[0]);
                if (!context) {
                    diagnostic(`contextUnresolved: ${model.owner(call)}: ${call.getText().slice(0, 120)}`);
                    continue;
                }
                contextUses.push({ owner: model.owner(call), context });
            }
        }
    }
    for (const { owner, context } of contextUses) {
        if (!contexts.has(context)) {
            diagnostic(`contextUnresolved: ${owner}: ${context} is not a statically known context`);
            continue;
        }
        if (!consumers.has(owner))
            consumers.set(owner, new Set());
        consumers.get(owner).add(context);
        edges.push({ sourceQN: owner, targetQN: context, type: 'INJECTS', properties: { framework: 'react', context_kind: 'consumer' } });
    }
    const contextTag = (tag) => {
        const expression = ts_morph_1.Node.isPropertyAccessExpression(tag) && tag.getName() === 'Provider' ? tag.getExpression() : tag;
        const target = model.target(expression);
        return target && contexts.has(target) ? target : undefined;
    };
    for (const file of model.files) {
        for (const element of [...file.getDescendantsOfKind(ts_morph_1.SyntaxKind.JsxElement), ...file.getDescendantsOfKind(ts_morph_1.SyntaxKind.JsxSelfClosingElement)]) {
            const opening = ts_morph_1.Node.isJsxElement(element) ? element.getOpeningElement() : element;
            const tag = opening.getTagNameNode();
            const text = tag.getText();
            const owner = model.owner(element);
            const context = contextTag(tag);
            if (context) {
                const value = opening.getAttributes().find(attribute => ts_morph_1.Node.isJsxAttribute(attribute) && attribute.getNameNode().getText() === 'value');
                const init = value && ts_morph_1.Node.isJsxAttribute(value) ? value.getInitializer() : undefined;
                const expression = init && ts_morph_1.Node.isJsxExpression(init) ? init.getExpression() : init;
                const values = callableValues(expression);
                const ordinal = providerCounts.get(owner) ?? 0;
                providerCounts.set(owner, ordinal + 1);
                const qn = `${owner}.$provider${ordinal}.${context.split(/[.#]/).pop()}`;
                const provider = { qn, context, element, values, dynamic: !!expression && values.size === 0 && !['null', 'undefined'].includes(expression.getText()) };
                providers.set(element, provider);
                nodes.push({ label: 'Property', name: `${context.split(/[.#]/).pop()} provider`, qualifiedName: qn, filePath: model.relative(element),
                    startLine: element.getStartLineNumber(), endLine: element.getEndLineNumber(), properties: { framework: 'react', context_kind: 'provider', context_qn: context, value_resolution: provider.dynamic ? 'unresolved' : 'static' } });
                edges.push({ sourceQN: owner, targetQN: qn, type: 'DEFINES' }, { sourceQN: qn, targetQN: context, type: 'REGISTERS', properties: { framework: 'react', context_kind: 'provider' } });
                for (const value of values)
                    edges.push({ sourceQN: qn, targetQN: value, type: 'CALLS', properties: { call_kind: 'context_value' } });
                if (provider.dynamic)
                    diagnostic(`contextValueUnresolved: ${qn}`);
                continue;
            }
            if (/^[a-z]/.test(text) && !text.includes('.'))
                continue;
            if (ts_morph_1.Node.isPropertyAccessExpression(tag) && ['Provider', 'Consumer'].includes(tag.getName()))
                continue;
            let target = model.target(tag);
            if (target && !renderable.has(target))
                target = undefined;
            if (target) {
                const declaration = renderable.get(target);
                if (declaration.label === 'Function' || declaration.label === 'Class') {
                    declaration.label = 'Component';
                    declaration.properties.framework = 'react';
                }
            }
            if (!target) {
                for (const imp of file.getImportDeclarations()) {
                    const named = imp.getNamedImports().find(item => (0, semanticModel_1.sameSymbol)(tag, item.getAliasNode() ?? item.getNameNode()));
                    const namespace = imp.getNamespaceImport()?.getText();
                    const defaultImport = (0, semanticModel_1.sameSymbol)(tag, imp.getDefaultImport());
                    const exportName = named?.getName() ?? (defaultImport ? 'default' : namespace && ts_morph_1.Node.isPropertyAccessExpression(tag) && (0, semanticModel_1.sameSymbol)(tag.getExpression(), imp.getNamespaceImport()) ? tag.getName() : undefined);
                    if (!exportName || model.module(tag, imp.getModuleSpecifierValue()))
                        continue;
                    const resolved = model.imports.find(item => item.filePath === model.relative(file) && item.importedNamespace === imp.getModuleSpecifierValue() && item.classification === 'external_npm_package');
                    if (!resolved?.externalPackageName)
                        continue;
                    target = `npm-symbol:${imp.getModuleSpecifierValue()}#${exportName}`;
                    if (!external.has(target)) {
                        nodes.push({ label: 'ExternalSymbol', name: exportName, qualifiedName: target, filePath: model.relative(file), startLine: 0, endLine: 0,
                            properties: { package_name: resolved.externalPackageName, import_path: imp.getModuleSpecifierValue(), export_name: exportName, framework: 'react' } });
                        external.add(target);
                    }
                }
            }
            if (target) {
                renders.push({ owner, target, element });
                edges.push({ sourceQN: owner, targetQN: target, type: 'RENDERS', properties: { framework: 'react' } });
            }
            else
                diagnostic(`renderUnresolved: ${owner}: ${text}`);
        }
    }
    const calls = new Map();
    for (const edge of edges.filter(edge => edge.type === 'CALLS')) {
        if (!calls.has(edge.sourceQN))
            calls.set(edge.sourceQN, new Set());
        calls.get(edge.sourceQN).add(edge.targetQN);
    }
    const bindings = new Map();
    const visited = new Set();
    const walk = (owner, environment, depth) => {
        if (depth > 100) {
            diagnostic(`contextTraversal: depth limit at ${owner}`);
            return;
        }
        const key = `${owner}|${[...environment].map(([context, provider]) => `${context}=${provider.qn}`).sort().join('|')}`;
        if (visited.has(key))
            return;
        visited.add(key);
        for (const context of consumers.get(owner) ?? []) {
            const provider = environment.get(context);
            if (!bindings.has(owner))
                bindings.set(owner, new Map());
            const byContext = bindings.get(owner);
            if (!byContext.has(context))
                byContext.set(context, new Set());
            byContext.get(context).add(provider?.qn ?? `${context}#default`);
        }
        for (const target of calls.get(owner) ?? [])
            walk(target, environment, depth + 1);
        const owned = renders.filter(render => render.owner === owner);
        const parentOf = (render) => {
            for (const ancestor of render.element.getAncestors()) {
                const parent = owned.find(candidate => candidate.element === ancestor);
                if (parent)
                    return parent;
            }
            return undefined;
        };
        const withProviders = (element, inherited) => {
            const next = new Map(inherited);
            for (const ancestor of element.getAncestors().reverse()) {
                const provider = providers.get(ancestor);
                if (provider)
                    next.set(provider.context, provider);
            }
            return next;
        };
        const visitRender = (render, inherited) => {
            const next = withProviders(render.element, inherited);
            walk(render.target, next, depth + 1);
            const children = owned.filter(child => parentOf(child) === render);
            if (!children.length)
                return;
            const callable = model.byQN.get(render.target);
            // Explicit children slots preserve provider scope across ordinary provider/layout wrappers.
            const slots = callable?.getDescendantsOfKind(ts_morph_1.SyntaxKind.JsxExpression).filter(slot => {
                if (model.owner(slot) !== render.target)
                    return false;
                const expression = slot.getExpression();
                if (!expression)
                    return false;
                if (ts_morph_1.Node.isIdentifier(expression) && expression.getText() === 'children')
                    return expression.getSymbol()?.getDeclarations().some(declaration => ts_morph_1.Node.isBindingElement(declaration) && !!declaration.getFirstAncestorByKind(ts_morph_1.SyntaxKind.Parameter));
                if (ts_morph_1.Node.isPropertyAccessExpression(expression) && expression.getName() === 'children')
                    return expression.getExpression().getSymbol()?.getDeclarations().some(ts_morph_1.Node.isParameterDeclaration);
                return false;
            }) ?? [];
            if (!slots.length) {
                diagnostic(`contextChildrenUnresolved: ${owner} -> ${render.target}`);
                return;
            }
            for (const slot of slots)
                for (const child of children)
                    visitRender(child, withProviders(slot, next));
        };
        for (const render of owned.filter(render => !parentOf(render)))
            visitRender(render, environment);
    };
    const targets = new Set(renders.map(render => render.target));
    for (const node of nodes.filter(node => node.label === 'Component' && !targets.has(node.qualifiedName)))
        walk(node.qualifiedName, new Map(), 0);
    for (const owner of consumers.keys())
        if (!bindings.has(owner))
            diagnostic(`contextScopeUnresolved: ${owner} has no observed render/call path`);
    const providerByQN = new Map([...providers.values()].map(provider => [provider.qn, provider]));
    for (const [owner, byContext] of bindings)
        for (const [context, alternatives] of byContext) {
            const ambiguous = alternatives.size > 1;
            if (ambiguous)
                diagnostic(`contextAlternatives: ${owner}: ${context}: ${[...alternatives].sort().join(', ')}`);
            for (const alternative of alternatives) {
                const properties = { framework: 'react', context_qn: context, context_kind: 'binding', confidence_band: ambiguous ? 'medium' : 'high', candidate: ambiguous };
                const provider = providerByQN.get(alternative);
                if (provider)
                    edges.push({ sourceQN: owner, targetQN: provider.qn, type: 'INJECTS', properties });
                else
                    for (const target of contexts.get(context)?.defaults ?? [])
                        edges.push({ sourceQN: owner, targetQN: target, type: 'CALLS', properties: { ...properties, call_kind: 'context_default' } });
            }
        }
}
