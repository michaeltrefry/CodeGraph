import { Node, SourceFile, SyntaxKind, CallExpression, ClassDeclaration, ExportDeclaration, ExportSpecifier, VariableDeclaration } from 'ts-morph';
import * as path from 'path';
import { addNpmExportMetadata } from './packageExports';
import { ExtractionWorkBudget, ExtractionWorkControl } from './extractionWork';
import type { GraphNodeDto, PendingEdgeDto, ResolvedImportDto, UnresolvedCallDto } from './protocol';

export function declarationQN(file: string, name: string): string {
  return [...file.replace(/\.(?:tsx?|jsx?)$/, '').replace(/\\/g, '/').split('/').filter(p => p !== 'src' && p !== ''), name].join('.');
}

export function sameSymbol(reference: Node, declaration: Node | undefined): boolean {
  const symbol = reference.getSymbol();
  return !!symbol && !!declaration && symbol.compilerSymbol === declaration.getSymbol()?.compilerSymbol;
}

export function isCallable(node: Node): boolean {
  return Node.isFunctionDeclaration(node) || Node.isFunctionExpression(node) || Node.isArrowFunction(node) ||
    Node.isMethodDeclaration(node) || Node.isGetAccessorDeclaration(node) || Node.isSetAccessorDeclaration(node) || Node.isConstructorDeclaration(node);
}

type LocalExportDeclaration = ReturnType<SourceFile['getFunctions']>[number] | ReturnType<SourceFile['getClasses']>[number]
  | ReturnType<SourceFile['getInterfaces']>[number] | VariableDeclaration | ReturnType<SourceFile['getEnums']>[number];
export interface FileExportIndex {
  declarations: LocalExportDeclaration[];
  direct: Map<string, LocalExportDeclaration>;
  explicit: Set<string>;
  named: Map<string, Array<{ declaration: ExportDeclaration; item: ExportSpecifier }>>;
  stars: ExportDeclaration[];
  assignment?: ReturnType<SourceFile['getExportAssignments']>[number];
  defaultDeclaration?: LocalExportDeclaration;
}

/** Shared, source-scoped symbol seam for subsequent HTTP, routes and desktop passes.
 * All resolution is bounded to the already admitted ASTs. Package dependencies remain imports,
 * not guessed same-name declarations. Explicit exports/aliases are followed without node_modules.
 */
