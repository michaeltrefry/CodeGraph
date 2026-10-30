import { Node, SourceFile, SyntaxKind } from 'ts-morph';
import * as path from 'path';

/** The observed ui library's Vite entry/output contract, read from admitted ASTs only. This is
 * deliberately static: no config/plugin execution, dependency loading or dist reads. */
export function viteLibrarySource(packageRoot: string, sources: Map<string, SourceFile>, output: string,
  diagnostic: (message: string) => void): string[] {
  const configs = [...sources.values()].filter(file => path.dirname(file.getFilePath()) === packageRoot
    && /^vite\.config\.[jt]s$/.test(path.basename(file.getFilePath())));
  if (!configs.length) return [];
  if (configs.length !== 1) { diagnostic('packageExportUnresolved: multiple admitted Vite library configurations'); return []; }
  const file = configs[0];
  const root = (node: Node): Node => Node.isPropertyAccessExpression(node) || Node.isElementAccessExpression(node)
    ? root(node.getExpression()) : node;
  const writes = new Set<unknown>();
  for (const expression of file.getDescendants()) {
    let target: Node | undefined;
    if (Node.isBinaryExpression(expression) && expression.getOperatorToken().getKind() >= SyntaxKind.FirstAssignment
      && expression.getOperatorToken().getKind() <= SyntaxKind.LastAssignment) target = expression.getLeft();
    else if ((Node.isPrefixUnaryExpression(expression) || Node.isPostfixUnaryExpression(expression))
      && [SyntaxKind.PlusPlusToken, SyntaxKind.MinusMinusToken].includes(expression.getOperatorToken())) target = expression.getOperand();
    else if (Node.isDeleteExpression(expression)) target = expression.getExpression();
    else if (Node.isCallExpression(expression) && expression.getExpression().getText() === 'Object.assign') target = expression.getArguments()[0];
    if (target) { const symbol = root(target).getSymbol()?.compilerSymbol; if (symbol) writes.add(symbol); }
  }
  const variables = file.getDescendantsOfKind(SyntaxKind.VariableDeclaration);
  for (let pass = 0; pass <= variables.length; pass++) {
    let changed = false;
    for (const variable of variables) {
      const init = variable.getInitializer(); const alias = variable.getSymbol()?.compilerSymbol;
      const source = init && root(init).getSymbol()?.compilerSymbol;
      if (alias && source && writes.has(alias) && !writes.has(source)) { writes.add(source); changed = true; }
    }
    if (!changed) break;
  }
  const fold = (node: Node | undefined, seen = new Set<Node>()): Node | undefined => {
    if (!node || seen.has(node) || seen.size > 30) return undefined;
    seen.add(node);
    if (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isSatisfiesExpression(node)) return fold(node.getExpression(), seen);
    if (Node.isIdentifier(node)) {
      const declarations = node.getSymbol()?.getDeclarations() ?? [];
      if (declarations.length !== 1) return undefined;
      const declaration = declarations[0];
      if (Node.isVariableDeclaration(declaration) && declaration.getSourceFile() === file
        && declaration.getVariableStatement()?.getDeclarationKind() === 'const' && !writes.has(declaration.getSymbol()?.compilerSymbol)) return fold(declaration.getInitializer(), seen);
    }
    return node;
  };
  const imported = (expression: Node, module: string[], member: string): boolean => {
    if (Node.isIdentifier(expression)) return (expression.getSymbol()?.getDeclarations() ?? []).some(declaration =>
      Node.isImportSpecifier(declaration) && declaration.getName() === member
      && module.includes(declaration.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!.getModuleSpecifierValue()));
    if (Node.isPropertyAccessExpression(expression) && expression.getName() === member)
      return (expression.getExpression().getSymbol()?.getDeclarations() ?? []).some(declaration => Node.isNamespaceImport(declaration)
        && module.includes(declaration.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!.getModuleSpecifierValue()));
    return false;
  };
  const text = (expression: Node | undefined, seen = new Set<Node>()): string | undefined => {
    const node = fold(expression, new Set(seen));
    if (!node || seen.has(node) || seen.size > 30) return undefined;
    seen.add(node);
    if (Node.isStringLiteral(node) || Node.isNoSubstitutionTemplateLiteral(node)) return node.getLiteralText();
    if (Node.isPropertyAccessExpression(node) && node.getExpression().getText() === 'import.meta' && node.getName() === 'dirname') return packageRoot;
    if (Node.isCallExpression(node) && imported(node.getExpression(), ['node:path', 'path'], 'resolve')) {
      const args = node.getArguments().map(arg => text(arg, new Set(seen)));
      if (args.every((value): value is string => value !== undefined) && args.length && path.isAbsolute(args[0])) return path.resolve(...args);
    }
    return undefined;
  };
  const property = (expression: Node | undefined, name: string): Node | undefined => {
    const object = fold(expression);
    if (!object || !Node.isObjectLiteralExpression(object)) return undefined;
    if (object.getProperties().some(item => Node.isSpreadAssignment(item)
      || ('getNameNode' in item && Node.isComputedPropertyName((item as { getNameNode(): Node }).getNameNode())))) return undefined;
    const matches = object.getProperties().filter(item => 'getName' in item && (item as { getName(): string }).getName() === name);
    if (matches.length !== 1) return undefined;
    const match = matches[0];
    return Node.isPropertyAssignment(match) ? match.getInitializer()
      : Node.isShorthandPropertyAssignment(match) ? match.getNameNode() : Node.isMethodDeclaration(match) ? match : undefined;
  };
  const assignments = file.getExportAssignments();
  if (assignments.length !== 1) return [];
  let configuration = fold(assignments[0].getExpression());
  if (configuration && Node.isCallExpression(configuration) && imported(configuration.getExpression(), ['vite'], 'defineConfig'))
    configuration = fold(configuration.getArguments()[0]);
  if (!configuration || !Node.isObjectLiteralExpression(configuration)) return [];
  const build = property(configuration, 'build');
  const library = property(build, 'lib');
  const entry = text(property(library, 'entry'));
  const fileName = fold(property(library, 'fileName'));
  if (!entry || !fileName || !(Node.isArrowFunction(fileName) || Node.isFunctionExpression(fileName) || Node.isMethodDeclaration(fileName))) return [];
  const body = fileName.getBody();
  const result = body && Node.isBlock(body) ? body.getStatements().length === 1 && Node.isReturnStatement(body.getStatements()[0])
    ? body.getStatements()[0].asKindOrThrow(SyntaxKind.ReturnStatement).getExpression() : undefined : body;
  const name = text(result);
  if (!name || !/\.[cm]?js$/.test(name) || path.basename(name) !== name) return [];
  const configRootExpression = property(configuration, 'root');
  const configuredRoot = configRootExpression ? text(configRootExpression) : packageRoot;
  const outExpression = property(build, 'outDir');
  const outDir = outExpression ? text(outExpression) : 'dist';
  if (configuredRoot === undefined || outDir === undefined) return [];
  const buildRoot = path.resolve(packageRoot, configuredRoot);
  if (path.resolve(buildRoot, outDir, name) !== output) return [];
  const source = path.resolve(buildRoot, entry);
  if (!sources.has(source)) { diagnostic('packageExportUnresolved: Vite library entry is outside admitted source'); return []; }
  return [source];
}
