"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.ViteLibraryAnalysisLimitError = exports.DEFAULT_VITE_LIBRARY_WORK_LIMIT = void 0;
exports.viteLibrarySource = viteLibrarySource;
const ts_morph_1 = require("ts-morph");
const path = __importStar(require("path"));
exports.DEFAULT_VITE_LIBRARY_WORK_LIMIT = 500000;
class ViteLibraryAnalysisLimitError extends Error {
    constructor(reason, work, limit) {
        super(`Vite library analysis refused: ${reason} (work=${work}, limit=${limit})`);
        this.reason = reason;
        this.work = work;
        this.limit = limit;
        this.code = 'vite_library_analysis_incomplete';
        this.name = 'ViteLibraryAnalysisLimitError';
    }
}
exports.ViteLibraryAnalysisLimitError = ViteLibraryAnalysisLimitError;
// workspaceResolver creates the admitted-source Map per extraction. Export candidates share
// both this allowance and summary; a new extraction's Map starts independent state.
const runs = new WeakMap();
/** Static admitted Vite library entry/output analysis. Never executes configs/plugins, loads
 * dependencies or reads dist. A work/cancellation refusal throws rather than success-empty. */
function viteLibrarySource(packageRoot, sources, output, diagnostic, control = {}) {
    const limit = control.maxWork ?? exports.DEFAULT_VITE_LIBRARY_WORK_LIMIT;
    if (!Number.isSafeInteger(limit) || limit < 0)
        throw new RangeError('Vite work limit must be a nonnegative safe integer');
    let state = runs.get(sources);
    if (!state) {
        state = { work: 0, summaries: new Map() };
        runs.set(sources, state);
    }
    const run = state;
    const refuse = (reason) => {
        const failure = new ViteLibraryAnalysisLimitError(reason, run.work, limit);
        run.failure = failure;
        diagnostic(`packageExportUnresolved: ${failure.message}`);
        throw failure;
    };
    const charge = (units = 1) => {
        if (run.failure)
            throw run.failure;
        if (control.shouldCancel?.())
            refuse('cancelled');
        if (units > limit - run.work)
            refuse('work_limit');
        run.work += units;
        control.onWork?.(run.work);
    };
    charge(packageRoot.length + 1);
    // Constant admitted lookups, rather than searching every source for every output candidate.
    const configs = [];
    for (const name of ['vite.config.js', 'vite.config.ts']) {
        const key = path.join(packageRoot, name).replace(/\\/g, '/');
        charge(key.length + 1);
        const file = sources.get(key);
        if (file && path.dirname(file.getFilePath()) === packageRoot)
            configs.push(file);
    }
    const nodes = configs.map(file => file.compilerNode);
    charge(nodes.length + 1);
    const cached = run.summaries.get(packageRoot);
    let summary;
    if (cached && cached.nodes.length === nodes.length && cached.nodes.every((node, index) => node === nodes[index]))
        summary = cached.summary;
    else {
        if (configs.length > 1)
            diagnostic('packageExportUnresolved: multiple admitted Vite library configurations');
        summary = configs.length === 1 ? analyze(configs[0], packageRoot, charge) : undefined;
        run.summaries.set(packageRoot, { nodes, summary });
    }
    if (!summary)
        return [];
    charge(output.length + summary.output.length + summary.source.length + 1);
    if (summary.output !== output)
        return [];
    if (!sources.has(summary.source)) {
        diagnostic('packageExportUnresolved: Vite library entry is outside admitted source');
        return [];
    }
    return [summary.source];
}
function analyze(file, packageRoot, charge) {
    const symbols = new Map();
    const symbolOf = (node) => {
        charge();
        if (symbols.has(node))
            return symbols.get(node);
        charge();
        const symbol = node.getSymbol();
        symbols.set(node, symbol);
        return symbol;
    };
    const declarationsOf = (node) => {
        const symbol = symbolOf(node);
        charge((symbol?.compilerSymbol.declarations?.length ?? 0) + 1);
        return symbol?.getDeclarations() ?? [];
    };
    const root = (node) => {
        while (ts_morph_1.Node.isPropertyAccessExpression(node) || ts_morph_1.Node.isElementAccessExpression(node)) {
            charge();
            node = node.getExpression();
        }
        return node;
    };
    const rawText = (node) => { charge(node.getFullWidth() + 1); return node.getText(); };
    const writes = new Set();
    const aliasSources = new Map();
    // Iterative direct-child traversal charges while visiting, not after materializing an AST.
    const pending = [file];
    while (pending.length) {
        charge();
        const expression = pending.pop();
        expression.forEachChild(child => { charge(); pending.push(child); });
        if (ts_morph_1.Node.isVariableDeclaration(expression)) {
            const init = expression.getInitializer();
            const alias = symbolOf(expression)?.compilerSymbol;
            const source = init ? symbolOf(root(init))?.compilerSymbol : undefined;
            if (alias && source) {
                charge();
                if (!aliasSources.has(alias))
                    aliasSources.set(alias, new Set());
                aliasSources.get(alias).add(source);
            }
        }
        let target;
        if (ts_morph_1.Node.isBinaryExpression(expression) && expression.getOperatorToken().getKind() >= ts_morph_1.SyntaxKind.FirstAssignment
            && expression.getOperatorToken().getKind() <= ts_morph_1.SyntaxKind.LastAssignment)
            target = expression.getLeft();
        else if ((ts_morph_1.Node.isPrefixUnaryExpression(expression) || ts_morph_1.Node.isPostfixUnaryExpression(expression))
            && [ts_morph_1.SyntaxKind.PlusPlusToken, ts_morph_1.SyntaxKind.MinusMinusToken].includes(expression.getOperatorToken()))
            target = expression.getOperand();
        else if (ts_morph_1.Node.isDeleteExpression(expression))
            target = expression.getExpression();
        else if (ts_morph_1.Node.isCallExpression(expression)) {
            const callee = expression.getExpression();
            if (ts_morph_1.Node.isPropertyAccessExpression(callee) && callee.getName() === 'assign'
                && ts_morph_1.Node.isIdentifier(callee.getExpression()) && rawText(callee.getExpression()) === 'Object')
                target = expression.getArguments()[0];
        }
        if (target) {
            const symbol = symbolOf(root(target))?.compilerSymbol;
            if (symbol)
                writes.add(symbol);
        }
    }
    // Each written symbol is enqueued once; each distinct alias→storage edge is examined once.
    const queue = [...writes];
    for (let index = 0; index < queue.length; index++) {
        charge();
        for (const source of aliasSources.get(queue[index]) ?? []) {
            charge();
            if (!writes.has(source)) {
                writes.add(source);
                queue.push(source);
            }
        }
    }
    const fold = (node, seen = new Set()) => {
        charge();
        if (!node || seen.has(node) || seen.size > 30)
            return undefined;
        seen.add(node);
        if (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isSatisfiesExpression(node))
            return fold(node.getExpression(), seen);
        if (ts_morph_1.Node.isIdentifier(node)) {
            const declarations = declarationsOf(node);
            if (declarations.length !== 1)
                return undefined;
            const declaration = declarations[0];
            if (ts_morph_1.Node.isVariableDeclaration(declaration) && declaration.getSourceFile() === file
                && declaration.getVariableStatement()?.getDeclarationKind() === 'const' && !writes.has(symbolOf(declaration)?.compilerSymbol))
                return fold(declaration.getInitializer(), seen);
        }
        return node;
    };
    const imported = (expression, modules, member) => {
        charge();
        const matchesModule = (declaration) => {
            const imported = declaration.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration);
            charge(imported.getModuleSpecifier().getFullWidth() + modules.length + 1);
            return modules.includes(imported.getModuleSpecifierValue());
        };
        if (ts_morph_1.Node.isIdentifier(expression))
            return declarationsOf(expression).some(declaration => {
                charge();
                return ts_morph_1.Node.isImportSpecifier(declaration) && declaration.getName() === member && matchesModule(declaration);
            });
        if (ts_morph_1.Node.isPropertyAccessExpression(expression) && expression.getName() === member)
            return declarationsOf(expression.getExpression()).some(declaration => { charge(); return ts_morph_1.Node.isNamespaceImport(declaration) && matchesModule(declaration); });
        return false;
    };
    const text = (expression, seen = new Set()) => {
        charge(seen.size + 1);
        const node = fold(expression, new Set(seen));
        if (!node || seen.has(node) || seen.size > 30)
            return undefined;
        seen.add(node);
        if (ts_morph_1.Node.isStringLiteral(node) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(node)) {
            charge(node.getFullWidth() + 1);
            return node.getLiteralText();
        }
        if (ts_morph_1.Node.isPropertyAccessExpression(node) && node.getName() === 'dirname' && rawText(node.getExpression()) === 'import.meta')
            return packageRoot;
        if (ts_morph_1.Node.isCallExpression(node) && imported(node.getExpression(), ['node:path', 'path'], 'resolve')) {
            const args = node.getArguments().map(arg => { charge(seen.size + 1); return text(arg, new Set(seen)); });
            if (args.every((value) => value !== undefined) && args.length && path.isAbsolute(args[0])) {
                charge(args.reduce((size, arg) => size + arg.length, 1));
                return path.resolve(...args);
            }
        }
        return undefined;
    };
    const property = (expression, name) => {
        const object = fold(expression);
        if (!object || !ts_morph_1.Node.isObjectLiteralExpression(object))
            return undefined;
        let match;
        for (const item of object.getProperties()) {
            charge();
            if (ts_morph_1.Node.isSpreadAssignment(item) || ('getNameNode' in item && ts_morph_1.Node.isComputedPropertyName(item.getNameNode())))
                return undefined;
            if ('getName' in item) {
                charge(item.getNameNode().getFullWidth() + 1);
                if (item.getName() === name) {
                    if (match)
                        return undefined;
                    match = item;
                }
            }
        }
        return match && ts_morph_1.Node.isPropertyAssignment(match) ? match.getInitializer()
            : match && ts_morph_1.Node.isShorthandPropertyAssignment(match) ? match.getNameNode() : match && ts_morph_1.Node.isMethodDeclaration(match) ? match : undefined;
    };
    const assignments = file.getExportAssignments();
    charge(assignments.length + 1);
    if (assignments.length !== 1)
        return undefined;
    let configuration = fold(assignments[0].getExpression());
    if (configuration && ts_morph_1.Node.isCallExpression(configuration) && imported(configuration.getExpression(), ['vite'], 'defineConfig'))
        configuration = fold(configuration.getArguments()[0]);
    if (!configuration || !ts_morph_1.Node.isObjectLiteralExpression(configuration))
        return undefined;
    const build = property(configuration, 'build');
    const library = property(build, 'lib');
    const entry = text(property(library, 'entry'));
    const fileName = fold(property(library, 'fileName'));
    if (!entry || !fileName || !(ts_morph_1.Node.isArrowFunction(fileName) || ts_morph_1.Node.isFunctionExpression(fileName) || ts_morph_1.Node.isMethodDeclaration(fileName)))
        return undefined;
    const body = fileName.getBody();
    const statements = body && ts_morph_1.Node.isBlock(body) && body.compilerNode.statements.length === 1 ? body.getStatements() : [];
    charge(statements.length + 1);
    const result = body && ts_morph_1.Node.isBlock(body) ? statements[0] && ts_morph_1.Node.isReturnStatement(statements[0]) ? statements[0].getExpression() : undefined : body;
    const name = text(result);
    if (!name)
        return undefined;
    charge(name.length + 1);
    if (!/\.[cm]?js$/.test(name) || path.basename(name) !== name)
        return undefined;
    const configRootExpression = property(configuration, 'root');
    const configuredRoot = configRootExpression ? text(configRootExpression) : packageRoot;
    const outExpression = property(build, 'outDir');
    const outDir = outExpression ? text(outExpression) : 'dist';
    if (configuredRoot === undefined || outDir === undefined)
        return undefined;
    charge(configuredRoot.length + outDir.length + entry.length + name.length + packageRoot.length + 1);
    const buildRoot = path.resolve(packageRoot, configuredRoot);
    return { output: path.resolve(buildRoot, outDir, name), source: path.resolve(buildRoot, entry) };
}
