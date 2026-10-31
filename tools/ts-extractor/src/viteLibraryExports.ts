import { Node, SourceFile, SyntaxKind } from 'ts-morph';
import * as path from 'path';

export const DEFAULT_VITE_LIBRARY_WORK_LIMIT = 500_000;
export interface ViteLibraryAnalysisControl {
  maxWork?: number;
  /** Synchronous polling only. Timers/AbortSignals cannot preempt this function; work is bounded. */
  shouldCancel?: () => boolean;
  onWork?: (work: number) => void;
}
export class ViteLibraryAnalysisLimitError extends Error {
  readonly code = 'vite_library_analysis_incomplete';
  constructor(readonly reason: 'work_limit' | 'cancelled', readonly work: number, readonly limit: number) {
    super(`Vite library analysis refused: ${reason} (work=${work}, limit=${limit})`);
    this.name = 'ViteLibraryAnalysisLimitError';
  }
}
interface Summary { output: string; source: string; }
interface CachedSummary { nodes: object[]; summary?: Summary; }
interface Run { work: number; failure?: ViteLibraryAnalysisLimitError; summaries: Map<string, CachedSummary>; }
// workspaceResolver creates the admitted-source Map per extraction. Export candidates share
// both this allowance and summary; a new extraction's Map starts independent state.
const runs = new WeakMap<Map<string, SourceFile>, Run>();
type CompilerSymbol = NonNullable<ReturnType<Node['getSymbol']>>['compilerSymbol'];

/** Static admitted Vite library entry/output analysis. Never executes configs/plugins, loads
 * dependencies or reads dist. A work/cancellation refusal throws rather than success-empty. */
export function viteLibrarySource(packageRoot: string, sources: Map<string, SourceFile>, output: string,
  diagnostic: (message: string) => void, control: ViteLibraryAnalysisControl = {}): string[] {
  const limit = control.maxWork ?? DEFAULT_VITE_LIBRARY_WORK_LIMIT;
  if (!Number.isSafeInteger(limit) || limit < 0) throw new RangeError('Vite work limit must be a nonnegative safe integer');
  let state = runs.get(sources);
  if (!state) { state = { work: 0, summaries: new Map() }; runs.set(sources, state); }
  const run = state;
  const refuse = (reason: 'work_limit' | 'cancelled'): never => {
    const failure = new ViteLibraryAnalysisLimitError(reason, run.work, limit);
    run.failure = failure; diagnostic(`packageExportUnresolved: ${failure.message}`); throw failure;
  };
  const charge = (units = 1): void => {
    if (run.failure) throw run.failure;
    if (control.shouldCancel?.()) refuse('cancelled');
    if (units > limit - run.work) refuse('work_limit');
    run.work += units; control.onWork?.(run.work);
  };
  charge(packageRoot.length + 1);
  // Constant admitted lookups, rather than searching every source for every output candidate.
  const configs: SourceFile[] = [];
  for (const name of ['vite.config.js', 'vite.config.ts']) {
    const key = path.join(packageRoot, name).replace(/\\/g, '/'); charge(key.length + 1);
    const file = sources.get(key);
    if (file && path.dirname(file.getFilePath()) === packageRoot) configs.push(file);
  }
  const nodes = configs.map(file => file.compilerNode); charge(nodes.length + 1);
  const cached = run.summaries.get(packageRoot);
  let summary: Summary | undefined;
  if (cached && cached.nodes.length === nodes.length && cached.nodes.every((node, index) => node === nodes[index])) summary = cached.summary;
  else {
    if (configs.length > 1) diagnostic('packageExportUnresolved: multiple admitted Vite library configurations');
    summary = configs.length === 1 ? analyze(configs[0], packageRoot, charge) : undefined;
    run.summaries.set(packageRoot, { nodes, summary });
  }
  if (!summary) return [];
  charge(output.length + summary.output.length + summary.source.length + 1);
  if (summary.output !== output) return [];
  if (!sources.has(summary.source)) { diagnostic('packageExportUnresolved: Vite library entry is outside admitted source'); return []; }
  return [summary.source];
}

