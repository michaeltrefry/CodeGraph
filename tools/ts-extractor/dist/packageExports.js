"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.addNpmExportMetadata = addNpmExportMetadata;
const ts_morph_1 = require("ts-morph");
/** Explicit public aliases attach to their actual declarations, so consumers in another indexed
 * repository can bind exact package+subpath+export identities without guessing display names. */
function addNpmExportMetadata(model, nodes) {
    const byQn = new Map(nodes.map(node => [node.qualifiedName, node]));
    const identities = new Map();
    const names = (file, seen = new Set()) => {
        if (seen.has(file))
            return new Set();
        seen.add(file);
        const result = new Set();
        for (const declaration of [...file.getFunctions(), ...file.getClasses(), ...file.getInterfaces(), ...file.getVariableDeclarations(), ...file.getEnums()]) {
            if (declaration.isDefaultExport())
                result.add('default');
            else if (declaration.isExported() && declaration.getName())
                result.add(declaration.getName());
        }
        if (file.getExportAssignments().length)
            result.add('default');
        for (const exported of file.getExportDeclarations()) {
            const named = exported.getNamedExports();
            if (named.length)
                for (const item of named)
                    result.add(item.getAliasNode()?.getText() ?? item.getName());
            else if (exported.getModuleSpecifierValue()) {
                const target = model.module(file, exported.getModuleSpecifierValue());
                if (target)
                    for (const name of names(target, new Set(seen)))
                        if (name !== 'default')
                            result.add(name);
            }
        }
        return result;
    };
    const members = (declaration, seen = new Set(), prefix = '') => {
        if (seen.has(declaration) || seen.size > 12)
            return undefined;
        seen = new Set(seen);
        seen.add(declaration);
        if (ts_morph_1.Node.isVariableDeclaration(declaration)) {
            if (declaration.getVariableStatement()?.getDeclarationKind() !== 'const' || model.hasMemberWrites(declaration))
                return undefined;
            const init = declaration.getInitializer();
            if (!init)
                return undefined;
            const value = model.resolve(init) ?? init;
            return members(value, seen, prefix);
        }
        if (!ts_morph_1.Node.isObjectLiteralExpression(declaration))
            return new Map();
        const result = new Map();
        for (const property of declaration.getProperties()) {
            if (!(ts_morph_1.Node.isPropertyAssignment(property) || ts_morph_1.Node.isMethodDeclaration(property) || ts_morph_1.Node.isShorthandPropertyAssignment(property)))
                return undefined;
            const key = property.getName().replace(/^['"]|['"]$/g, '');
            if (!/^[a-zA-Z_$][\w$]*$/.test(key))
                return undefined;
            const member = prefix ? `${prefix}.${key}` : key;
            const expression = ts_morph_1.Node.isPropertyAssignment(property) ? property.getInitializer()
                : ts_morph_1.Node.isShorthandPropertyAssignment(property) ? property.getValueSymbol()?.getDeclarations()[0] : property;
            if (!expression)
                return undefined;
            const resolved = model.resolve(expression) ?? expression;
            if (ts_morph_1.Node.isObjectLiteralExpression(resolved) || ts_morph_1.Node.isVariableDeclaration(resolved)) {
                const nested = members(resolved, seen, member);
                if (!nested)
                    return undefined;
                for (const [path, qn] of nested)
                    result.set(path, qn);
            }
            else {
                const qn = model.target(expression);
                if (qn && byQn.has(qn) && ['Component', 'Function', 'Method'].includes(byQn.get(qn).label))
                    result.set(member, qn);
            }
        }
        return result;
    };
    const namesByEntry = new Map();
    for (const module of nodes.filter(node => node.properties.module_kind === 'typescript_workspace_package')) {
        const packageName = module.properties.package_name;
        const entries = module.properties.package_entrypoints;
        if (typeof packageName !== 'string' || !Array.isArray(entries))
            continue;
        const byImport = new Map();
        for (const entry of entries) {
            if (!entry || typeof entry.import_path !== 'string')
                continue;
            if (!byImport.has(entry.import_path))
                byImport.set(entry.import_path, []);
            byImport.get(entry.import_path).push(entry);
        }
        for (const [importPath, candidates] of byImport) {
            if (candidates.some(entry => !entry.file_path || !model.byPath.has(entry.file_path))) {
                model.diagnostic(`packageExportUnresolved: ${importPath} has an unadmitted or unresolved runtime entry`);
                continue;
            }
            const files = [...new Set(candidates.map(entry => model.byPath.get(entry.file_path)))];
            for (const file of files)
                if (!namesByEntry.has(file))
                    namesByEntry.set(file, names(file));
            const publicNames = new Set(files.flatMap(file => [...namesByEntry.get(file)]));
            for (const name of publicNames) {
                const surfaces = [];
                let complete = true;
                // Runtime conditions are alternatives, not independent packages. Every modeled entry
                // must resolve this name before any of its declaration identities can be advertised.
                for (const file of files) {
                    if (!namesByEntry.get(file).has(name)) {
                        model.diagnostic(`packageExportUnresolved: ${importPath}#${name} is missing from runtime entry ${model.relative(file)}`);
                        complete = false;
                        continue;
                    }
                    const explicit = [...file.getFunctions(), ...file.getClasses(), ...file.getInterfaces(), ...file.getVariableDeclarations(), ...file.getEnums()]
                        .some(declaration => declaration.isExported() && (declaration.isDefaultExport() ? name === 'default' : declaration.getName() === name))
                        || file.getExportDeclarations().some(exported => exported.getNamedExports().some(item => (item.getAliasNode()?.getText() ?? item.getName()) === name));
                    const unknownStar = file.getExportDeclarations().some(exported => !exported.getNamedExports().length
                        && exported.getModuleSpecifierValue() && !model.module(file, exported.getModuleSpecifierValue()));
                    if (!explicit && unknownStar) {
                        model.diagnostic(`packageExportUnresolved: ${importPath}#${name} has an unresolved star-export alternative`);
                        complete = false;
                        continue;
                    }
                    const declaration = model.exported(file, name);
                    if (!declaration) {
                        model.diagnostic(`packageExportUnresolved: ${importPath}#${name} has no admitted declaration`);
                        complete = false;
                        continue;
                    }
                    const surface = members(declaration);
                    if (!surface) {
                        model.diagnostic(`packageExportUnresolved: ${importPath}#${name} has mutable or unsupported member definitions`);
                        complete = false;
                        continue;
                    }
                    const resolved = model.resolve(declaration) ?? declaration;
                    const qn = model.declarations.get(resolved);
                    if (qn && byQn.has(qn))
                        surface.set('', qn);
                    surfaces.push(surface);
                }
                if (!complete)
                    continue;
                const paths = new Set(surfaces.flatMap(surface => [...surface.keys()]));
                for (const member of paths) {
                    if (surfaces.some(surface => !surface.has(member))) {
                        model.diagnostic(`packageExportUnresolved: ${importPath}#${name}${member ? `::${member}` : ''} is missing from a runtime entry`);
                        continue;
                    }
                    const identity = { package_name: packageName, import_path: importPath, export_name: name,
                        ...(member ? { member_path: member } : {}) };
                    for (const qn of new Set(surfaces.map(surface => surface.get(member)))) {
                        if (!identities.has(qn))
                            identities.set(qn, new Map());
                        identities.get(qn).set(`${packageName}\0${importPath}\0${name}\0${member}`, identity);
                    }
                }
            }
        }
    }
    for (const [qn, aliases] of identities)
        byQn.get(qn).properties.npm_exports =
            [...aliases.entries()].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0).map(([, identity]) => identity);
}
