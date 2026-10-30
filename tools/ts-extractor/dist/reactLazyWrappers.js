"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.lazyWrapperTargets = lazyWrapperTargets;
const ts_morph_1 = require("ts-morph");
const semanticModel_1 = require("./semanticModel");
/** Source-only initializer flow for a returned React wrapper. This preserves possible lazy
 * targets through local factories, captures and React's declared useState initializer. It never
 * executes application code, evaluates setter updates or treats a helper name as React evidence.
 * A target requires an actual react-imported lazy call and its resolved module/export payload.
 */
function lazyWrapperTargets(model, diagnostic) {
    const cache = new Map();
    const mutated = new Set();
    const mutationFiles = new Set();
    let expansions = 0;
    const isMutated = (node) => {
        if (!(ts_morph_1.Node.isVariableDeclaration(node) || ts_morph_1.Node.isParameterDeclaration(node) || ts_morph_1.Node.isFunctionDeclaration(node)))
            return false;
        const file = node.getSourceFile();
        if (mutationFiles.has(file))
            return mutated.has(node);
        mutationFiles.add(file);
        for (const binary of file.getDescendantsOfKind(ts_morph_1.SyntaxKind.BinaryExpression)) {
            const operator = binary.getOperatorToken().getKind();
            if (operator >= ts_morph_1.SyntaxKind.FirstAssignment && operator <= ts_morph_1.SyntaxKind.LastAssignment && ts_morph_1.Node.isIdentifier(binary.getLeft()))
                for (const declaration of binary.getLeft().getSymbol()?.getDeclarations() ?? [])
                    mutated.add(declaration);
        }
        for (const operation of [...file.getDescendantsOfKind(ts_morph_1.SyntaxKind.PrefixUnaryExpression), ...file.getDescendantsOfKind(ts_morph_1.SyntaxKind.PostfixUnaryExpression)]) {
            if (![ts_morph_1.SyntaxKind.PlusPlusToken, ts_morph_1.SyntaxKind.MinusMinusToken].includes(operation.getOperatorToken()))
                continue;
            for (const declaration of operation.getOperand().getSymbol()?.getDeclarations() ?? [])
                mutated.add(declaration);
        }
        return mutated.has(node);
    };
    const declarations = (node) => node.getSymbol()?.getDeclarations().filter(declaration => model.byPath.has(model.relative(declaration))) ?? [];
    const component = (value) => typeof value === 'object' && value && (value.kind === 'function' || value.kind === 'component') ? value.node : undefined;
    const signature = (value, seen = new Set()) => {
        if (!value || typeof value !== 'object')
            return JSON.stringify(value) ?? '?';
        if (seen.has(value))
            return '?cycle';
        const next = new Set(seen);
        next.add(value);
        if (value.kind === 'component')
            return model.declarations.get(value.node) ?? '?';
        if (value.kind === 'function')
            return JSON.stringify([model.declarations.get(value.node), [...value.environment].map(([node, bound]) => [`${model.relative(node)}:${node.getPos()}`, signature(bound, next)])]);
        if (value.kind === 'lazy')
            return `lazy:${model.declarations.get(value.target)}`;
        if (value.kind === 'module')
            return `module:${model.relative(value.file)}`;
        if (value.kind === 'array')
            return JSON.stringify(value.elements.map(value => signature(value, next)));
        return JSON.stringify([...value.properties].map(([key, value]) => [key, signature(value, next)]));
    };
    const invoke = (callable, args, active) => {
        if (!callable || typeof callable !== 'object' || callable.kind !== 'function')
            return undefined;
        const declaration = callable.node;
        if (active.has(declaration))
            return undefined;
        if (active.size >= 16) {
            diagnostic(`reactLazyTraversal: wrapper depth limit at ${model.relative(declaration)}`);
            return undefined;
        }
        const next = new Set(active);
        next.add(declaration);
        const environment = new Map(callable.environment);
        if (!(ts_morph_1.Node.isFunctionDeclaration(declaration) || ts_morph_1.Node.isFunctionExpression(declaration) || ts_morph_1.Node.isArrowFunction(declaration) || ts_morph_1.Node.isMethodDeclaration(declaration)))
            return undefined;
        declaration.getParameters().forEach((parameter, index) => environment.set(parameter, args[index]));
        const body = declaration.getBody();
        const returned = !body ? [] : ts_morph_1.Node.isBlock(body) ? body.getDescendantsOfKind(ts_morph_1.SyntaxKind.ReturnStatement)
            .filter(statement => model.owner(statement) === model.callables.get(declaration)).map(statement => statement.getExpression()) : [body];
        const values = returned.map(expression => evaluate(expression, environment, next, new Set()));
        if (values.length && values.every(value => signature(value) === signature(values[0]))) {
            const value = values[0];
            return value && typeof value === 'object' && value.kind === 'function' ? { ...value, factoryReturn: true } : value;
        }
        if (values.some(value => value !== undefined))
            diagnostic(`reactLazyUnresolved: differing returned values in ${model.callables.get(declaration)}`);
        return undefined;
    };
    const evaluate = (node, environment, active, seen) => {
        if (!node || seen.has(node))
            return undefined;
        if (seen.size > 60) {
            diagnostic(`reactLazyTraversal: initializer alias depth limit in ${model.relative(node)}`);
            return undefined;
        }
        if (++expansions > 100000) {
            if (expansions === 100001)
                diagnostic('reactLazyTraversal: initializer expansion budget exceeded');
            return undefined;
        }
        if (isMutated(node))
            return undefined;
        if (environment.has(node))
            return environment.get(node);
        const next = new Set(seen);
        next.add(node);
        const ev = (expression) => evaluate(expression, environment, active, next);
        if (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isNonNullExpression(node) || ts_morph_1.Node.isAwaitExpression(node))
            return ev(node.getExpression());
        if (ts_morph_1.Node.isStringLiteral(node) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(node))
            return node.getLiteralText();
        if (ts_morph_1.Node.isNumericLiteral(node))
            return node.getLiteralValue();
        if (node.getKind() === ts_morph_1.SyntaxKind.TrueKeyword)
            return true;
        if (node.getKind() === ts_morph_1.SyntaxKind.FalseKeyword)
            return false;
        if ((0, semanticModel_1.isCallable)(node))
            return { kind: 'function', node, environment: new Map(environment) };
        if (ts_morph_1.Node.isClassDeclaration(node))
            return { kind: 'component', node };
        if (ts_morph_1.Node.isIdentifier(node)) {
            const declared = declarations(node);
            if (declared.length === 1)
                return ev(declared[0]);
            return undefined;
        }
        if (ts_morph_1.Node.isImportSpecifier(node) || ts_morph_1.Node.isImportClause(node)) {
            const declaration = node.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration);
            const file = model.module(node, declaration.getModuleSpecifierValue());
            return file ? ev(model.exported(file, ts_morph_1.Node.isImportSpecifier(node) ? node.getName() : 'default')) : undefined;
        }
        if (ts_morph_1.Node.isParameterDeclaration(node))
            return undefined;
        if (ts_morph_1.Node.isVariableDeclaration(node)) {
            if (node.getParentIfKind(ts_morph_1.SyntaxKind.VariableDeclarationList)?.getDeclarationKind() !== 'const')
                return undefined;
            return ev(node.getInitializer());
        }
        if (ts_morph_1.Node.isBindingElement(node)) {
            const pattern = node.getParent();
            const value = ev(pattern?.getParent());
            if (ts_morph_1.Node.isArrayBindingPattern(pattern) && value && typeof value === 'object' && value.kind === 'array')
                return value.elements[pattern.getElements().indexOf(node)];
            if (ts_morph_1.Node.isObjectBindingPattern(pattern) && value && typeof value === 'object' && value.kind === 'object')
                return value.properties.get(node.getPropertyNameNode()?.getText() ?? node.getName());
            return undefined;
        }
        if (ts_morph_1.Node.isObjectLiteralExpression(node)) {
            const properties = new Map();
            for (const property of node.getProperties()) {
                if (ts_morph_1.Node.isPropertyAssignment(property))
                    properties.set(property.getName(), ev(property.getInitializer()));
                else if (ts_morph_1.Node.isShorthandPropertyAssignment(property))
                    properties.set(property.getName(), ev(property.getValueSymbol()?.getDeclarations()[0]));
                else if (ts_morph_1.Node.isSpreadAssignment(property))
                    return undefined;
            }
            return { kind: 'object', properties };
        }
        if (ts_morph_1.Node.isPropertyAccessExpression(node) || ts_morph_1.Node.isElementAccessExpression(node)) {
            const value = ev(node.getExpression());
            const key = ts_morph_1.Node.isPropertyAccessExpression(node) ? node.getName() : ev(node.getArgumentExpression());
            if (typeof key !== 'string' || !value || typeof value !== 'object')
                return undefined;
            if (value.kind === 'object')
                return value.properties.get(key);
            if (value.kind === 'module')
                return ev(model.exported(value.file, key));
            return undefined;
        }
        if (ts_morph_1.Node.isConditionalExpression(node)) {
            const condition = ev(node.getCondition());
            if (typeof condition === 'boolean')
                return ev(condition ? node.getWhenTrue() : node.getWhenFalse());
            const yes = ev(node.getWhenTrue());
            const no = ev(node.getWhenFalse());
            if (signature(yes) === signature(no))
                return yes;
            diagnostic(`reactLazyUnresolved: conditional initializer in ${model.relative(node)}`);
            return undefined;
        }
        if (ts_morph_1.Node.isCallExpression(node)) {
            const expression = node.getExpression();
            if (expression.getKind() === ts_morph_1.SyntaxKind.ImportKeyword) {
                const specifier = ev(node.getArguments()[0]);
                const file = typeof specifier === 'string' ? model.module(node, specifier) : undefined;
                return file ? { kind: 'module', file } : undefined;
            }
            const api = model.reactApi(node);
            if (api === 'lazy') {
                const payload = invoke(ev(node.getArguments()[0]), [], active);
                const target = payload && typeof payload === 'object' && payload.kind === 'module' ? model.exported(payload.file, 'default') :
                    payload && typeof payload === 'object' && payload.kind === 'object' ? component(payload.properties.get('default')) : undefined;
                if (!target || !model.declarations.has(target)) {
                    diagnostic(`reactLazyUnresolved: loader does not select one admitted export in ${model.relative(node)}`);
                    return undefined;
                }
                return { kind: 'lazy', target, chain: [...active].map(node => model.callables.get(node)).filter((qn) => !!qn) };
            }
            if (api === 'useState') {
                const initial = ev(node.getArguments()[0]);
                return { kind: 'array', elements: [initial && typeof initial === 'object' && initial.kind === 'function' ? invoke(initial, [], active) : initial, undefined] };
            }
            if (ts_morph_1.Node.isPropertyAccessExpression(expression) && expression.getName() === 'then') {
                const source = ev(expression.getExpression());
                if (source && typeof source === 'object' && source.kind === 'module')
                    return invoke(ev(node.getArguments()[0]), [source], active);
                return undefined;
            }
            return invoke(ev(expression), node.getArguments().map(ev), active);
        }
        return undefined;
    };
    const candidates = new Map();
    const provenLazyFactories = new Set();
    const sourceCalls = new Map();
    const hasLazySource = (callable, seen = new Set()) => {
        if (provenLazyFactories.has(callable))
            return true;
        if (seen.has(callable))
            return false;
        if (seen.size >= 64) {
            diagnostic(`reactLazyUnresolved: source factory discovery bound at ${model.relative(callable)}`);
            return false;
        }
        const next = new Set(seen);
        next.add(callable);
        let targets = sourceCalls.get(callable);
        if (!targets) {
            targets = [];
            for (const call of callable.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
                if (model.reactApi(call) === 'lazy') {
                    provenLazyFactories.add(callable);
                    return true;
                }
                const target = model.resolve(call.getExpression());
                if (target && model.callables.has(target))
                    targets.push(target);
            }
            sourceCalls.set(callable, targets);
        }
        if (targets.some(target => hasLazySource(target, next))) {
            provenLazyFactories.add(callable);
            return true;
        }
        return false;
    };
    const isFactoryInitializer = (declaration, seen = new Set()) => {
        if (seen.has(declaration))
            return false;
        if (seen.size > 16) {
            diagnostic(`reactLazyTraversal: factory alias depth limit in ${model.relative(declaration)}`);
            return false;
        }
        const cached = candidates.get(declaration);
        if (cached !== undefined)
            return cached;
        seen.add(declaration);
        let result = false;
        if (ts_morph_1.Node.isVariableDeclaration(declaration)) {
            let initializer = declaration.getInitializer();
            while (initializer && (ts_morph_1.Node.isParenthesizedExpression(initializer) || ts_morph_1.Node.isAsExpression(initializer) || ts_morph_1.Node.isNonNullExpression(initializer)))
                initializer = initializer.getExpression();
            if (initializer && ts_morph_1.Node.isCallExpression(initializer)) {
                const api = model.reactApi(initializer);
                const target = api ? undefined : model.resolve(initializer.getExpression());
                result = api === 'lazy' || !!target && model.callables.has(target) && hasLazySource(target);
            }
            else if (initializer && ts_morph_1.Node.isIdentifier(initializer)) {
                const declared = declarations(initializer);
                result = declared.length === 1 && isFactoryInitializer(declared[0], seen);
            }
            else if (initializer && ts_morph_1.Node.isConditionalExpression(initializer)) {
                result = [initializer.getWhenTrue(), initializer.getWhenFalse()].some(branch => {
                    if (ts_morph_1.Node.isIdentifier(branch)) {
                        const declared = declarations(branch);
                        return declared.length === 1 && isFactoryInitializer(declared[0], new Set(seen));
                    }
                    if (!ts_morph_1.Node.isCallExpression(branch))
                        return false;
                    const api = model.reactApi(branch);
                    const target = api ? undefined : model.resolve(branch.getExpression());
                    return api === 'lazy' || !!target && model.callables.has(target) && hasLazySource(target);
                });
            }
        }
        else if (ts_morph_1.Node.isImportSpecifier(declaration) || ts_morph_1.Node.isImportClause(declaration)) {
            const imported = declaration.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration);
            const file = model.module(declaration, imported.getModuleSpecifierValue());
            const target = file ? model.exported(file, ts_morph_1.Node.isImportSpecifier(declaration) ? declaration.getName() : 'default') : undefined;
            result = !!target && isFactoryInitializer(target, seen);
        }
        candidates.set(declaration, result);
        return result;
    };
    return tag => {
        const declared = declarations(tag);
        const key = declared.length === 1 ? declared[0] : tag;
        const cached = cache.get(key);
        if (cached)
            return cached;
        // Existing semantic extraction already handles plain functions/classes/JSX aliases. Only
        // source factory initializers need closure-aware lazy traversal; avoid expanding icon maps
        // and other unrelated object-valued UI declarations in large applications.
        if (declared.length !== 1 || !isFactoryInitializer(key)) {
            cache.set(key, []);
            return [];
        }
        const value = evaluate(tag, new Map(), new Set(), new Set());
        const targets = new Map();
        const add = (value) => {
            if (!value || typeof value !== 'object' || value.kind !== 'lazy')
                return;
            const qualifiedName = model.declarations.get(value.target);
            if (!qualifiedName)
                return;
            targets.set(qualifiedName, { qualifiedName, filePath: model.relative(value.target), wrapperChain: value.chain });
        };
        add(value);
        if (value && typeof value === 'object' && value.kind === 'function' && value.factoryReturn) {
            for (const jsx of [...value.node.getDescendantsOfKind(ts_morph_1.SyntaxKind.JsxSelfClosingElement), ...value.node.getDescendantsOfKind(ts_morph_1.SyntaxKind.JsxOpeningElement)]) {
                if (model.owner(jsx) !== model.callables.get(value.node))
                    continue;
                add(evaluate(jsx.getTagNameNode(), value.environment, new Set(), new Set()));
            }
        }
        const result = [...targets.values()];
        cache.set(key, result);
        return result;
    };
}
