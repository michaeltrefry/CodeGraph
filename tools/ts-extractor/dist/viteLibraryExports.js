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
exports.viteLibrarySource = viteLibrarySource;
const ts_morph_1 = require("ts-morph");
const path = __importStar(require("path"));
/** The observed ui library's Vite entry/output contract, read from admitted ASTs only. This is
 * deliberately static: no config/plugin execution, dependency loading or dist reads. */
function viteLibrarySource(packageRoot, sources, output, diagnostic) {
    const configs = [...sources.values()].filter(file => path.dirname(file.getFilePath()) === packageRoot
        && /^vite\.config\.[jt]s$/.test(path.basename(file.getFilePath())));
    if (!configs.length)
        return [];
    if (configs.length !== 1) {
        diagnostic('packageExportUnresolved: multiple admitted Vite library configurations');
        return [];
    }
    const file = configs[0];
    const root = (node) => ts_morph_1.Node.isPropertyAccessExpression(node) || ts_morph_1.Node.isElementAccessExpression(node)
        ? root(node.getExpression()) : node;
    const writes = new Set();
    for (const expression of file.getDescendants()) {
        let target;
        if (ts_morph_1.Node.isBinaryExpression(expression) && expression.getOperatorToken().getKind() >= ts_morph_1.SyntaxKind.FirstAssignment
            && expression.getOperatorToken().getKind() <= ts_morph_1.SyntaxKind.LastAssignment)
            target = expression.getLeft();
        else if ((ts_morph_1.Node.isPrefixUnaryExpression(expression) || ts_morph_1.Node.isPostfixUnaryExpression(expression))
            && [ts_morph_1.SyntaxKind.PlusPlusToken, ts_morph_1.SyntaxKind.MinusMinusToken].includes(expression.getOperatorToken()))
            target = expression.getOperand();
        else if (ts_morph_1.Node.isDeleteExpression(expression))
            target = expression.getExpression();
        else if (ts_morph_1.Node.isCallExpression(expression) && expression.getExpression().getText() === 'Object.assign')
            target = expression.getArguments()[0];
        if (target) {
            const symbol = root(target).getSymbol()?.compilerSymbol;
            if (symbol)
                writes.add(symbol);
        }
    }
    const variables = file.getDescendantsOfKind(ts_morph_1.SyntaxKind.VariableDeclaration);
    for (let pass = 0; pass <= variables.length; pass++) {
        let changed = false;
        for (const variable of variables) {
            const init = variable.getInitializer();
            const alias = variable.getSymbol()?.compilerSymbol;
            const source = init && root(init).getSymbol()?.compilerSymbol;
            if (alias && source && writes.has(alias) && !writes.has(source)) {
                writes.add(source);
                changed = true;
            }
        }
        if (!changed)
            break;
    }
    const fold = (node, seen = new Set()) => {
        if (!node || seen.has(node) || seen.size > 30)
            return undefined;
        seen.add(node);
        if (ts_morph_1.Node.isParenthesizedExpression(node) || ts_morph_1.Node.isAsExpression(node) || ts_morph_1.Node.isSatisfiesExpression(node))
            return fold(node.getExpression(), seen);
        if (ts_morph_1.Node.isIdentifier(node)) {
            const declarations = node.getSymbol()?.getDeclarations() ?? [];
            if (declarations.length !== 1)
                return undefined;
            const declaration = declarations[0];
            if (ts_morph_1.Node.isVariableDeclaration(declaration) && declaration.getSourceFile() === file
                && declaration.getVariableStatement()?.getDeclarationKind() === 'const' && !writes.has(declaration.getSymbol()?.compilerSymbol))
                return fold(declaration.getInitializer(), seen);
        }
        return node;
    };
    const imported = (expression, module, member) => {
        if (ts_morph_1.Node.isIdentifier(expression))
            return (expression.getSymbol()?.getDeclarations() ?? []).some(declaration => ts_morph_1.Node.isImportSpecifier(declaration) && declaration.getName() === member
                && module.includes(declaration.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration).getModuleSpecifierValue()));
        if (ts_morph_1.Node.isPropertyAccessExpression(expression) && expression.getName() === member)
            return (expression.getExpression().getSymbol()?.getDeclarations() ?? []).some(declaration => ts_morph_1.Node.isNamespaceImport(declaration)
                && module.includes(declaration.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration).getModuleSpecifierValue()));
        return false;
    };
    const text = (expression, seen = new Set()) => {
        const node = fold(expression, new Set(seen));
        if (!node || seen.has(node) || seen.size > 30)
            return undefined;
        seen.add(node);
        if (ts_morph_1.Node.isStringLiteral(node) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(node))
            return node.getLiteralText();
        if (ts_morph_1.Node.isPropertyAccessExpression(node) && node.getExpression().getText() === 'import.meta' && node.getName() === 'dirname')
            return packageRoot;
        if (ts_morph_1.Node.isCallExpression(node) && imported(node.getExpression(), ['node:path', 'path'], 'resolve')) {
            const args = node.getArguments().map(arg => text(arg, new Set(seen)));
            if (args.every((value) => value !== undefined) && args.length && path.isAbsolute(args[0]))
                return path.resolve(...args);
        }
        return undefined;
    };
    const property = (expression, name) => {
        const object = fold(expression);
        if (!object || !ts_morph_1.Node.isObjectLiteralExpression(object))
            return undefined;
        if (object.getProperties().some(item => ts_morph_1.Node.isSpreadAssignment(item)
            || ('getNameNode' in item && ts_morph_1.Node.isComputedPropertyName(item.getNameNode()))))
            return undefined;
        const matches = object.getProperties().filter(item => 'getName' in item && item.getName() === name);
        if (matches.length !== 1)
            return undefined;
        const match = matches[0];
        return ts_morph_1.Node.isPropertyAssignment(match) ? match.getInitializer()
            : ts_morph_1.Node.isShorthandPropertyAssignment(match) ? match.getNameNode() : ts_morph_1.Node.isMethodDeclaration(match) ? match : undefined;
    };
    const assignments = file.getExportAssignments();
    if (assignments.length !== 1)
        return [];
    let configuration = fold(assignments[0].getExpression());
    if (configuration && ts_morph_1.Node.isCallExpression(configuration) && imported(configuration.getExpression(), ['vite'], 'defineConfig'))
        configuration = fold(configuration.getArguments()[0]);
    if (!configuration || !ts_morph_1.Node.isObjectLiteralExpression(configuration))
        return [];
    const build = property(configuration, 'build');
    const library = property(build, 'lib');
    const entry = text(property(library, 'entry'));
    const fileName = fold(property(library, 'fileName'));
    if (!entry || !fileName || !(ts_morph_1.Node.isArrowFunction(fileName) || ts_morph_1.Node.isFunctionExpression(fileName) || ts_morph_1.Node.isMethodDeclaration(fileName)))
        return [];
    const body = fileName.getBody();
    const result = body && ts_morph_1.Node.isBlock(body) ? body.getStatements().length === 1 && ts_morph_1.Node.isReturnStatement(body.getStatements()[0])
        ? body.getStatements()[0].asKindOrThrow(ts_morph_1.SyntaxKind.ReturnStatement).getExpression() : undefined : body;
    const name = text(result);
    if (!name || !/\.[cm]?js$/.test(name) || path.basename(name) !== name)
        return [];
    const configRootExpression = property(configuration, 'root');
    const configuredRoot = configRootExpression ? text(configRootExpression) : packageRoot;
    const outExpression = property(build, 'outDir');
    const outDir = outExpression ? text(outExpression) : 'dist';
    if (configuredRoot === undefined || outDir === undefined)
        return [];
    const buildRoot = path.resolve(packageRoot, configuredRoot);
    if (path.resolve(buildRoot, outDir, name) !== output)
        return [];
    const source = path.resolve(buildRoot, entry);
    if (!sources.has(source)) {
        diagnostic('packageExportUnresolved: Vite library entry is outside admitted source');
        return [];
    }
    return [source];
}
