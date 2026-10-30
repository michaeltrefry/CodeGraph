"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.analyzeReact = analyzeReact;
const ts_morph_1 = require("ts-morph");
const semanticModel_1 = require("./semanticModel");
const reactLazyWrappers_1 = require("./reactLazyWrappers");
/** Walk observed render trees with a provider environment; never flatten provider values into
 * a global registry. A provider returned by a component does not provide its own hooks. */
function analyzeReact(model, nodes, edges, diagnostic) {
    const screenMetadata = declaredScreenMetadata(model);
    const lazyTargets = (0, reactLazyWrappers_1.lazyWrapperTargets)(model, diagnostic);
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
                edges.push({ sourceQN: owner, targetQN: target, type: 'RENDERS', properties: { framework: 'react', ...screenMetadata(element) } });
            }
            else
                diagnostic(`renderUnresolved: ${owner}: ${text}`);
            for (const lazy of lazyTargets(tag)) {
                if (lazy.qualifiedName === target || !renderable.has(lazy.qualifiedName))
                    continue;
                renders.push({ owner, target: lazy.qualifiedName, element, uncertain: true });
                edges.push({ sourceQN: owner, targetQN: lazy.qualifiedName, type: 'RENDERS', properties: { framework: 'react', ...screenMetadata(element),
                        lazy_wrapper: true, lazy_target_file: lazy.filePath, lazy_wrapper_chain: lazy.wrapperChain,
                        confidence_band: 'medium', candidate: true, lazy_evidence: 'source_initializer_to_react_lazy' } });
            }
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
            // Source-initializer lazy candidates preserve impact, but do not prove the provider
            // environment inside a factory wrapper. Keep its consumers explicitly unresolved.
            if (render.uncertain)
                return;
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
    canonicalizeScreenSelections(edges);
}
/** Persistence identifies an edge by source/target/type. Factory instances can share a wrapper
 * declaration, so keep each declared selection canonically instead of allowing last-write wins. */
function canonicalizeScreenSelections(edges) {
    const groups = new Map();
    for (const edge of edges) {
        if (edge.type !== 'RENDERS' || edge.properties?.framework !== 'react')
            continue;
        const key = JSON.stringify([edge.sourceQN, edge.targetQN]);
        if (!groups.has(key))
            groups.set(key, []);
        groups.get(key).push(edge);
    }
    const discard = new Set();
    for (const group of groups.values()) {
        if (group.length < 2 || !group.some(edge => edge.properties?.screen_key !== undefined))
            continue;
        const selections = new Map();
        for (const edge of group) {
            if (edge.properties?.screen_key === undefined)
                continue;
            const selection = Object.fromEntries(Object.entries(edge.properties).filter(([key]) => key.startsWith('screen_')));
            selections.set(JSON.stringify(selection), selection);
        }
        const canonical = [...selections].sort(([left], [right]) => left.localeCompare(right)).map(([, value]) => value);
        const first = group[0];
        first.properties = { ...first.properties };
        for (const key of Object.keys(first.properties))
            if (key.startsWith('screen_'))
                delete first.properties[key];
        if (canonical.length === 1)
            Object.assign(first.properties, canonical[0]);
        else
            first.properties.screen_selections = canonical;
        for (const duplicate of group.slice(1))
            discard.add(duplicate);
    }
    if (discard.size)
        edges.splice(0, edges.length, ...edges.filter(edge => !discard.has(edge)));
}
/** Preserve declared screen selection, not arbitrary conditional visibility. A selector must
 * first be used by an actual navigation landmark's aria-current=page declaration. Only positive
 * literal guards using that same symbol (or immutable boolean aliases) become screen metadata.
 * Keep-alive wrappers can pass the declared selection through their explicit active prop.
 */
