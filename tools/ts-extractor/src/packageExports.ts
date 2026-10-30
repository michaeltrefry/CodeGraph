import { SourceFile } from 'ts-morph';
import type { GraphNodeDto } from './protocol';
import type { SemanticModel } from './semanticModel';

/** Explicit public aliases attach to their actual declarations, so consumers in another indexed
 * repository can bind exact package+subpath+export identities without guessing display names. */
export function addNpmExportMetadata(model: SemanticModel, nodes: GraphNodeDto[]): void {
  const byQn = new Map(nodes.map(node => [node.qualifiedName, node]));
  const identities = new Map<string, Map<string, { package_name: string; import_path: string; export_name: string }>>();
  const names = (file: SourceFile, seen = new Set<SourceFile>()): Set<string> => {
    if (seen.has(file)) return new Set(); seen.add(file);
    const result = new Set<string>();
    for (const declaration of [...file.getFunctions(), ...file.getClasses(), ...file.getInterfaces(), ...file.getVariableDeclarations(), ...file.getEnums()]) {
      if (declaration.isDefaultExport()) result.add('default');
      else if (declaration.isExported() && declaration.getName()) result.add(declaration.getName()!);
    }
    if (file.getExportAssignments().length) result.add('default');
    for (const exported of file.getExportDeclarations()) {
      const named = exported.getNamedExports();
      if (named.length) for (const item of named) result.add(item.getAliasNode()?.getText() ?? item.getName());
      else if (exported.getModuleSpecifierValue()) {
        const target = model.module(file, exported.getModuleSpecifierValue()!);
        if (target) for (const name of names(target, new Set(seen))) if (name !== 'default') result.add(name);
      }
    }
    return result;
  };
  const namesByEntry = new Map<SourceFile, Set<string>>();
  for (const module of nodes.filter(node => node.properties.module_kind === 'typescript_workspace_package')) {
    const packageName = module.properties.package_name;
    const entries = module.properties.package_entrypoints;
    if (typeof packageName !== 'string' || !Array.isArray(entries)) continue;
    const byImport = new Map<string, Array<{ file_path?: string }>>();
    for (const entry of entries) {
      if (!entry || typeof entry.import_path !== 'string') continue;
      if (!byImport.has(entry.import_path)) byImport.set(entry.import_path, []);
      byImport.get(entry.import_path)!.push(entry);
    }
    for (const [importPath, candidates] of byImport) {
      if (candidates.some(entry => !entry.file_path || !model.byPath.has(entry.file_path))) {
        model.diagnostic(`packageExportUnresolved: ${importPath} has an unadmitted or unresolved runtime entry`);
        continue;
      }
      for (const entry of candidates) {
        const file = model.byPath.get(entry.file_path!)!;
        if (!namesByEntry.has(file)) namesByEntry.set(file, names(file));
        for (const name of namesByEntry.get(file)!) {
          const explicit = [...file.getFunctions(), ...file.getClasses(), ...file.getInterfaces(), ...file.getVariableDeclarations(), ...file.getEnums()]
            .some(declaration => declaration.isExported() && (declaration.isDefaultExport() ? name === 'default' : declaration.getName() === name))
            || file.getExportDeclarations().some(exported => exported.getNamedExports().some(item => (item.getAliasNode()?.getText() ?? item.getName()) === name));
          const unknownStar = file.getExportDeclarations().some(exported => !exported.getNamedExports().length
            && exported.getModuleSpecifierValue() && !model.module(file, exported.getModuleSpecifierValue()!));
          if (!explicit && unknownStar) { model.diagnostic(`packageExportUnresolved: ${importPath}#${name} has an unresolved star-export alternative`); continue; }
          const declaration = model.exported(file, name);
          const qn = declaration && model.declarations.get(declaration);
          if (!qn || !byQn.has(qn)) {
            model.diagnostic(`packageExportUnresolved: ${importPath}#${name} has no admitted declaration`);
            continue;
          }
          if (!identities.has(qn)) identities.set(qn, new Map());
          const identity = { package_name: packageName, import_path: importPath, export_name: name };
          identities.get(qn)!.set(`${packageName}\0${importPath}\0${name}`, identity);
        }
      }
    }
  }
  for (const [qn, aliases] of identities) byQn.get(qn)!.properties.npm_exports =
    [...aliases.entries()].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0).map(([, identity]) => identity);
}
