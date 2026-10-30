"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.addNpmExportMetadata = addNpmExportMetadata;
const ts_morph_1 = require("ts-morph");
/** Explicit public aliases attach to their actual declarations, so consumers in another indexed
 * repository can bind exact package+subpath+export identities without guessing display names. */
function addNpmExportMetadata(model, nodes) {
    const work = model.work;
    const byQn = new Map();
    for (const node of nodes) {
        work.text(node.qualifiedName);
        byQn.set(node.qualifiedName, node);
    }
    const identities = new Map();
    const membersCache = new Map();
    const unknownStars = new Map();
    const members = (declaration, seen = new Set(), prefix = '') => {
        work.charge(seen.size + prefix.length + 1);
        if (seen.has(declaration) || seen.size > 12)
            return undefined;
        seen = new Set(seen);
        seen.add(declaration);
        if (ts_morph_1.Node.isParenthesizedExpression(declaration) || ts_morph_1.Node.isAsExpression(declaration) || ts_morph_1.Node.isNonNullExpression(declaration))
            return members(declaration.getExpression(), seen, prefix);
        if (ts_morph_1.Node.isIdentifier(declaration)) {
            const bindings = declaration.getSymbol()?.getDeclarations() ?? [];
            if (bindings.length === 1 && ts_morph_1.Node.isVariableDeclaration(bindings[0]))
                return members(bindings[0], seen, prefix);
            if (bindings.length === 1 && (ts_morph_1.Node.isImportSpecifier(bindings[0]) || ts_morph_1.Node.isImportClause(bindings[0]))) {
                const binding = bindings[0];
                const imported = binding.getFirstAncestorByKind(ts_morph_1.SyntaxKind.ImportDeclaration);
                const file = model.module(declaration, imported.getModuleSpecifierValue());
                const target = file && model.exported(file, ts_morph_1.Node.isImportSpecifier(binding) ? binding.getName() : 'default');
                return target ? members(target, seen, prefix) : undefined;
            }
        }
        if (ts_morph_1.Node.isVariableDeclaration(declaration)) {
            if (declaration.getVariableStatement()?.getDeclarationKind() !== 'const' || model.hasMemberWrites(declaration))
                return undefined;
            const init = declaration.getInitializer();
            if (!init)
                return undefined;
            return members(init, seen, prefix);
        }
        if (!ts_morph_1.Node.isObjectLiteralExpression(declaration)) {
            if ((ts_morph_1.Node.isPropertyAccessExpression(declaration) || ts_morph_1.Node.isElementAccessExpression(declaration))
                && model.hasMemberWrites(declaration.getExpression()))
                return undefined;
            const value = model.resolve(declaration);
            return value && value !== declaration && (ts_morph_1.Node.isObjectLiteralExpression(value) || ts_morph_1.Node.isVariableDeclaration(value))
                ? members(value, seen, prefix) : new Map();
        }
        // Resolution may return an initializer through an import/export alias. Its original
        // binding and object identity still need write checks before nested members are read.
        const owner = declaration.getParent();
        if ((owner && ts_morph_1.Node.isVariableDeclaration(owner) && (owner.getVariableStatement()?.getDeclarationKind() !== 'const'
            || model.hasMemberWrites(owner))) || model.hasMemberWrites(declaration))
            return undefined;
        const result = new Map();
        work.charge(declaration.compilerNode.properties.length + 1);
        for (const property of declaration.getProperties()) {
            work.charge();
            if (!(ts_morph_1.Node.isPropertyAssignment(property) || ts_morph_1.Node.isMethodDeclaration(property) || ts_morph_1.Node.isShorthandPropertyAssignment(property)))
                return undefined;
            work.charge(property.getNameNode().getFullWidth() + 1);
            const key = property.getName().replace(/^['"]|['"]$/g, '');
            if (!/^[a-zA-Z_$][\w$]*$/.test(key))
                return undefined;
            work.charge(prefix.length + key.length + 1);
            const member = prefix ? `${prefix}.${key}` : key;
            const expression = ts_morph_1.Node.isPropertyAssignment(property) ? property.getInitializer()
                : ts_morph_1.Node.isShorthandPropertyAssignment(property) ? property.getValueSymbol()?.getDeclarations()[0] : property;
            if (!expression)
                return undefined;
            const nested = members(expression, seen, member);
            if (!nested)
                return undefined;
            for (const [path, qn] of nested) {
                work.charge(path.length + qn.length + 1);
                result.set(path, qn);
            }
            const qn = model.target(expression);
            if (qn && byQn.has(qn) && ['Component', 'Function', 'Method'].includes(byQn.get(qn).label))
                result.set(member, qn);
        }
        return result;
    };
    // Root-only caching: nested recursion depends on seen/prefix. Never memoize a cutoff from
    // another root, and clone before the caller adds its declaration identity to a surface.
    const surfaceFor = (declaration) => {
        work.charge();
        if (!membersCache.has(declaration))
            membersCache.set(declaration, members(declaration));
        const cached = membersCache.get(declaration);
        work.charge((cached?.size ?? 0) + 1);
        return cached ? new Map(cached) : undefined;
    };
    for (const module of nodes) {
        work.charge();
        if (module.properties.module_kind !== 'typescript_workspace_package')
            continue;
        const packageName = module.properties.package_name;
        const entries = module.properties.package_entrypoints;
        if (typeof packageName !== 'string' || !Array.isArray(entries))
            continue;
        work.text(packageName);
        work.charge(entries.length + 1);
        const byImport = new Map();
        for (const entry of entries) {
            work.charge();
            if (!entry || typeof entry.import_path !== 'string')
                continue;
            work.text(entry.import_path);
            if (typeof entry.file_path === 'string')
                work.text(entry.file_path);
            if (!byImport.has(entry.import_path))
                byImport.set(entry.import_path, []);
            byImport.get(entry.import_path).push(entry);
        }
        for (const [importPath, candidates] of byImport) {
            work.charge(candidates.length * 2 + 1);
            if (candidates.some(entry => !entry.file_path || !model.byPath.has(entry.file_path))) {
                work.text(importPath);
                model.diagnostic(`packageExportUnresolved: ${importPath} has an unadmitted or unresolved runtime entry`);
                continue;
            }
            const files = [...new Set(candidates.map(entry => model.byPath.get(entry.file_path)))];
            const publicNames = new Set();
            for (const file of files)
                for (const name of model.publicExportNames(file)) {
                    work.text(name);
                    publicNames.add(name);
                }
            for (const name of publicNames) {
                work.text(name);
                const surfaces = [];
                let complete = true;
                // Runtime conditions are alternatives, not independent packages. Every modeled entry
                // must resolve this name before any of its declaration identities can be advertised.
                for (const file of files) {
                    work.charge();
                    if (!model.publicExportNames(file).has(name)) {
                        work.charge(importPath.length + name.length + model.relative(file).length + 1);
                        model.diagnostic(`packageExportUnresolved: ${importPath}#${name} is missing from runtime entry ${model.relative(file)}`);
                        complete = false;
                        continue;
                    }
                    const index = model.exportIndex(file);
                    const explicit = index.explicit.has(name);
                    let unknownStar = unknownStars.get(file);
                    if (unknownStar === undefined) {
                        unknownStar = false;
                        for (const exported of index.stars) {
                            work.charge();
                            const specifier = exported.getModuleSpecifierValue();
                            if (specifier && !model.module(file, specifier))
                                unknownStar = true;
                        }
                        unknownStars.set(file, unknownStar);
                    }
                    if (!explicit && unknownStar) {
                        work.charge(importPath.length + name.length + 1);
                        model.diagnostic(`packageExportUnresolved: ${importPath}#${name} has an unresolved star-export alternative`);
                        complete = false;
                        continue;
                    }
                    const declaration = model.exported(file, name);
                    if (!declaration) {
                        work.charge(importPath.length + name.length + 1);
                        model.diagnostic(`packageExportUnresolved: ${importPath}#${name} has no admitted declaration`);
                        complete = false;
                        continue;
                    }
                    const surface = surfaceFor(declaration);
                    if (!surface) {
                        work.charge(importPath.length + name.length + 1);
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
                const paths = new Set();
                for (const surface of surfaces)
                    for (const member of surface.keys()) {
                        work.text(member);
                        paths.add(member);
                    }
                for (const member of paths) {
                    work.charge(member.length + surfaces.length + 1);
                    if (surfaces.some(surface => !surface.has(member))) {
                        work.charge(importPath.length + name.length + member.length + 1);
                        model.diagnostic(`packageExportUnresolved: ${importPath}#${name}${member ? `::${member}` : ''} is missing from a runtime entry`);
                        continue;
                    }
                    work.charge(packageName.length + importPath.length + name.length + member.length + 8);
                    const identity = { package_name: packageName, import_path: importPath, export_name: name,
                        ...(member ? { member_path: member } : {}) };
                    work.charge(surfaces.length + 1);
                    for (const qn of new Set(surfaces.map(surface => surface.get(member)))) {
                        work.charge(qn.length + packageName.length + importPath.length + name.length + member.length + 8);
                        if (!identities.has(qn))
                            identities.set(qn, new Map());
                        identities.get(qn).set(`${packageName}\0${importPath}\0${name}\0${member}`, identity);
                    }
                }
            }
        }
    }
    const output = new Map();
    for (const [qn, aliases] of identities) {
        work.charge(qn.length + aliases.size + 1);
        const values = [...aliases.entries()].sort(([a], [b]) => { work.charge(a.length + b.length + 1); return a < b ? -1 : a > b ? 1 : 0; }).map(([, identity]) => identity);
        output.set(qn, values);
    }
    work.charge(output.size + 1);
    for (const [qn, aliases] of output)
        byQn.get(qn).properties.npm_exports = aliases;
    work.charge(0);
}