function declaredScreenMetadata(model) {
    const selectors = new Map();
    const unwrap = (node) => {
        while (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isNonNullExpression(node))
            node = node.getExpression();
        return node;
    };
    const declarationOf = (node) => {
        if (!ts_morph_1.Node.isIdentifier(node))
            return undefined;
        const declarations = node.getSymbol()?.getDeclarations() ?? [];
        return declarations.length === 1 ? declarations[0] : undefined;
    };
    const literal = (node) => ts_morph_1.Node.isStringLiteral(node) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(node) ? node.getLiteralText() : undefined;
    for (const file of model.files)
        for (const attribute of file.getDescendantsOfKind(ts_morph_1.SyntaxKind.JsxAttribute)) {
            if (attribute.getNameNode().getText() !== 'aria-current')
                continue;
            const navigation = attribute.getAncestors().some(ancestor => ts_morph_1.Node.isJsxElement(ancestor) && ancestor.getOpeningElement().getTagNameNode().getText() === 'nav');
            if (!navigation)
                continue;
            const initializer = attribute.getInitializer();
            const expression = initializer && ts_morph_1.Node.isJsxExpression(initializer) ? initializer.getExpression() : undefined;
            if (!expression)
                continue;
            const conditional = unwrap(expression);
            if (!ts_morph_1.Node.isConditionalExpression(conditional) || literal(unwrap(conditional.getWhenTrue())) !== 'page')
                continue;
            const condition = unwrap(conditional.getCondition());
            if (!ts_morph_1.Node.isBinaryExpression(condition) || condition.getOperatorToken().getKind() !== ts_morph_1.SyntaxKind.EqualsEqualsEqualsToken)
                continue;
            for (const operand of [condition.getLeft(), condition.getRight()]) {
                const reference = unwrap(operand);
                const declaration = declarationOf(reference);
                if (declaration)
                    selectors.set(declaration, { name: reference.getText(), owner: model.owner(declaration) });
            }
        }
    const selection = (expression, seen = new Set()) => {
        const node = unwrap(expression);
        if (seen.has(node) || seen.size > 8)
            return undefined;
        seen.add(node);
        if (ts_morph_1.Node.isIdentifier(node)) {
            const declaration = declarationOf(node);
            if (!declaration || !ts_morph_1.Node.isVariableDeclaration(declaration) || declaration.getParentIfKind(ts_morph_1.SyntaxKind.VariableDeclarationList)?.getDeclarationKind() !== 'const')
                return undefined;
            const initializer = declaration.getInitializer();
            return initializer ? selection(initializer, seen) : undefined;
        }
        if (!ts_morph_1.Node.isBinaryExpression(node))
            return undefined;
        if (node.getOperatorToken().getKind() === ts_morph_1.SyntaxKind.AmpersandAmpersandToken) {
            const left = selection(node.getLeft(), new Set(seen));
            const right = selection(node.getRight(), new Set(seen));
            if (left && right && (left.owner !== right.owner || left.selector !== right.selector || left.key !== right.key))
                return undefined;
            return left ?? right;
        }
        if (node.getOperatorToken().getKind() !== ts_morph_1.SyntaxKind.EqualsEqualsEqualsToken)
            return undefined;
        for (const [reference, value] of [[node.getLeft(), node.getRight()], [node.getRight(), node.getLeft()]]) {
            const declaration = declarationOf(unwrap(reference));
            const key = literal(unwrap(value));
            const selector = declaration ? selectors.get(declaration) : undefined;
            if (selector && key !== undefined)
                return { key, selector: selector.name, owner: selector.owner };
        }
        return undefined;
    };
    return element => {
        const ancestors = element.getAncestors();
        const contained = new Set([element, ...ancestors]);
        const metadata = (value, evidence) => ({ screen_key: value.key, screen_selector: value.selector,
            screen_selector_owner: value.owner, screen_evidence: evidence, screen_selection_evidence: 'navigation_aria_current_page' });
        for (const ancestor of [element, ...ancestors]) {
            if (model.callables.has(ancestor))
                break;
            if (ts_morph_1.Node.isJsxElement(ancestor) || ts_morph_1.Node.isJsxSelfClosingElement(ancestor)) {
                const opening = ts_morph_1.Node.isJsxElement(ancestor) ? ancestor.getOpeningElement() : ancestor;
                const active = opening.getAttributes().find(attribute => ts_morph_1.Node.isJsxAttribute(attribute) && attribute.getNameNode().getText() === 'active');
                const initializer = active && ts_morph_1.Node.isJsxAttribute(active) ? active.getInitializer() : undefined;
                const expression = initializer && ts_morph_1.Node.isJsxExpression(initializer) ? initializer.getExpression() : undefined;
                const value = expression ? selection(expression) : undefined;
                if (value)
                    return metadata(value, 'active_prop');
                // A surrounding component owns its children rendering. Do not infer its nested JSX to
                // be another screen from an outer guard unless it explicitly passes an active selector.
                if (ancestor !== element && /^[A-Z]/.test(opening.getTagNameNode().getText()))
                    break;
            }
            if (ts_morph_1.Node.isConditionalExpression(ancestor) && contained.has(ancestor.getWhenTrue())) {
                const value = selection(ancestor.getCondition());
                if (value)
                    return metadata(value, 'conditional_guard');
            }
            if (ts_morph_1.Node.isBinaryExpression(ancestor) && ancestor.getOperatorToken().getKind() === ts_morph_1.SyntaxKind.AmpersandAmpersandToken && contained.has(ancestor.getRight())) {
                const value = selection(ancestor.getLeft());
                if (value)
                    return metadata(value, 'conditional_guard');
            }
        }
        return {};
    };
}