function analyze(file: SourceFile, packageRoot: string, charge: (units?: number) => void): Summary | undefined {
  const symbols = new Map<Node, ReturnType<Node['getSymbol']>>();
  const symbolOf = (node: Node): ReturnType<Node['getSymbol']> => {
    charge(); if (symbols.has(node)) return symbols.get(node);
    charge(); const symbol = node.getSymbol(); symbols.set(node, symbol); return symbol;
  };
  const declarationsOf = (node: Node): Node[] => {
    const symbol = symbolOf(node); charge((symbol?.compilerSymbol.declarations?.length ?? 0) + 1);
    return symbol?.getDeclarations() ?? [];
  };
  const root = (node: Node): Node => {
    while (Node.isPropertyAccessExpression(node) || Node.isElementAccessExpression(node)) { charge(); node = node.getExpression(); }
    return node;
  };
  const rawText = (node: Node): string => { charge(node.getFullWidth() + 1); return node.getText(); };
  const writes = new Set<CompilerSymbol>();
  const aliasSources = new Map<CompilerSymbol, Set<CompilerSymbol>>();
  // Iterative direct-child traversal charges while visiting, not after materializing an AST.
  const pending: Node[] = [file];
  while (pending.length) {
    charge(); const expression = pending.pop()!;
    expression.forEachChild(child => { charge(); pending.push(child); });
    if (Node.isVariableDeclaration(expression)) {
      const init = expression.getInitializer(); const alias = symbolOf(expression)?.compilerSymbol;
      const source = init ? symbolOf(root(init))?.compilerSymbol : undefined;
      if (alias && source) {
        charge(); if (!aliasSources.has(alias)) aliasSources.set(alias, new Set()); aliasSources.get(alias)!.add(source);
      }
    }
    let target: Node | undefined;
    if (Node.isBinaryExpression(expression) && expression.getOperatorToken().getKind() >= SyntaxKind.FirstAssignment
      && expression.getOperatorToken().getKind() <= SyntaxKind.LastAssignment) target = expression.getLeft();
    else if ((Node.isPrefixUnaryExpression(expression) || Node.isPostfixUnaryExpression(expression))
      && [SyntaxKind.PlusPlusToken, SyntaxKind.MinusMinusToken].includes(expression.getOperatorToken())) target = expression.getOperand();
    else if (Node.isDeleteExpression(expression)) target = expression.getExpression();
    else if (Node.isCallExpression(expression)) {
      const callee = expression.getExpression();
      if (Node.isPropertyAccessExpression(callee) && callee.getName() === 'assign'
        && Node.isIdentifier(callee.getExpression()) && rawText(callee.getExpression()) === 'Object') target = expression.getArguments()[0];
    }
    if (target) { const symbol = symbolOf(root(target))?.compilerSymbol; if (symbol) writes.add(symbol); }
  }
  // Each written symbol is enqueued once; each distinct alias→storage edge is examined once.
  const queue = [...writes];
  for (let index = 0; index < queue.length; index++) {
    charge();
    for (const source of aliasSources.get(queue[index]) ?? []) {
      charge(); if (!writes.has(source)) { writes.add(source); queue.push(source); }
    }
  }
  const fold = (node: Node | undefined, seen = new Set<Node>()): Node | undefined => {
    charge();
    if (!node || seen.has(node) || seen.size > 30) return undefined;
    seen.add(node);
    if (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isSatisfiesExpression(node)) return fold(node.getExpression(), seen);
    if (Node.isIdentifier(node)) {
      const declarations = declarationsOf(node);
      if (declarations.length !== 1) return undefined;
      const declaration = declarations[0];
      if (Node.isVariableDeclaration(declaration) && declaration.getSourceFile() === file
        && declaration.getVariableStatement()?.getDeclarationKind() === 'const' && !writes.has(symbolOf(declaration)?.compilerSymbol!)) return fold(declaration.getInitializer(), seen);
    }
    return node;
  };
  const imported = (expression: Node, modules: string[], member: string): boolean => {
    charge();
    const matchesModule = (declaration: Node): boolean => {
      const imported = declaration.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
      charge(imported.getModuleSpecifier().getFullWidth() + modules.length + 1);
      return modules.includes(imported.getModuleSpecifierValue());
    };
    if (Node.isIdentifier(expression)) return declarationsOf(expression).some(declaration => {
      charge(); return Node.isImportSpecifier(declaration) && declaration.getName() === member && matchesModule(declaration);
    });
    if (Node.isPropertyAccessExpression(expression) && expression.getName() === member)
      return declarationsOf(expression.getExpression()).some(declaration => { charge(); return Node.isNamespaceImport(declaration) && matchesModule(declaration); });
    return false;
  };
  const text = (expression: Node | undefined, seen = new Set<Node>()): string | undefined => {
    charge(seen.size + 1);
    const node = fold(expression, new Set(seen));
    if (!node || seen.has(node) || seen.size > 30) return undefined;
    seen.add(node);
    if (Node.isStringLiteral(node) || Node.isNoSubstitutionTemplateLiteral(node)) { charge(node.getFullWidth() + 1); return node.getLiteralText(); }
    if (Node.isPropertyAccessExpression(node) && node.getName() === 'dirname' && rawText(node.getExpression()) === 'import.meta') return packageRoot;
    if (Node.isCallExpression(node) && imported(node.getExpression(), ['node:path', 'path'], 'resolve')) {
      const args = node.getArguments().map(arg => { charge(seen.size + 1); return text(arg, new Set(seen)); });
      if (args.every((value): value is string => value !== undefined) && args.length && path.isAbsolute(args[0])) { charge(args.reduce((size, arg) => size + arg.length, 1)); return path.resolve(...args); }
    }
    return undefined;
  };
  const property = (expression: Node | undefined, name: string): Node | undefined => {
    const object = fold(expression);
    if (!object || !Node.isObjectLiteralExpression(object)) return undefined;
    let match: Node | undefined;
    for (const item of object.getProperties()) {
      charge();
      if (Node.isSpreadAssignment(item) || ('getNameNode' in item && Node.isComputedPropertyName((item as { getNameNode(): Node }).getNameNode()))) return undefined;
      if ('getName' in item) {
        charge((item as { getNameNode(): Node }).getNameNode().getFullWidth() + 1);
        if ((item as { getName(): string }).getName() === name) { if (match) return undefined; match = item; }
      }
    }
    return match && Node.isPropertyAssignment(match) ? match.getInitializer()
      : match && Node.isShorthandPropertyAssignment(match) ? match.getNameNode() : match && Node.isMethodDeclaration(match) ? match : undefined;
  };
  const assignments = file.getExportAssignments(); charge(assignments.length + 1);
  if (assignments.length !== 1) return undefined;
  let configuration = fold(assignments[0].getExpression());
  if (configuration && Node.isCallExpression(configuration) && imported(configuration.getExpression(), ['vite'], 'defineConfig')) configuration = fold(configuration.getArguments()[0]);
  if (!configuration || !Node.isObjectLiteralExpression(configuration)) return undefined;
  const build = property(configuration, 'build');
  const library = property(build, 'lib');
  const entry = text(property(library, 'entry'));
  const fileName = fold(property(library, 'fileName'));
  if (!entry || !fileName || !(Node.isArrowFunction(fileName) || Node.isFunctionExpression(fileName) || Node.isMethodDeclaration(fileName))) return undefined;
  const body = fileName.getBody();
  const statements = body && Node.isBlock(body) && body.compilerNode.statements.length === 1 ? body.getStatements() : [];
  charge(statements.length + 1);
  const result = body && Node.isBlock(body) ? statements[0] && Node.isReturnStatement(statements[0]) ? statements[0].getExpression() : undefined : body;
  const name = text(result);
  if (!name) return undefined;
  charge(name.length + 1);
  if (!/\.[cm]?js$/.test(name) || path.basename(name) !== name) return undefined;
  const configRootExpression = property(configuration, 'root');
  const configuredRoot = configRootExpression ? text(configRootExpression) : packageRoot;
  const outExpression = property(build, 'outDir');
  const outDir = outExpression ? text(outExpression) : 'dist';
  if (configuredRoot === undefined || outDir === undefined) return undefined;
  charge(configuredRoot.length + outDir.length + entry.length + name.length + packageRoot.length + 1);
  const buildRoot = path.resolve(packageRoot, configuredRoot);
  return { output: path.resolve(buildRoot, outDir, name), source: path.resolve(buildRoot, entry) };
}