export class SemanticModel {
  readonly declarations = new Map<Node, string>();
  readonly callables = new Map<Node, string>();
  readonly byPath = new Map<string, SourceFile>();
  readonly byQN = new Map<string, Node>();
  private readonly relativePaths = new Map<SourceFile, string>();
  private readonly importBySource = new Map<string, ResolvedImportDto>();
  private readonly resolvedModules = new Map<string, SourceFile | undefined>();
  private readonly externalCallSymbols = new Map<string, GraphNodeDto>();
  readonly work: ExtractionWorkBudget;
  private readonly exportIndexes = new Map<SourceFile, FileExportIndex>();
  private readonly publicNameSets = new Map<SourceFile, Set<string>>();
  private readonly starNameSources = new Map<SourceFile, Map<string, SourceFile[]>>();
  private readonly returnExpressions = new Map<Node, Node[]>();
  private readonly importFacts = new Map<SourceFile, { packages: Map<string,{names:Map<object,string>;namespaces:Set<object>;defaults:Set<object>}>; declarations:ReturnType<SourceFile['getImportDeclarations']>; namespaceModules:Map<object,string>; origins:Map<object,Array<{specifier:string;name:string;kind:'named'|'namespace'|'default'}>>; locals:Map<string,Array<{specifier:string;name:string;kind:'named'|'namespace'|'default'}>> }>();
  private readonly classFacts = new Map<ClassDeclaration, { implemented:Map<string,Node>; methods:Map<string,ReturnType<ClassDeclaration['getMethods']>[number]>; properties:Map<string,ReturnType<ClassDeclaration['getProperties']>[number]>; parameters:Map<string,ReturnType<ReturnType<ClassDeclaration['getConstructors']>[number]['getParameters']>[number]>; expressions:Node[]; base?:Node }>();
  private readonly localValues = new Map<SourceFile,{classes:Map<string,ClassDeclaration>;variables:Map<string,VariableDeclaration>}>();
  private readonly objectMembers = new Map<Node,Map<string,Node>>();
  private readonly resolvedRoots = new Map<Node, Node | undefined>();
  private readonly allVariables: VariableDeclaration[] = [];
  private readonly writeTargets: Node[] = [];
  private mutationIndex?: Set<Node | string>;
  private buildingMutations = false;
  constructor(readonly repository: string, readonly root: string, readonly files: SourceFile[],
    readonly imports: ResolvedImportDto[], readonly diagnostic: (message: string) => void,
    control: ExtractionWorkControl | ExtractionWorkBudget = {}) {
    this.work = control instanceof ExtractionWorkBudget ? control : new ExtractionWorkBudget(control);
    for (const file of files) this.byPath.set(this.relative(file), file);
    for (const imported of imports) {
      this.work.charge(imported.filePath.length + imported.importedNamespace.length + 1);
      const key = `${imported.filePath}\0${imported.importedNamespace}`;
      if (!this.importBySource.has(key)) this.importBySource.set(key, imported);
    }
    for (const file of files) {
      file.forEachDescendant(node => {
        this.work.charge();
        if (Node.isVariableDeclaration(node)) this.allVariables.push(node);
        if (Node.isBinaryExpression(node) && node.getOperatorToken().getKind() >= SyntaxKind.FirstAssignment && node.getOperatorToken().getKind() <= SyntaxKind.LastAssignment) this.writeTargets.push(node.getLeft());
        else if ((Node.isPrefixUnaryExpression(node) || Node.isPostfixUnaryExpression(node)) && [SyntaxKind.PlusPlusToken, SyntaxKind.MinusMinusToken].includes(node.getOperatorToken())) this.writeTargets.push(node.getOperand());
        else if (Node.isDeleteExpression(node)) this.writeTargets.push(node.getExpression());
        else if (Node.isCallExpression(node)) {
          const expr = node.getExpression();
          if (Node.isPropertyAccessExpression(expr) && expr.getName() === 'assign' && Node.isIdentifier(expr.getExpression()) && expr.getExpression().getText() === 'Object' && node.getArguments()[0]) this.writeTargets.push(node.getArguments()[0]);
        }
        if (Node.isClassDeclaration(node) || Node.isInterfaceDeclaration(node) || Node.isEnumDeclaration(node) || Node.isVariableDeclaration(node)) {
          this.identify(node, this.qn(node));
        }
        if (isCallable(node)) {
          const qn = this.qn(node);
          this.identify(node, qn);
          this.callables.set(node, qn);
        }
      });
    }
  }
  relative(node: Node): string {
    const file = node.getSourceFile();
    let relative = this.relativePaths.get(file);
    if (relative === undefined) {
      this.work.charge(this.root.length + file.getFilePath().length + 1);
      relative = path.relative(this.root, file.getFilePath()).replace(/\\/g, '/');
      this.relativePaths.set(file, relative);
    }
    return relative;
  }
  fileQN(node: Node): string { return `${this.repository}:${this.relative(node)}`; }
  private identify(node: Node, qn: string): void { this.declarations.set(node, qn); this.byQN.set(qn, node); }
  private qn(node: Node): string {
    const owner = node.getAncestors().find(ancestor => isCallable(ancestor) || Node.isClassDeclaration(ancestor));
    const nameNode = (node as unknown as { getNameNode?: () => Node | undefined }).getNameNode?.();
    this.work.charge((nameNode?.getFullWidth() ?? 0) + 1);
    let name = (node as unknown as { getName?: () => string | undefined }).getName?.();
    const parent = node.getParent();
    if (parent && (Node.isVariableDeclaration(parent) || Node.isPropertyAssignment(parent) || Node.isPropertyDeclaration(parent)) &&
        (Node.isArrowFunction(node) || Node.isFunctionExpression(node))) name = parent.getName();
    if (!name && parent && Node.isExportAssignment(parent)) name = 'default';
    if (!name && Node.isClassDeclaration(node) && node.isDefaultExport()) name = 'default';
    // React wrappers own the enclosed render callable under the exported variable's identity.
    if (!name && parent && Node.isCallExpression(parent)) {
      let wrapper: Node = parent;
      while (Node.isCallExpression(wrapper) && this.reactApi(wrapper)) {
        const next = wrapper.getParent();
        if (next && Node.isVariableDeclaration(next)) { name = next.getName(); break; }
        if (!next) break; wrapper = next;
      }
    }
    if (!name) {
      // AST-relative path survives unrelated edits elsewhere in the file; duplicate callbacks in
      // the same owner remain distinct without relying on line numbers or global counters.
      let current: Node = node; const segments: number[] = [];
      while (current.getParent() && current.getParent() !== owner && !Node.isSourceFile(current.getParent()!)) {
        const parentNode = current.getParentOrThrow(); segments.unshift(parentNode.getChildren().indexOf(current)); current = parentNode;
      }
      name = Node.isConstructorDeclaration(node) ? 'constructor' : `$callback${segments.join('_')}`;
    }
    const containers: string[] = [];
    for (const ancestor of node.getAncestors()) {
      if (ancestor === owner) break;
      if ((Node.isVariableDeclaration(ancestor) || Node.isPropertyAssignment(ancestor) || Node.isPropertyDeclaration(ancestor)) &&
          Node.isObjectLiteralExpression(ancestor.getInitializer())) containers.unshift(ancestor.getName());
    }
    const scopes: string[] = [];
    if (owner) for (const ancestor of node.getAncestors()) {
      if (ancestor === owner) break;
      if (!Node.isBlock(ancestor) || ancestor.getParent() === owner) continue;
      let current: Node = ancestor; const indices: number[] = [];
      while (current.getParent() && current !== owner) {
        const parentNode = current.getParentOrThrow(); indices.unshift(parentNode.getChildren().indexOf(current)); current = parentNode;
      }
      scopes.unshift(`$scope${indices.join('_')}`);
    }
    this.work.charge(scopes.reduce((size, value) => size + value.length, 0) + containers.reduce((size, value) => size + value.length, 0) + name.length + this.relative(node).length + this.repository.length + 8);
    const lexicalName = [...scopes, ...containers, name].join('.');
    if (owner) return `${this.declarations.get(owner) ?? this.qn(owner)}.${lexicalName}`;
    return Node.isClassDeclaration(node) || Node.isInterfaceDeclaration(node) || Node.isEnumDeclaration(node)
      ? declarationQN(this.relative(node), lexicalName)
      : `${this.fileQN(node)}#${lexicalName}`;
  }
  owner(node: Node): string {
    for (const ancestor of node.getAncestors()) {
      const callable = this.callables.get(ancestor);
      if (callable) return callable;
    }
    return this.fileQN(node);
  }
  private importsFor(file:SourceFile) {
    this.work.charge(); const prior=this.importFacts.get(file); if(prior) return prior;
    const packages=new Map<string,{names:Map<object,string>;namespaces:Set<object>;defaults:Set<object>}>(); const namespaceModules=new Map<object,string>(); const locals=new Map<string,Array<{specifier:string;name:string;kind:'named'|'namespace'|'default'}>>(); const origins=new Map<object,Array<{specifier:string;name:string;kind:'named'|'namespace'|'default'}>>();
    const origin=(symbol:object,specifier:string,name:string,kind:'named'|'namespace'|'default',local:string,includeLocal=true)=>{ const value={specifier,name,kind}; if(!origins.has(symbol)) origins.set(symbol,[]); origins.get(symbol)!.push(value); if(includeLocal) { if(!locals.has(local)) locals.set(local,[]); locals.get(local)!.push(value); } };
    this.work.charge(file.compilerNode.statements.length+1);
    const declarations=file.getImportDeclarations();
    for(const declaration of declarations) {
      this.work.charge(declaration.getFullWidth()+1); const name=declaration.getModuleSpecifierValue();
      if(!packages.has(name)) packages.set(name,{names:new Map(),namespaces:new Set(),defaults:new Set()}); const facts=packages.get(name)!; const namedLocals=new Set<string>();
      for(const item of declaration.getNamedImports()) {
        this.work.charge(item.getFullWidth()+1); const symbol=(item.getAliasNode()??item.getNameNode()).getSymbol()?.compilerSymbol;
        if(symbol) { const importedName=item.getName(); if(!facts.names.has(symbol)) facts.names.set(symbol,importedName); const local=(item.getAliasNode()??item.getNameNode()).getText(); origin(symbol,name,importedName,'named',local,!namedLocals.has(local)); namedLocals.add(local); }
      }
      const namespace=declaration.getNamespaceImport()?.getSymbol()?.compilerSymbol;
      if(namespace) { origin(namespace,name,'*','namespace',declaration.getNamespaceImport()!.getText()); facts.namespaces.add(namespace); if(!namespaceModules.has(namespace)) namespaceModules.set(namespace,name); }
      const defaultSymbol=declaration.getDefaultImport()?.getSymbol()?.compilerSymbol; if(defaultSymbol) { facts.defaults.add(defaultSymbol); origin(defaultSymbol,name,'default','default',declaration.getDefaultImport()!.getText()); }
    }
    const facts={packages,declarations,namespaceModules,origins,locals}; this.importFacts.set(file,facts); return facts;
  }
  /** Actual local import symbols only; caller-specific values and argument environments are never cached. */
  private importedName(node:Node,facts:{names:Map<object,string>;namespaces:Set<object>;defaults:Set<object>},defaultNamespace:boolean):string|undefined {
    if(Node.isIdentifier(node)) { const symbol=node.getSymbol()?.compilerSymbol; return symbol?facts.names.get(symbol):undefined; }
    const receiver=Node.isPropertyAccessExpression(node)?node.getExpression():Node.isQualifiedName(node)?node.getLeft():undefined;
    if(receiver) { const symbol=receiver.getSymbol()?.compilerSymbol; if(symbol&&(facts.namespaces.has(symbol)||defaultNamespace&&facts.defaults.has(symbol))) {
      const name=Node.isPropertyAccessExpression(node)?node.getNameNode():Node.isQualifiedName(node)?node.getRight():undefined;
      if(name) { this.work.charge(name.getFullWidth()+1); return name.getText(); }
    } }
    return undefined;
  }
  importedApi(node:Node,packageName:string,defaultNamespace=false):string|undefined {
    this.work.charge(packageName.length+1); const facts=this.importsFor(node.getSourceFile()).packages.get(packageName);
    return facts?this.importedName(node,facts,defaultNamespace):undefined;
  }
  importDeclarations(file:SourceFile) { return this.importsFor(file).declarations; }
  importOrigins(node:Node):readonly {specifier:string;name:string;kind:'named'|'namespace'|'default'}[] {
    this.work.charge(); const receiver=Node.isPropertyAccessExpression(node)?node.getExpression():Node.isQualifiedName(node)?node.getLeft():node;
    const symbol=receiver.getSymbol()?.compilerSymbol; const origins=symbol?this.importsFor(node.getSourceFile()).origins.get(symbol):undefined;
    this.work.charge((origins?.length??0)+1); return origins??[];
  }
  templateImports(file:SourceFile,name:string) { this.work.text(name); const values=this.importsFor(file).locals.get(name); this.work.charge((values?.length??0)+1); return values??[]; }
  localValueDeclaration(file:SourceFile,name:string):Node|undefined {
    this.work.text(name); let values=this.localValues.get(file);
    if(!values) { const classes=new Map<string,ClassDeclaration>(); const variables=new Map<string,VariableDeclaration>(); this.work.charge(file.compilerNode.statements.length*2+1);
      for(const value of file.getClasses()) { this.work.charge((value.getNameNode()?.getFullWidth()??0)+1); const name=value.getName(); if(name&&!classes.has(name)) classes.set(name,value); }
      for(const value of file.getVariableDeclarations()) { this.work.charge(value.getNameNode().getFullWidth()+1); const name=value.getName(); if(!variables.has(name)) variables.set(name,value); }
      values={classes,variables}; this.localValues.set(file,values);
    }
    return values.classes.get(name)??values.variables.get(name);
  }
  importResolution(from:Node,specifier:string):ResolvedImportDto|undefined {
    this.work.charge(this.relative(from).length+specifier.length+1); return this.importBySource.get(`${this.relative(from)}\0${specifier}`);
  }
  private classIndex(cls:ClassDeclaration) {
    this.work.charge(); const prior=this.classFacts.get(cls); if(prior) return prior;
    this.work.charge(cls.getFullWidth()+cls.compilerNode.members.length+1);
    const implemented=new Map<string,Node>(); const expressions:Node[]=[]; const methods=new Map<string,ReturnType<ClassDeclaration['getMethods']>[number]>();
    const properties=new Map<string,ReturnType<ClassDeclaration['getProperties']>[number]>(); const parameters=new Map<string,ReturnType<ReturnType<ClassDeclaration['getConstructors']>[number]['getParameters']>[number]>();
    for(const item of cls.getImplements()) { const expression=item.getExpression(); this.work.charge(expression.getFullWidth()+1); const text=expression.getText(); if(!implemented.has(text)) { implemented.set(text,expression); expressions.push(expression); } }
    for(const method of cls.getMethods()) { this.work.charge(method.getNameNode().getFullWidth()+1); const name=method.getName(); if(!methods.has(name)) methods.set(name,method); }
    for(const property of cls.getProperties()) { this.work.charge(property.getNameNode().getFullWidth()+1); const name=property.getName(); if(!properties.has(name)) properties.set(name,property); }
    for(const constructor of cls.getConstructors()) { this.work.charge(constructor.compilerNode.parameters.length+1); for(const parameter of constructor.getParameters()) {
      this.work.charge(parameter.getNameNode().getFullWidth()+1); const name=parameter.getName(); if(!parameters.has(name)) parameters.set(name,parameter);
    } }
    const facts={implemented,methods,properties,parameters,expressions,base:cls.getExtends()?.getExpression()}; this.classFacts.set(cls,facts); return facts;
  }
  classProperty(cls:ClassDeclaration,name:string) { this.work.text(name); return this.classIndex(cls).properties.get(name); }
  classMethod(cls:ClassDeclaration,name:string) { this.work.text(name); return this.classIndex(cls).methods.get(name); }
  constructorParameter(cls:ClassDeclaration,name:string) { this.work.text(name); return this.classIndex(cls).parameters.get(name); }
  implementedExpression(cls:ClassDeclaration,text:string) { this.work.text(text); return this.classIndex(cls).implemented.get(text); }
  implementedExpressions(cls:ClassDeclaration):readonly Node[] { const values=this.classIndex(cls).expressions; this.work.charge(values.length+1); return values; }
  baseExpression(cls:ClassDeclaration) { return this.classIndex(cls).base; }
  objectProperty(object:import('ts-morph').ObjectLiteralExpression,name:string):Node|undefined {
    this.work.text(name); let members=this.objectMembers.get(object);
    if(!members) { members=new Map(); this.work.charge(object.compilerNode.properties.length+1);
      for(const member of object.getProperties()) { this.work.charge(member.getFullWidth()+1); if(Node.isSpreadAssignment(member)) continue; const key=member.getName(); if(!members.has(key)) members.set(key,member); }
      this.objectMembers.set(object,members);
    }
    return members.get(name);
  }
  module(from: Node, specifier: string): SourceFile | undefined {
    this.work.charge(this.relative(from).length + specifier.length + 1);
    const key = `${this.relative(from)}\0${specifier}`;
    if (this.resolvedModules.has(key)) return this.resolvedModules.get(key);
    const resolved = this.importBySource.get(key);
    const module = resolved?.resolvedFilePath ? this.byPath.get(resolved.resolvedFilePath) : undefined;
    this.resolvedModules.set(key, module);
    return module;
  }
  exportIndex(file: SourceFile): FileExportIndex {
    this.work.charge(); const cached = this.exportIndexes.get(file); if (cached) return cached;
    this.work.charge(file.compilerNode.statements.length * 2 + 1);
    const index: FileExportIndex = { declarations: [], direct: new Map(), explicit: new Set(), named: new Map(), stars: [] };
    const exports = file.getSymbol();
    this.work.charge((exports?.compilerSymbol.exports?.size ?? 0) + 1);
    const exportedSymbols = new Set<object>(); const defaultSymbols = new Set<object>();
    for (const symbol of exports?.getExports() ?? []) {
      this.work.charge(); const alias = symbol.getAliasedSymbol(); exportedSymbols.add(symbol.compilerSymbol);
      if (alias) exportedSymbols.add(alias.compilerSymbol);
      if (symbol.getName() === 'default') { defaultSymbols.add(symbol.compilerSymbol); if (alias) defaultSymbols.add(alias.compilerSymbol); }
    }
    for (const statement of file.getStatements()) {
      this.work.charge();
      if (Node.isFunctionDeclaration(statement) || Node.isClassDeclaration(statement) || Node.isInterfaceDeclaration(statement) || Node.isEnumDeclaration(statement)) index.declarations.push(statement);
      if (Node.isVariableStatement(statement)) { this.work.charge(statement.compilerNode.declarationList.declarations.length); index.declarations.push(...statement.getDeclarations()); }
      if (Node.isExportAssignment(statement) && !index.assignment) index.assignment = statement;
      if (!Node.isExportDeclaration(statement)) continue;
      const named = statement.getNamedExports();
      if (!named.length) index.stars.push(statement);
      for (const item of named) {
        this.work.charge(item.getFullWidth() + 1);
        const name = item.getAliasNode()?.getText() ?? item.getName(); index.explicit.add(name);
        if (!index.named.has(name)) index.named.set(name, []); index.named.get(name)!.push({ declaration: statement, item });
      }
    }
    for (const declaration of index.declarations) {
      this.work.charge((declaration.getNameNode()?.getFullWidth() ?? 0) + 1);
      const name = declaration.getName();
      const symbol = (!declaration.hasExportKeyword() || defaultSymbols.size) ? declaration.getSymbol()?.compilerSymbol : undefined;
      const isDefault = declaration.hasDefaultKeyword() || !!symbol && defaultSymbols.has(symbol);
      if (isDefault) { index.explicit.add('default'); if (!index.defaultDeclaration) index.defaultDeclaration = declaration; }
      if (name && (declaration.hasExportKeyword() || !!symbol && exportedSymbols.has(symbol))) {
        if (!index.direct.has(name)) index.direct.set(name, declaration); index.explicit.add(isDefault ? 'default' : name);
      }
    }
    if (index.assignment) index.explicit.add('default');
    this.exportIndexes.set(file, index); return index;
  }
  publicExportNames(file: SourceFile): ReadonlySet<string> {
    this.work.charge(); const prior = this.publicNameSets.get(file); if (prior) return prior;
    const pending = [file]; const discovered = new Set<SourceFile>();
    const reverse = new Map<SourceFile, Set<SourceFile>>(); const frontier = new Set<SourceFile>();
    while (pending.length) {
      this.work.charge(); const current = pending.pop()!;
      if (discovered.has(current)) continue; discovered.add(current);
      if (this.publicNameSets.has(current)) { frontier.add(current); continue; }
      const index = this.exportIndex(current); const names = new Set<string>();
      for (const name of index.explicit) { this.work.text(name); names.add(name); }
      this.publicNameSets.set(current, names);
      for (const star of index.stars) {
        this.work.charge(); const specifier = star.getModuleSpecifierValue(); if (!specifier) continue;
        const target = this.module(current, specifier); if (!target) continue;
        if (!reverse.has(target)) reverse.set(target, new Set()); reverse.get(target)!.add(current); pending.push(target);
      }
    }
    const queue: Array<[SourceFile, string]> = [];
    for (const current of discovered) for (const name of this.publicNameSets.get(current) ?? []) { this.work.text(name); queue.push([current, name]); }
    for (let cursor = 0; cursor < queue.length; cursor++) {
      this.work.charge(); const [source, name] = queue[cursor]; if (name === 'default') continue;
      for (const consumer of reverse.get(source) ?? []) {
        this.work.text(name); const names = this.publicNameSets.get(consumer)!;
        if (!names.has(name)) { names.add(name); queue.push([consumer, name]); }
      }
    }
    return this.publicNameSets.get(file)!;
  }
  private starSources(file: SourceFile, name: string): readonly SourceFile[] {
    let index = this.starNameSources.get(file);
    if (!index) {
      index = new Map(); const targets = new Set<SourceFile>();
      for (const star of this.exportIndex(file).stars) {
        this.work.charge(); const specifier = star.getModuleSpecifierValue(); const target = specifier ? this.module(file, specifier) : undefined;
        if (target) targets.add(target);
      }
      for (const target of targets) for (const publicName of this.publicExportNames(target)) {
        this.work.text(publicName); if (publicName === 'default') continue;
        if (!index.has(publicName)) index.set(publicName, []); index.get(publicName)!.push(target);
      }
      this.starNameSources.set(file, index);
    }
    return index.get(name) ?? [];
  }
  /** One run-wide alias/mutation graph. Writes propagate back to referenced storage once;
   * imported provenance anchors preserve namespace and exact named-import uncertainty. */
  private ensureMutationIndex(): Set<Node | string> {
    if (this.mutationIndex) return this.mutationIndex;
    const written = new Set<Node | string>();
    if (!this.writeTargets.length) { this.mutationIndex = written; return written; }
    if (this.buildingMutations) throw new Error('Recursive member mutation indexing');
    this.buildingMutations = true;
    const links = new Map<Node | string, Set<Node | string>>();
    const key = (node: Node): Node => { const symbol = node.getSymbol(); this.work.charge((symbol?.compilerSymbol.declarations?.length ?? 0) + 1); return symbol?.getDeclarations()[0] ?? node; };
    const link = (from: Node | string, target: Node | string): void => {
      this.work.charge(); if (!links.has(from)) links.set(from, new Set()); links.get(from)!.add(target);
    };
    const root = (node: Node): Node => {
      while (Node.isPropertyAccessExpression(node) || Node.isElementAccessExpression(node) || Node.isParenthesizedExpression(node) || Node.isAsExpression(node)) { this.work.charge(); node = node.getExpression(); }
      return node;
    };
    try {
      for (const alias of this.allVariables) {
        this.work.charge(); const init = alias.getInitializer(); if (!init) continue;
        const source = root(init); if (!Node.isIdentifier(source)) continue;
        const resolved = this.resolve(init); const rootDeclaration = this.resolve(source);
        const binding = alias.getNameNode();
        const names = Node.isIdentifier(binding) ? [binding] : binding.getDescendantsOfKind(SyntaxKind.Identifier);
        for (const name of names) {
          const from = key(name); link(from, key(source));
          if (resolved) link(from, key(resolved));
          if (rootDeclaration) link(from, key(rootDeclaration));
        }
      }
      for (const file of this.files) for (const imported of this.importDeclarations(file)) {
        this.work.charge(imported.getModuleSpecifier().getFullWidth() + 1);
        const specifier = imported.getModuleSpecifierValue();
        for (const named of imported.getNamedImports()) {
          this.work.charge(named.getFullWidth() + 1);
          const local = named.getAliasNode() ?? named.getNameNode(); const from = key(local); const target = this.resolve(local);
          if (target) link(from, key(target));
          link(from, `named\0${specifier}\0${named.getName()}`); link(from, `any\0${specifier}`);
        }
        const namespace = imported.getNamespaceImport(); if (namespace) link(key(namespace), `namespace\0${specifier}`);
      }
      for (const target of this.writeTargets) {
        let part: Node | undefined = target;
        while (part) {
          this.work.charge(); written.add(key(part)); const resolved = this.resolve(part); if (resolved) written.add(key(resolved));
          part = Node.isPropertyAccessExpression(part) || Node.isElementAccessExpression(part) ? part.getExpression() : undefined;
        }
      }
      const queue = [...written];
      for (let cursor = 0; cursor < queue.length; cursor++) {
        this.work.charge(); for (const source of links.get(queue[cursor]) ?? []) { this.work.charge(); if (!written.has(source)) { written.add(source); queue.push(source); } }
      }
      this.mutationIndex = written; return written;
    } finally { this.buildingMutations = false; }
  }
  hasMemberWrites(node: Node, provenance?: Record<string, unknown>): boolean {
    this.work.charge(); const written = this.ensureMutationIndex(); if (!written.size) return false;
    const symbol = node.getSymbol(); this.work.charge((symbol?.compilerSymbol.declarations?.length ?? 0) + 1);
    const key = symbol?.getDeclarations()[0] ?? node;
    if (written.has(key)) return true;
    const resolved = this.resolve(node); if (resolved && written.has(resolved.getSymbol()?.getDeclarations()[0] ?? resolved)) return true;
    if (!provenance) return false;
    const specifier = String(provenance.import_path); this.work.text(specifier);
    return written.has(`namespace\0${specifier}`) || written.has(provenance.export_name === undefined ? `any\0${specifier}` : `named\0${specifier}\0${provenance.export_name}`);
  }
  /** Imported npm provenance, including aliases and bounded local barrels. A display name or
   * an instance member alone never establishes an external application declaration. */
  externalReference(expression: Node | undefined, seen = new Set<Node>()): { qualifiedName: string; properties: Record<string, unknown> } | undefined {
    if (!expression || seen.has(expression) || seen.size > 60) return undefined;
    seen.add(expression);
    const imported = (from: Node, specifier: string, name: string, history = seen): ReturnType<SemanticModel['externalReference']> => {
      const module = this.module(from, specifier);
      if (module) return exported(module, name, history);
      const resolution = this.importBySource.get(`${this.relative(from)}\0${specifier}`);
      if (resolution?.classification !== 'external_npm_package' || !resolution.externalPackageName || specifier.startsWith('node:')) return undefined;
      return { qualifiedName: `npm-symbol:${specifier}#${name}`, properties: {
        package_name: resolution.externalPackageName, import_path: specifier, export_name: name,
      } };
    };
    const exported = (file: SourceFile, name: string, history: Set<Node>): ReturnType<SemanticModel['externalReference']> => {
      if (history.has(file) || history.size > 60) return undefined;
      const branch = new Set(history); branch.add(file);
      if (name === 'default' && file.getExportAssignments().length)
        return this.externalReference(file.getExportAssignments()[0].getExpression(), branch);
      const index = this.exportIndex(file);
      const explicit = index.named.get(name) ?? [];
      if (explicit.length) {
        const candidates = explicit.map(({ declaration, item }) => declaration.getModuleSpecifierValue()
          ? imported(declaration, declaration.getModuleSpecifierValue()!, item.getName(), branch)
          : this.externalReference(item.getNameNode(), branch));
        const identities = new Map(candidates.filter((value): value is NonNullable<typeof value> => !!value).map(value => [value.qualifiedName, value]));
        return candidates.every(value => !!value) && identities.size === 1 ? [...identities.values()][0] : undefined;
      }
      // Resolve a directly exported local alias before considering stars. Actual local
      // functions/classes have already resolved through target(), so they cannot become imports.
      const candidate = name === 'default' ? index.defaultDeclaration : index.direct.get(name);
      const local = candidate && (Node.isFunctionDeclaration(candidate) || Node.isClassDeclaration(candidate) || Node.isVariableDeclaration(candidate)) ? candidate : undefined;
      if (local) return this.externalReference(local, branch);
      const stars = index.stars.filter(declaration => !!declaration.getModuleSpecifierValue());
      const candidates = stars.map(declaration => imported(declaration, declaration.getModuleSpecifierValue()!, name, branch));
      const identities = new Map(candidates.filter((value): value is NonNullable<typeof value> => !!value).map(value => [value.qualifiedName, value]));
      return candidates.length && candidates.every(value => !!value) && identities.size === 1 ? [...identities.values()][0] : undefined;
    };
    if (Node.isParenthesizedExpression(expression) || Node.isAsExpression(expression) || Node.isNonNullExpression(expression))
      return this.externalReference(expression.getExpression(), seen);
    if (Node.isVariableDeclaration(expression) && expression.getVariableStatement()?.getDeclarationKind() === 'const')
      return this.externalReference(expression.getInitializer(), seen);
    if (Node.isPropertyAccessExpression(expression)) {
      const namespace = expression.getExpression().getSymbol()?.getDeclarations() ?? [];
      if (namespace.length === 1 && Node.isNamespaceImport(namespace[0])) {
        const declaration = namespace[0].getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
        if (this.hasMemberWrites(expression.getExpression(), { import_path: declaration.getModuleSpecifierValue() })) {
          this.diagnostic(`externalMemberUnresolved: ${declaration.getModuleSpecifierValue()} namespace has writes`);
          return undefined;
        }
        return imported(declaration, declaration.getModuleSpecifierValue(), expression.getName());
      }
      const base = this.externalReference(expression.getExpression(), new Set(seen));
      if (base) {
        if (this.hasMemberWrites(expression.getExpression(), base.properties)) {
          this.diagnostic(`externalMemberUnresolved: ${base.qualifiedName} has writes through its binding or aliases`);
          return undefined;
        }
        const member = [base.properties.member_path, expression.getName()].filter(Boolean).join('.');
        return { qualifiedName: `npm-symbol:${base.properties.import_path}#${base.properties.export_name}::${member}`,
          properties: { ...base.properties, member_path: member } };
      }
      return undefined;
    }
    const declarations = expression.getSymbol()?.getDeclarations() ?? [];
    if (declarations.length !== 1) return undefined;
    const declaration = declarations[0];
    if (Node.isImportSpecifier(declaration)) {
      const imp = declaration.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
      return imported(imp, imp.getModuleSpecifierValue(), declaration.getName());
    }
    if (Node.isImportClause(declaration)) return imported(declaration, declaration.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!.getModuleSpecifierValue(), 'default');
    if (Node.isVariableDeclaration(declaration)) return this.externalReference(declaration, seen);
    return undefined;
  }
  exported(file: SourceFile, name: string, seen = new Set<string>()): Node | undefined {
    this.work.charge(this.relative(file).length + name.length + seen.size + 1);
    const index = this.exportIndex(file); const key = `${this.relative(file)}:${name}`;
    if (seen.has(key)) return undefined; seen.add(key);
    if (name === 'default') {
      if (index.assignment) return this.resolve(index.assignment.getExpression(), seen);
      if (index.defaultDeclaration) return index.defaultDeclaration;
    }
    const direct = index.direct.get(name); if (direct) return direct;
    const candidates = new Set<Node>();
    for (const { declaration, item } of index.named.get(name) ?? []) {
      this.work.charge(seen.size + 1); const specifier = declaration.getModuleSpecifierValue();
      const target = specifier ? this.module(file, specifier) : file; if (!target) continue;
      const value = target === file ? this.resolve(item.getNameNode(), new Set(seen)) : this.exported(target, item.getName(), new Set(seen));
      if (value) candidates.add(value);
    }
    if (name !== 'default') for (const target of this.starSources(file, name)) {
      this.work.charge(seen.size + 1); const value = this.exported(target, name, new Set(seen)); if (value) candidates.add(value);
    }
    if (candidates.size > 1) this.diagnostic(`symbolAmbiguity: ${key} has ${candidates.size} export candidates`);
    return candidates.size === 1 ? [...candidates][0] : undefined;
  }
  resolve(node: Node | undefined, seen?: Set<string>): Node | undefined {
    this.work.charge();
    if (!node) return undefined;
    // Only an independent query is context-free. Recursive/history-bound queries must
    // retain their own cycle and cutoff behavior; HTTP argument bindings live elsewhere.
    if (seen !== undefined) return this.resolveUncached(node, seen);
    if (this.resolvedRoots.has(node)) return this.resolvedRoots.get(node);
    const result = this.resolveUncached(node, new Set<string>());
    this.resolvedRoots.set(node, result);
    return result;
  }
  private resolveUncached(node: Node, seen: Set<string>): Node | undefined {
    if (seen.size > 100) { this.diagnostic(`symbolResolutionLimit: ${this.relative(node)}`); return undefined; }
    this.work.charge(node.getSourceFile().getFilePath().length + 32);
    const key = `${node.getSourceFile().getFilePath()}:${node.getPos()}:${node.getKind()}`;
    if (seen.has(key)) return undefined; seen.add(key);
    if (this.declarations.has(node)) {
      if (Node.isVariableDeclaration(node) || Node.isPropertyDeclaration(node)) {
        const init = node.getInitializer();
        if (init) return this.resolve(init, seen) ?? node;
        const type = node.getTypeNode();
        if (type) return this.resolve(type, seen) ?? node;
      }
      return node;
    }
    if (Node.isParenthesizedExpression(node) || Node.isAsExpression(node) || Node.isNonNullExpression(node) || Node.isTypeAssertion(node)) return this.resolve(node.getExpression(), seen);
    if (Node.isNewExpression(node)) return this.resolve(node.getExpression(), seen);
    if (Node.isCallExpression(node)) {
      if (['memo', 'forwardRef', 'useCallback'].includes(this.reactApi(node) ?? '')) return this.resolve(node.getArguments()[0], seen);
      if (this.reactApi(node) === 'lazy') {
        const load = node.getDescendantsOfKind(SyntaxKind.CallExpression).find(call => call.getExpression().getKind() === SyntaxKind.ImportKeyword);
        const argument = load?.getArguments()[0];
        if (argument && Node.isStringLiteral(argument)) {
          const file = this.module(node, argument.getLiteralText());
          // lazy(() => import(...).then(m => ({ default: m.Named })))
          const named = node.getDescendantsOfKind(SyntaxKind.PropertyAssignment).find(prop => prop.getName() === 'default');
          const expression = named?.getInitializer();
          const exportName = expression && Node.isPropertyAccessExpression(expression) ? expression.getName() : 'default';
          return file ? this.exported(file, exportName) : undefined;
        }
      }
      // A known local/imported factory or custom hook can return a callable/object. Follow
      // only statically unique returns and keep alternatives explicitly unresolved.
      const callee = this.resolve(node.getExpression(), new Set(seen));
      if (callee && (Node.isFunctionDeclaration(callee) || Node.isFunctionExpression(callee) || Node.isArrowFunction(callee) || Node.isMethodDeclaration(callee))) {
        const body = callee.getBody();
        if (body) {
          let expressions = this.returnExpressions.get(callee);
          if (!expressions) {
            expressions = []; const pending: Node[] = [body];
            while (pending.length) {
              this.work.charge(); const current = pending.pop()!;
              if (current !== body && isCallable(current)) continue;
              if (Node.isReturnStatement(current) && current.getExpression()) expressions.push(current.getExpression()!);
              current.forEachChild(child => { this.work.charge(); pending.push(child); });
            }
            if (!Node.isBlock(body)) expressions = [body];
            this.returnExpressions.set(callee, expressions);
          }
          this.work.charge(expressions.length * (seen.size + 1) + 1);
          const resolvedReturns = expressions.map(expression => this.resolve(expression, new Set(seen)));
          const returns = new Set(resolvedReturns.filter((value): value is Node => !!value));
          if (returns.size === 1 && resolvedReturns.every(value => !!value)) return [...returns][0];
          if (returns.size > 1 || returns.size && resolvedReturns.some(value => !value)) this.diagnostic(`functionReturnAmbiguity: ${this.declarations.get(callee)} has ${returns.size} known return candidates and ${resolvedReturns.filter(value => !value).length} unknown returns`);
        }
      }
      return undefined;
    }
    const symbol = node.getSymbol();
    this.work.charge((symbol?.compilerSymbol.declarations?.length ?? 0) + 1);
    const declarations = symbol?.getDeclarations() ?? [];
    for (const declaration of declarations) {
      if (!this.byPath.has(this.relative(declaration))) continue;
      if (Node.isImportSpecifier(declaration)) {
        const imp = declaration.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
        const target = this.module(node, imp.getModuleSpecifierValue());
        if (target) return this.exported(target, declaration.getName(), seen);
      } else if (Node.isImportClause(declaration)) {
        const imp = declaration.getFirstAncestorByKind(SyntaxKind.ImportDeclaration)!;
        const target = this.module(node, imp.getModuleSpecifierValue());
        if (target) return this.exported(target, 'default', seen);
      } else if (this.declarations.has(declaration)) return this.resolve(declaration, seen);
      else if (Node.isPropertyDeclaration(declaration) || Node.isPropertyAssignment(declaration)) return this.resolve(declaration.getInitializer(), seen);
      else if (Node.isParameterDeclaration(declaration)) {
        const type = declaration.getTypeNode();
        if (type) return this.resolve(type, seen);
      }
    }
    if (Node.isPropertyAccessExpression(node)) {
      const receiver = node.getExpression();
      if (Node.isIdentifier(receiver)) {
        const symbol=receiver.getSymbol()?.compilerSymbol; const specifier=symbol?this.importsFor(node.getSourceFile()).namespaceModules.get(symbol):undefined;
        if(specifier!==undefined) { const file=this.module(node,specifier); if(file) return this.exported(file,node.getName(),seen); }
      }
      const target = this.resolve(receiver, seen);
      if (target && Node.isClassDeclaration(target)) return this.classMethod(target,node.getName()) ?? this.classProperty(target,node.getName());
      if (target && Node.isObjectLiteralExpression(target)) {
        const prop = this.objectProperty(target,node.getName());
        if (prop && Node.isPropertyAssignment(prop)) return this.resolve(prop.getInitializer(), seen);
        if (prop && Node.isShorthandPropertyAssignment(prop)) return this.resolve(prop.getValueSymbol()?.getDeclarations()[0], seen);
        if (prop && Node.isMethodDeclaration(prop)) return prop;
      }
    }
    if (Node.isTypeReference(node)) return this.resolve(node.getTypeName(), seen);
    if (Node.isObjectLiteralExpression(node)) return node;
    return isCallable(node) ? node : undefined;
  }
  target(node: Node | undefined): string | undefined {
    const resolved = this.resolve(node);
    return resolved ? this.declarations.get(resolved) : undefined;
  }
  /** This source-only import lookup is safe during constructor declaration indexing. */
  reactApi(call:CallExpression):string|undefined { this.work.charge(); const facts=this.importsFor(call.getSourceFile()).packages.get('react'); return facts?this.importedName(call.getExpression(),facts,true):undefined; }
  extract(nodes: GraphNodeDto[], edges: PendingEdgeDto[], unresolved: UnresolvedCallDto[]): void {
    const existing = new Map(nodes.map(node => [node.qualifiedName, node]));
    const callableQNs = new Set(this.callables.values());
    for (const [callable, qn] of this.callables) {
      this.work.charge(callable.getFullWidth()+1);
      const jsx = callable.getDescendants().some(node => Node.isJsxElement(node) || Node.isJsxSelfClosingElement(node) || Node.isJsxFragment(node));
      const name = qn.split(/[.#]/).pop()!;
      const component = jsx && (/^[A-Z]/.test(name) || name === 'default');
      const node = existing.get(qn);
      if (node) { if (component) { node.label = 'Component'; node.properties.framework = 'react'; } continue; }
      const emitted: GraphNodeDto = this.work.createNode(()=>({ label: component ? 'Component' : Node.isMethodDeclaration(callable) ? 'Method' : 'Function', name,
        qualifiedName: qn, filePath: this.relative(callable), startLine: callable.getStartLineNumber(), endLine: callable.getEndLineNumber(),
        properties: { callable: true, ...(component ? { framework: 'react' } : {}), is_hook: /^use[A-Z]/.test(name) } }));
      nodes.push(emitted); existing.set(qn, emitted);
      const owner = callable.getAncestors().find(node => this.callables.has(node) || Node.isClassDeclaration(node));
      edges.push(this.work.createEdge(()=>({ sourceQN: owner ? this.declarations.get(owner)! : this.fileQN(callable), targetQN: qn,
        type: owner && Node.isClassDeclaration(owner) ? 'DEFINES_METHOD' : 'DEFINES' })));
    }
    for (const file of this.files) {
      this.work.charge(file.getFullWidth()+1);
      for (const attribute of file.getDescendantsOfKind(SyntaxKind.JsxAttribute)) {
        if (!/^on[A-Z]/.test(attribute.getNameNode().getText())) continue;
        const init = attribute.getInitializer();
        const target = init && Node.isJsxExpression(init) ? this.target(init.getExpression()) : undefined;
        if (target && callableQNs.has(target)) edges.push(this.work.createEdge(()=>({ sourceQN: this.owner(attribute), targetQN: target, type: 'CALLS', properties: { call_kind: 'event_handler' } })));
      }
      this.work.charge(file.getFullWidth()+1);
      for (const call of file.getDescendantsOfKind(SyntaxKind.CallExpression)) {
        const source = this.owner(call); const target = this.target(call.getExpression());
        let external: ReturnType<SemanticModel['externalReference']>;
        if (target && callableQNs.has(target)) edges.push(this.work.createEdge(()=>({ sourceQN: source, targetQN: target, type: 'CALLS' })));
        else if ((external = this.externalReference(call.getExpression()))) {
          const reference=external;
          if (!this.externalCallSymbols.has(reference.qualifiedName)) this.externalCallSymbols.set(reference.qualifiedName, this.work.createNode(()=>({
            label: 'ExternalSymbol', name: [reference.properties.export_name, reference.properties.member_path].filter(Boolean).join('.'), qualifiedName: reference.qualifiedName,
            filePath: this.relative(call), startLine: 0, endLine: 0, properties: reference.properties,
          })));
          edges.push(this.work.createEdge(()=>({ sourceQN: source, targetQN: reference.qualifiedName, type: 'CALLS', properties: { call_kind: 'external_import' } })));
        }
        for (const arg of call.getArguments()) {
          const callback = this.target(arg);
          if (callback && callback !== source && callableQNs.has(callback)) edges.push(this.work.createEdge(()=>({ sourceQN: source, targetQN: callback, type: 'CALLS', properties: { call_kind: 'callback' } })));
        }
        if (!target && !external && unresolved.length < 100 && !this.reactApi(call)) unresolved.push({ callerQN: source, calleeName: call.getExpression().getText().slice(0, 160), confidence: 0 });
      }
    }
  }
  reconcilePackageExports(nodes: GraphNodeDto[]): void {
    // Rendering may already have materialized the same npm symbol. Keep a single declaration
    // owner instead of emitting conflicting node copies for call-plus-render references.
    const present = new Set(nodes.map(node => node.qualifiedName));
    for (const [qn, node] of this.externalCallSymbols) if (!present.has(qn)) nodes.push(node);
    addNpmExportMetadata(this, nodes);
  }
  reconcileAngular(nodes: GraphNodeDto[], edges: PendingEdgeDto[]): void {
    const angular=new Set(['INHERITS','IMPLEMENTS','INJECTS','HANDLES']); const remove=new Set<PendingEdgeDto>();
    const routes=new Map<string,GraphNodeDto>(); const components=new Map<SourceFile,Map<string,Node>>();
    for(const node of nodes) { this.work.text(node.qualifiedName); if(!routes.has(node.qualifiedName)) routes.set(node.qualifiedName,node); }
    const componentExpression=(file:SourceFile,text:string):Node|undefined=>{
      this.work.text(text); let index=components.get(file);
      if(!index) { index=new Map(); this.work.charge(file.getFullWidth()+1);
        for(const property of file.getDescendantsOfKind(SyntaxKind.PropertyAssignment)) {
          this.work.charge(property.getNameNode().getFullWidth()+1); if(property.getName()!=='component') continue;
          const value=property.getInitializer(); if(!value) continue; this.work.charge(value.getFullWidth()+1); const key=value.getText(); if(!index.has(key)) index.set(key,value);
        }
        components.set(file,index);
      }
      return index.get(text);
    };
    for(let index=edges.length-1;index>=0;index--) {
      this.work.charge(); const edge=edges[index]; if(!angular.has(edge.type)||edge.properties?.framework==='react') continue;
      this.work.text(edge.sourceQN); const source=this.byQN.get(edge.sourceQN); let expression:Node|undefined;
      if(source&&Node.isClassDeclaration(source)) {
        if(edge.type==='INHERITS') expression=this.baseExpression(source);
        if(edge.type==='IMPLEMENTS') expression=this.implementedExpression(source,edge.targetQN);
        if(edge.type==='INJECTS') {
          const rawName=edge.properties?.parameter_name; const name=String(rawName); expression=typeof rawName==='string'?this.constructorParameter(source,rawName)?.getTypeNode():undefined;
          if(!expression) { const initializer=this.classProperty(source,name)?.getInitializer(); if(initializer&&Node.isCallExpression(initializer)) expression=initializer.getArguments()[0]; }
        }
      }
      if(edge.type==='HANDLES') { const route=routes.get(edge.sourceQN); const file=route?this.byPath.get(route.filePath):undefined; if(file) expression=componentExpression(file,edge.targetQN); }
      const target=this.target(expression); if(target) edge.targetQN=target; else remove.add(edge);
    }
    if(remove.size) { let write=0; for(const edge of edges) { this.work.charge(); if(!remove.has(edge)) edges[write++]=edge; } edges.length=write; }
  }
}
