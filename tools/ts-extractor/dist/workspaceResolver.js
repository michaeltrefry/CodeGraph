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
exports.analyzeWorkspaceImports = analyzeWorkspaceImports;
const ts_morph_1 = require("ts-morph");
const path = __importStar(require("path"));
const ts = __importStar(require("typescript"));
const viteLibraryExports_1 = require("./viteLibraryExports");
const boundedTextReader_1 = require("./boundedTextReader");
const SKIP_DIRS = new Set(['node_modules', 'dist', 'build', 'coverage', '.git', '.angular']);
const MAX_TSCONFIGS = 200;
const MAX_WORKSPACE_PACKAGES = 500;
const MAX_IMPORT_RESOLUTIONS = 20000;
const MAX_BARREL_EXPORTS = 25;
function analyzeWorkspaceImports(repositoryName, rootPath, requestedTsconfigPath, sourceFiles, log = () => { }, scope, reader = new boundedTextReader_1.BoundedTextReader(rootPath, scope ? file => scope.includesFile(file) : undefined, {}, log)) {
    const diagnostics = [];
    const diag = (msg) => {
        diagnostics.push(msg);
        log(msg);
    };
    const normalizedRoot = normalizeAbsolute(rootPath);
    // With a discovery scope, manifests come from the admitted files only, never a disk walk.
    const findManifests = (dir, fileName, limit) => scope
        ? scope.absoluteFiles(file => path.posix.basename(file) === fileName &&
            !file.split('/').some(segment => SKIP_DIRS.has(segment)))
            .map(normalizeAbsolute)
            .filter(file => isInside(dir, file))
            .slice(0, limit)
        : findFilesByName(dir, fileName, limit, reader);
    const tsconfigs = discoverTsConfigs(normalizedRoot, requestedTsconfigPath, diag, findManifests, scope, reader);
    const fallbackConfig = pickFallbackConfig(tsconfigs, requestedTsconfigPath);
    const workspacePackages = discoverWorkspacePackages(repositoryName, normalizedRoot, tsconfigs, diag, findManifests, reader, scope).filter(pkg => !scope || scope.includesDirectory(pkg.relativeRoot));
    qualifyCollidingPackages(repositoryName, workspacePackages);
    const sourceFilesByPath = new Map();
    for (const sourceFile of sourceFiles) {
        sourceFilesByPath.set(normalizeAbsolute(sourceFile.getFilePath()), sourceFile);
    }
    const ctx = {
        repositoryName,
        rootPath: normalizedRoot,
        tsconfigs,
        fallbackConfig,
        workspacePackages,
        packagesByName: groupPackagesByName(workspacePackages),
        sourceFilesByPath,
        moduleHost: {
            fileExists: file => sourceFilesByPath.has(normalizeAbsolute(file)) ||
                (/(?:package|[tj]sconfig[^/]*)\.json$/.test(file) && reader.exists(file)),
            readFile: file => reader.read(file),
            directoryExists: directory => scope ? scope.includesDirectory(toRelative(normalizedRoot, directory)) : reader.directoryExists(directory),
            realpath: file => file,
            getCurrentDirectory: () => normalizedRoot,
        },
        diagnostics,
        reader,
    };
    const nodes = buildWorkspacePackageNodes(workspacePackages, ctx);
    const edges = buildWorkspacePackageContainmentEdges(repositoryName, workspacePackages);
    const resolvedImports = [];
    const unresolvedImports = [];
    let resolutionCount = 0;
    for (const sourceFile of sourceFiles) {
        const absPath = normalizeAbsolute(sourceFile.getFilePath());
        const relPath = toRelative(rootPath, absPath);
        if (shouldSkipRelativePath(relPath))
            continue;
        const fileQN = `${repositoryName}:${relPath}`;
        const sourcePackage = findOwningPackage(workspacePackages, absPath);
        const specs = collectImportSpecs(sourceFile);
        for (const spec of specs) {
            if (resolutionCount >= MAX_IMPORT_RESOLUTIONS) {
                diag(`moduleResolution: stopped after ${MAX_IMPORT_RESOLUTIONS} import specifiers`);
                break;
            }
            resolutionCount++;
            const resolved = resolveImportSpec(ctx, absPath, fileQN, relPath, sourcePackage, spec);
            resolvedImports.push(resolved);
            addGraphEdgesForResolvedImport(edges, repositoryName, fileQN, sourcePackage, resolved);
            if (resolved.classification === 'unresolved_alias' ||
                resolved.classification === 'unsupported_dynamic_expression') {
                unresolvedImports.push({
                    fileQN,
                    importedNamespace: resolved.importedNamespace,
                });
            }
        }
    }
    nodes.push(...buildExternalPackageNodes(resolvedImports));
    diag(`workspaceDiscovery: ${workspacePackages.length} packages, ${tsconfigs.length} tsconfig files`);
    diag(`moduleResolution: ${resolvedImports.length} import/export/dynamic specifiers`);
    return {
        nodes,
        edges: dedupeEdges(edges),
        workspacePackages: workspacePackages.map(toWorkspacePackageDto),
        resolvedImports,
        unresolvedImports,
        diagnostics,
    };
}
function discoverTsConfigs(rootPath, requestedTsconfigPath, diag, findManifests, scope, reader) {
    const discovered = [...findManifests(rootPath, 'tsconfig.json', MAX_TSCONFIGS + 1), ...findManifests(rootPath, 'jsconfig.json', MAX_TSCONFIGS + 1)];
    const normalizedRequested = normalizeAbsolute(requestedTsconfigPath);
    if (!discovered.includes(normalizedRequested) && (!scope || scope.includesFile(normalizedRequested)) && reader.exists(normalizedRequested)) {
        discovered.unshift(normalizedRequested);
    }
    if (discovered.length > MAX_TSCONFIGS) {
        diag(`tsconfigDiscovery: limited from ${discovered.length} to ${MAX_TSCONFIGS} files`);
    }
    const configs = [];
    for (const configPath of discovered.slice(0, MAX_TSCONFIGS)) {
        const parsed = parseTsConfig(configPath, rootPath, diag, scope, reader);
        if (parsed)
            configs.push(parsed);
    }
    if (configs.length === 0) {
        configs.push({
            path: normalizedRequested,
            dir: path.dirname(normalizedRequested),
            relativePath: toRelative(rootPath, normalizedRequested),
            options: {
                allowJs: true,
                skipLibCheck: true,
                noResolve: true,
                moduleResolution: ts.ModuleResolutionKind.NodeJs,
                target: ts.ScriptTarget.ES2020,
            },
            references: [],
        });
    }
    configs.sort((a, b) => b.dir.length - a.dir.length);
    return configs;
}
function parseTsConfig(configPath, rootPath, diag, scope, reader) {
    const host = {
        useCaseSensitiveFileNames: ts.sys.useCaseSensitiveFileNames,
        getCurrentDirectory: ts.sys.getCurrentDirectory,
        fileExists: file => reader.exists(file),
        readFile: file => reader.read(file),
        // With a discovery scope, include/exclude globs see the admitted files; without one the
        // config's file list is never walked (sources are discovered separately).
        readDirectory: scope ? scopedReadDirectory(scope) : () => [],
        onUnRecoverableConfigFileDiagnostic: diagnostic => diag(`tsconfig ${toRelative(rootPath, configPath)}: ${flattenDiagnostic(diagnostic)}`),
    };
    const parsed = ts.getParsedCommandLineOfConfigFile(configPath, {
        noResolve: true,
        skipLibCheck: true,
        allowJs: true,
        types: [],
    }, host);
    if (!parsed)
        return null;
    for (const error of parsed.errors) {
        // Without a scope no directory is read, so "No inputs were found" would always be false.
        if (!scope && error.code === NO_INPUTS_FOUND_DIAGNOSTIC)
            continue;
        diag(`tsconfig ${toRelative(rootPath, configPath)}: ${flattenDiagnostic(error)}`);
    }
    const relativePath = toRelative(rootPath, configPath);
    const references = (parsed.projectReferences ?? [])
        .map(reference => toRelative(rootPath, normalizeAbsolute(reference.path)));
    return {
        path: normalizeAbsolute(configPath),
        dir: normalizeAbsolute(path.dirname(configPath)),
        relativePath,
        options: parsed.options,
        references,
        baseUrl: parsed.options.baseUrl ? toRelative(rootPath, parsed.options.baseUrl) : undefined,
        paths: parsed.options.paths,
        rootDirs: parsed.options.rootDirs?.map(dir => toRelative(rootPath, dir)),
        moduleResolution: moduleResolutionName(parsed.options.moduleResolution),
    };
}
/** TS18003: "No inputs were found in config file ...". */
const NO_INPUTS_FOUND_DIAGNOSTIC = 18003;
/**
 * A `readDirectory` that lists only the files of the discovery scope, matched against a
 * tsconfig's include/exclude globs with TypeScript's own matcher (as `ts.sys.readDirectory` does
 * against the disk), so a config whose sources are admitted is not reported as having no inputs.
 */
function scopedReadDirectory(scope) {
    const matchFiles = ts.matchFiles;
    return (rootDir, extensions, excludes, includes, depth) => {
        if (typeof matchFiles === 'function') {
            return matchFiles(rootDir, extensions, excludes, includes, ts.sys.useCaseSensitiveFileNames, ts.sys.getCurrentDirectory(), depth, dir => scope.fileSystemEntries(dir), p => p);
        }
        // Fallback should the internal matcher disappear: admitted files below rootDir by extension.
        const root = normalizeAbsolute(rootDir);
        return scope.absoluteFiles()
            .map(normalizeAbsolute)
            .filter(file => isInside(root, file) && extensions.some(extension => file.endsWith(extension)));
    };
}
function pickFallbackConfig(configs, requestedTsconfigPath) {
    const requested = normalizeAbsolute(requestedTsconfigPath);
    return configs.find(config => config.path === requested) ?? configs[0];
}
function discoverWorkspacePackages(repositoryName, rootPath, tsconfigs, diag, findManifests, reader, scope) {
    const packagesByRoot = new Map();
    const packageJsonPaths = findManifests(rootPath, 'package.json', MAX_WORKSPACE_PACKAGES + 1);
    if (packageJsonPaths.length > MAX_WORKSPACE_PACKAGES) {
        diag(`workspaceDiscovery: limited package.json scan from ${packageJsonPaths.length} to ${MAX_WORKSPACE_PACKAGES}`);
    }
    const rootPackageJson = path.join(rootPath, 'package.json');
    const workspacePatterns = [
        ...readPackageJsonWorkspacePatterns(rootPackageJson, reader),
        ...readPnpmWorkspacePatterns(path.join(rootPath, 'pnpm-workspace.yaml'), reader),
    ];
    const workspaceRoots = expandWorkspacePatterns(rootPath, workspacePatterns, findManifests, reader, scope);
    for (const packageJsonPath of packageJsonPaths.slice(0, MAX_WORKSPACE_PACKAGES)) {
        const pkg = readJsonFile(packageJsonPath, reader);
        const packageName = typeof pkg?.name === 'string' ? pkg.name : null;
        const packageRoot = normalizeAbsolute(path.dirname(packageJsonPath));
        const isRoot = packageRoot === rootPath;
        const matchesWorkspace = workspaceRoots.some(root => root === packageRoot);
        if (!packageName && !matchesWorkspace && !isRoot)
            continue;
        const inferredName = packageName ?? path.basename(packageRoot);
        addWorkspacePackage(packagesByRoot, {
            name: inferredName,
            qualifiedName: buildWorkspacePackageQN(repositoryName, inferredName),
            rootPath: packageRoot,
            relativeRoot: toRelative(rootPath, packageRoot),
            kind: isRoot ? 'root_package' : 'workspace_package',
            packageJsonPath: toRelative(rootPath, packageJsonPath),
            tsconfigPath: findNearestTsConfigForRoot(tsconfigs, packageRoot, rootPath),
        });
    }
    addAngularWorkspacePackages(repositoryName, rootPath, packagesByRoot, reader);
    for (const config of tsconfigs) {
        for (const reference of config.references) {
            const referencedPath = normalizeAbsolute(path.join(rootPath, reference));
            const referencedRoot = path.basename(referencedPath).startsWith('tsconfig.')
                ? normalizeAbsolute(path.dirname(referencedPath))
                : referencedPath;
            if (!isInside(rootPath, referencedRoot))
                continue;
            addWorkspacePackage(packagesByRoot, {
                name: path.basename(referencedRoot),
                qualifiedName: buildWorkspacePackageQN(repositoryName, path.basename(referencedRoot)),
                rootPath: referencedRoot,
                relativeRoot: toRelative(rootPath, referencedRoot),
                kind: 'project_reference',
                tsconfigPath: findNearestTsConfigForRoot(tsconfigs, referencedRoot, rootPath),
            });
        }
    }
    return [...packagesByRoot.values()]
        .sort((a, b) => a.relativeRoot.localeCompare(b.relativeRoot));
}
function addWorkspacePackage(packagesByRoot, candidate) {
    const existing = packagesByRoot.get(candidate.rootPath);
    if (!existing) {
        packagesByRoot.set(candidate.rootPath, candidate);
        return;
    }
    packagesByRoot.set(candidate.rootPath, {
        ...existing,
        name: existing.name || candidate.name,
        qualifiedName: existing.qualifiedName || candidate.qualifiedName,
        kind: mergePackageKinds(existing.kind, candidate.kind),
        packageJsonPath: existing.packageJsonPath ?? candidate.packageJsonPath,
        tsconfigPath: existing.tsconfigPath ?? candidate.tsconfigPath,
    });
}
function addAngularWorkspacePackages(repositoryName, rootPath, packagesByRoot, reader) {
    for (const workspaceFile of ['angular.json', 'workspace.json']) {
        const workspacePath = path.join(rootPath, workspaceFile);
        const workspace = readJsonFile(workspacePath, reader);
        const projects = workspace?.projects;
        if (!projects || typeof projects !== 'object')
            continue;
        for (const [name, value] of Object.entries(projects)) {
            if (!value || typeof value !== 'object')
                continue;
            const project = value;
            const rawRoot = typeof project.root === 'string' ? project.root : null;
            const rawSourceRoot = typeof project.sourceRoot === 'string' ? project.sourceRoot : null;
            const root = normalizeAbsolute(path.join(rootPath, rawRoot || rawSourceRoot || name));
            if (!isInside(rootPath, root))
                continue;
            const projectType = typeof project.projectType === 'string'
                ? project.projectType
                : root.includes('/libs/') || root.startsWith(path.join(rootPath, 'libs')) ? 'library' : 'application';
            addWorkspacePackage(packagesByRoot, {
                name,
                qualifiedName: buildWorkspacePackageQN(repositoryName, name),
                rootPath: root,
                relativeRoot: toRelative(rootPath, root),
                kind: projectType === 'library' ? 'angular_library' : 'angular_application',
                tsconfigPath: undefined,
            });
        }
    }
}
function buildWorkspacePackageNodes(packages, ctx) {
    return packages.map(pkg => ({
        label: 'Module',
        name: pkg.name,
        qualifiedName: pkg.qualifiedName,
        filePath: pkg.relativeRoot,
        startLine: 0,
        endLine: 0,
        properties: {
            module_kind: 'typescript_workspace_package',
            package_name: pkg.name,
            package_entrypoints: packageEntryPoints(pkg, ctx),
            workspace_kind: pkg.kind,
            root_path: pkg.relativeRoot,
            ...(pkg.packageJsonPath ? { package_json_path: pkg.packageJsonPath } : {}),
            ...(pkg.tsconfigPath ? { tsconfig_path: pkg.tsconfigPath } : {}),
        },
    }));
}
/** Public package entry identities, resolved only against already admitted ASTs. No dist or
 * node_modules reads are needed. An explicit compiler rootDir/outDir mapping can prove where a
 * manifest's generated JS entry originates; missing/alternative targets remain visible. */
function packageEntryPoints(pkg, ctx) {
    if (!pkg.packageJsonPath)
        return [];
    const manifest = readJsonFile(path.join(ctx.rootPath, pkg.packageJsonPath), ctx.reader);
    if (!manifest || typeof manifest.name !== 'string' || manifest.name !== pkg.name)
        return [];
    const entries = new Map();
    const runtimeTargets = (value) => {
        if (typeof value === 'string')
            return [value];
        if (Array.isArray(value))
            return value.flatMap(runtimeTargets);
        if (!value || typeof value !== 'object')
            return [];
        return Object.entries(value).filter(([condition]) => condition !== 'types').flatMap(([, target]) => runtimeTargets(target));
    };
    if (manifest.exports !== undefined) {
        const exports = manifest.exports;
        if (exports && typeof exports === 'object' && !Array.isArray(exports) && Object.keys(exports).some(key => key.startsWith('.'))) {
            for (const [subpath, target] of Object.entries(exports))
                if (subpath === '.' || subpath.startsWith('./'))
                    entries.set(subpath, runtimeTargets(target));
        }
        else
            entries.set('.', runtimeTargets(exports));
    }
    else {
        const targets = [manifest.source, manifest.module, manifest.main].filter((value) => typeof value === 'string');
        entries.set('.', targets.length ? targets : ['./index']);
    }
    const config = ctx.tsconfigs.find(config => config.relativePath === pkg.tsconfigPath);
    const emittedPath = (source, output) => {
        if (!config?.options.rootDir || !isInside(normalizeAbsolute(config.options.rootDir), source))
            return undefined;
        const jsx = /\.[jt]sx$/.test(source);
        if (jsx && config.options.jsx === undefined)
            return undefined;
        const extension = jsx && config.options.jsx === ts.JsxEmit.Preserve ? '.jsx' : '.js';
        return normalizeAbsolute(path.join(output, path.relative(config.options.rootDir, source))).replace(/\.[jt]sx?$/, extension);
    };
    const sourceCandidates = (absolute) => {
        if (ctx.sourceFilesByPath.has(absolute))
            return [absolute];
        const candidates = (0, viteLibraryExports_1.viteLibrarySource)(pkg.rootPath, ctx.sourceFilesByPath, absolute, message => ctx.diagnostics.push(message));
        if (!path.extname(absolute))
            candidates.push(...['.ts', '.tsx', '.js', '.jsx'].map(extension => absolute + extension), ...['index.ts', 'index.tsx', 'index.js', 'index.jsx'].map(index => path.join(absolute, index)));
        if (config?.options.rootDir && config.options.outDir && isInside(normalizeAbsolute(config.options.outDir), absolute)) {
            const stem = normalizeAbsolute(path.join(config.options.rootDir, path.relative(config.options.outDir, absolute))).replace(/\.[jt]sx?$/, '');
            for (const extension of ['.ts', '.tsx', '.js', '.jsx']) {
                const source = stem + extension;
                if (emittedPath(source, config.options.outDir) === absolute)
                    candidates.push(source);
            }
        }
        return [...new Set(candidates)].filter(candidate => isInside(ctx.rootPath, candidate) && ctx.sourceFilesByPath.has(candidate));
    };
    const result = [];
    for (const [subpath, targets] of entries) {
        const importPath = subpath === '.' ? pkg.name : `${pkg.name}${subpath.slice(1)}`;
        if (!targets.length) {
            result.push({ import_path: importPath });
            continue;
        }
        const distinctTargets = [...new Set(targets)];
        const wildcardTargets = new Map();
        for (const target of distinctTargets) {
            if (!target.startsWith('./') && manifest.exports !== undefined) {
                result.push({ import_path: importPath });
                continue;
            }
            const absolute = normalizeAbsolute(path.join(pkg.rootPath, target));
            if (!isInside(pkg.rootPath, absolute)) {
                result.push({ import_path: importPath });
                continue;
            }
            if (subpath.includes('*') || target.includes('*')) {
                // Expand a single public wildcard from the admitted source universe, never a filesystem glob.
                if (subpath.split('*').length !== 2 || target.split('*').length !== 2) {
                    result.push({ import_path: importPath });
                    continue;
                }
                const [prefix, suffix] = absolute.split('*');
                for (const admitted of ctx.sourceFilesByPath.keys()) {
                    const emitted = [admitted];
                    if (config?.options.rootDir && config.options.outDir && isInside(normalizeAbsolute(config.options.rootDir), admitted)) {
                        const compiled = emittedPath(admitted, config.options.outDir);
                        if (compiled)
                            emitted.push(compiled);
                    }
                    for (const candidate of emitted)
                        if (candidate.startsWith(prefix) && candidate.endsWith(suffix)) {
                            const wildcard = candidate.slice(prefix.length, candidate.length - suffix.length || undefined);
                            const publicPath = importPath.replace('*', wildcard);
                            result.push({ import_path: publicPath, file_path: toRelative(ctx.rootPath, admitted) });
                            if (!wildcardTargets.has(publicPath))
                                wildcardTargets.set(publicPath, new Set());
                            wildcardTargets.get(publicPath).add(target);
                        }
                }
            }
            else {
                const candidates = sourceCandidates(absolute);
                if (!candidates.length)
                    result.push({ import_path: importPath });
                for (const candidate of candidates)
                    result.push({ import_path: importPath, file_path: toRelative(ctx.rootPath, candidate) });
            }
        }
        if (subpath.includes('*'))
            for (const [publicPath, resolvedTargets] of wildcardTargets)
                if (resolvedTargets.size !== distinctTargets.length)
                    result.push({ import_path: publicPath });
    }
    return [...new Map(result.map(entry => [`${entry.import_path}\0${entry.file_path ?? ''}`, entry])).values()]
        .sort((a, b) => a.import_path.localeCompare(b.import_path) || (a.file_path ?? '').localeCompare(b.file_path ?? ''));
}
function buildWorkspacePackageContainmentEdges(repositoryName, packages) {
    return packages.map(pkg => ({
        sourceQN: repositoryName,
        targetQN: pkg.qualifiedName,
        type: 'CONTAINS_PROJECT',
        properties: {
            workspace_kind: pkg.kind,
            root_path: pkg.relativeRoot,
        },
    }));
}
function buildExternalPackageNodes(resolvedImports) {
    const packages = new Map();
    for (const resolved of resolvedImports) {
        if (resolved.classification !== 'external_npm_package' ||
            !resolved.targetPackageQN ||
            !resolved.externalPackageName) {
            continue;
        }
        packages.set(resolved.targetPackageQN, resolved.externalPackageName);
    }
    return [...packages.entries()].map(([qualifiedName, name]) => ({
        label: 'NuGetPackage',
        name,
        qualifiedName,
        filePath: '',
        startLine: 0,
        endLine: 0,
        properties: {
            package_type: 'npm',
        },
    }));
}
function collectImportSpecs(sourceFile) {
    const specs = [];
    for (const importDecl of sourceFile.getImportDeclarations()) {
        specs.push({ specifier: importDecl.getModuleSpecifierValue(), kind: 'static' });
    }
    for (const exportDecl of sourceFile.getExportDeclarations()) {
        const specifier = exportDecl.getModuleSpecifierValue();
        if (specifier)
            specs.push({ specifier, kind: 'export' });
    }
    for (const call of sourceFile.getDescendantsOfKind(ts_morph_1.SyntaxKind.CallExpression)) {
        if (call.getExpression().getKind() !== ts_morph_1.SyntaxKind.ImportKeyword)
            continue;
        const args = call.getArguments();
        const firstArg = args[0];
        if (!firstArg) {
            specs.push({ specifier: '<dynamic expression>', kind: 'dynamic' });
        }
        else if (ts_morph_1.Node.isStringLiteral(firstArg) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(firstArg)) {
            specs.push({ specifier: firstArg.getLiteralValue(), kind: 'dynamic' });
        }
        else {
            specs.push({ specifier: '<dynamic expression>', kind: 'dynamic' });
        }
    }
    // Angular's pre-import() lazy syntax still goes through the normal scoped module resolver.
    // Keep the export suffix out of the module specifier; route analysis resolves that export.
    for (const property of sourceFile.getDescendantsOfKind(ts_morph_1.SyntaxKind.PropertyAssignment)) {
        if (property.getName() !== 'loadChildren')
            continue;
        const initializer = property.getInitializer();
        if (initializer && (ts_morph_1.Node.isStringLiteral(initializer) || ts_morph_1.Node.isNoSubstitutionTemplateLiteral(initializer))) {
            const specifier = initializer.getLiteralText().split('#')[0];
            if (specifier)
                specs.push({ specifier, kind: 'dynamic' });
        }
    }
    return specs;
}
function resolveImportSpec(ctx, importerPath, fileQN, filePath, sourcePackage, spec) {
    if (spec.specifier === '<dynamic expression>') {
        return {
            fileQN,
            filePath,
            importedNamespace: spec.specifier,
            importKind: spec.kind,
            classification: 'unsupported_dynamic_expression',
            diagnostic: 'dynamic import expression is not a string literal',
        };
    }
    const config = findConfigForFile(ctx.tsconfigs, ctx.fallbackConfig, importerPath);
    // The compiler rejects upper-case JS/TS suffixes. An explicit relative specifier can
    // still name one exact admitted source; preserve its case without probing or guessing.
    const literalRelativeTarget = /^\.{1,2}\//.test(spec.specifier)
        ? normalizeAbsolute(path.resolve(path.dirname(importerPath), spec.specifier)) : undefined;
    const resolved = ts.resolveModuleName(spec.specifier, importerPath, config.options, ctx.moduleHost).resolvedModule ?? (literalRelativeTarget && ctx.sourceFilesByPath.has(literalRelativeTarget)
        ? { resolvedFileName: literalRelativeTarget } : undefined);
    const localPackage = findWorkspacePackageBySpecifier(ctx.packagesByName, spec.specifier, importerPath);
    if (!resolved && localPackage) {
        const exportedPath = resolveWorkspacePackageExport(localPackage, spec.specifier, ctx.reader);
        const exportedFilePath = exportedPath && ctx.sourceFilesByPath.has(normalizeAbsolute(exportedPath)) && isInside(ctx.rootPath, exportedPath)
            ? toRelative(ctx.rootPath, exportedPath)
            : undefined;
        return {
            fileQN,
            filePath,
            importedNamespace: spec.specifier,
            importKind: spec.kind,
            classification: 'local_workspace_package',
            resolvedFilePath: exportedFilePath,
            targetFileQN: exportedFilePath ? `${ctx.repositoryName}:${exportedFilePath}` : undefined,
            targetWorkspacePackage: localPackage.name,
            targetPackageQN: localPackage.qualifiedName,
            diagnostic: exportedFilePath
                ? undefined
                : 'resolved to workspace package name without a concrete file',
        };
    }
    if (!resolved) {
        const diagnostic = `unresolved import '${spec.specifier}' from ${filePath}`;
        const classification = spec.specifier.startsWith('.') || spec.specifier.startsWith('/') || looksLikeAlias(config, spec.specifier) ? 'unresolved_alias' : 'external_npm_package';
        if (classification === 'external_npm_package') {
            const packageName = getPackageNameFromSpecifier(spec.specifier);
            return {
                fileQN,
                filePath,
                importedNamespace: spec.specifier,
                importKind: spec.kind,
                classification,
                externalPackageName: packageName,
                targetPackageQN: buildNpmPackageQN(packageName),
                diagnostic,
            };
        }
        return {
            fileQN,
            filePath,
            importedNamespace: spec.specifier,
            importKind: spec.kind,
            classification,
            diagnostic,
        };
    }
    const resolvedPath = normalizeAbsolute(resolved.resolvedFileName);
    if (isNodeModulePath(resolvedPath)) {
        const packageName = getPackageNameFromSpecifier(spec.specifier);
        return {
            fileQN,
            filePath,
            importedNamespace: spec.specifier,
            importKind: spec.kind,
            classification: 'external_npm_package',
            resolvedFilePath: toRelative(ctx.rootPath, resolvedPath),
            externalPackageName: packageName,
            targetPackageQN: buildNpmPackageQN(packageName),
        };
    }
    if (isInside(ctx.rootPath, resolvedPath)) {
        const resolvedFilePath = toRelative(ctx.rootPath, resolvedPath);
        const targetPackage = findOwningPackage(ctx.workspacePackages, resolvedPath);
        const isCrossWorkspacePackage = !!targetPackage &&
            targetPackage.kind !== 'root_package' &&
            targetPackage.qualifiedName !== sourcePackage?.qualifiedName;
        const barrelTarget = findSingleBarrelExportTarget(ctx, resolvedPath);
        const targetFilePath = barrelTarget ?? resolvedFilePath;
        return {
            fileQN,
            filePath,
            importedNamespace: spec.specifier,
            importKind: spec.kind,
            classification: isCrossWorkspacePackage ? 'local_workspace_package' : 'local_file',
            resolvedFilePath,
            targetFileQN: `${ctx.repositoryName}:${targetFilePath}`,
            targetWorkspacePackage: isCrossWorkspacePackage ? targetPackage.name : undefined,
            targetPackageQN: isCrossWorkspacePackage ? targetPackage.qualifiedName : undefined,
            isBarrel: !!barrelTarget,
            barrelTargetFilePath: barrelTarget,
        };
    }
    const packageName = getPackageNameFromSpecifier(spec.specifier);
    return {
        fileQN,
        filePath,
        importedNamespace: spec.specifier,
        importKind: spec.kind,
        classification: 'external_npm_package',
        resolvedFilePath: resolvedPath,
        externalPackageName: packageName,
        targetPackageQN: buildNpmPackageQN(packageName),
    };
}
function addGraphEdgesForResolvedImport(edges, repositoryName, fileQN, sourcePackage, resolved) {
    const commonProperties = {
        imported_namespace: resolved.importedNamespace,
        import_kind: resolved.importKind,
        classification: resolved.classification,
        ...(resolved.resolvedFilePath ? { resolved_file_path: resolved.resolvedFilePath } : {}),
        ...(resolved.isBarrel ? { via_barrel: true, barrel_target_file_path: resolved.barrelTargetFilePath } : {}),
    };
    if (resolved.targetFileQN) {
        edges.push({
            sourceQN: fileQN,
            targetQN: resolved.targetFileQN,
            type: 'IMPORTS',
            properties: commonProperties,
        });
    }
    if (resolved.targetPackageQN && resolved.classification === 'local_workspace_package') {
        edges.push({
            sourceQN: fileQN,
            targetQN: resolved.targetPackageQN,
            type: 'IMPORTS',
            properties: commonProperties,
        });
        if (sourcePackage && sourcePackage.qualifiedName !== resolved.targetPackageQN) {
            edges.push({
                sourceQN: sourcePackage.qualifiedName,
                targetQN: resolved.targetPackageQN,
                type: 'IMPORTS',
                properties: commonProperties,
            });
        }
    }
    if (resolved.targetPackageQN && resolved.classification === 'external_npm_package') {
        const packageName = resolved.externalPackageName ?? resolved.importedNamespace;
        edges.push({
            sourceQN: fileQN,
            targetQN: resolved.targetPackageQN,
            type: 'REFERENCES_PACKAGE',
            properties: {
                package_type: 'npm',
                package_name: packageName,
                imported_namespace: resolved.importedNamespace,
            },
        });
        if (sourcePackage) {
            edges.push({
                sourceQN: sourcePackage.qualifiedName,
                targetQN: resolved.targetPackageQN,
                type: 'REFERENCES_PACKAGE',
                properties: {
                    package_type: 'npm',
                    package_name: packageName,
                },
            });
        }
        else {
            edges.push({
                sourceQN: repositoryName,
                targetQN: resolved.targetPackageQN,
                type: 'REFERENCES_PACKAGE',
                properties: {
                    package_type: 'npm',
                    package_name: packageName,
                },
            });
        }
    }
}
function findSingleBarrelExportTarget(ctx, resolvedPath) {
    const basename = path.basename(resolvedPath).toLowerCase();
    if (basename !== 'index.ts' && basename !== 'public-api.ts')
        return undefined;
    const sourceFile = ctx.sourceFilesByPath.get(resolvedPath);
    if (!sourceFile)
        return undefined;
    for (const exportDecl of sourceFile.getExportDeclarations().slice(0, MAX_BARREL_EXPORTS)) {
        const specifier = exportDecl.getModuleSpecifierValue();
        if (!specifier)
            continue;
        const config = findConfigForFile(ctx.tsconfigs, ctx.fallbackConfig, resolvedPath);
        const resolved = ts.resolveModuleName(specifier, resolvedPath, config.options, ctx.moduleHost).resolvedModule;
        if (!resolved)
            continue;
        const targetPath = normalizeAbsolute(resolved.resolvedFileName);
        if (targetPath === resolvedPath || !isInside(ctx.rootPath, targetPath))
            continue;
        return toRelative(ctx.rootPath, targetPath);
    }
    return undefined;
}
function findConfigForFile(configs, fallback, filePath) {
    const normalized = normalizeAbsolute(filePath);
    return configs.find(config => isInside(config.dir, normalized)) ?? fallback;
}
function findOwningPackage(packages, filePath) {
    const normalized = normalizeAbsolute(filePath);
    let best;
    for (const pkg of packages) {
        if (!isInside(pkg.rootPath, normalized))
            continue;
        if (!best || pkg.rootPath.length > best.rootPath.length)
            best = pkg;
    }
    return best;
}
function findWorkspacePackageBySpecifier(packagesByName, specifier, importerPath) {
    const exact = packagesByName.get(specifier);
    if (exact)
        return nearestPackage(exact, importerPath);
    for (const [name, packages] of packagesByName) {
        if (specifier.startsWith(`${name}/`))
            return nearestPackage(packages, importerPath);
    }
    return undefined;
}
/**
 * The same-named workspace package an importer means: the one containing it (a self import),
 * else the one sharing the longest directory prefix with it, else the first by directory.
 */
function nearestPackage(packages, importerPath) {
    if (packages.length <= 1)
        return packages[0];
    const importer = normalizeAbsolute(importerPath);
    let best;
    let bestScore = -1;
    for (const pkg of packages) {
        const score = isInside(pkg.rootPath, importer)
            ? Number.MAX_SAFE_INTEGER - (importer.length - pkg.rootPath.length)
            : sharedDirectoryDepth(pkg.rootPath, path.dirname(importer));
        if (score > bestScore) {
            best = pkg;
            bestScore = score;
        }
    }
    return best;
}
function sharedDirectoryDepth(left, right) {
    const a = left.split('/');
    const b = right.split('/');
    let depth = 0;
    while (depth < a.length && depth < b.length && a[depth] === b[depth])
        depth++;
    return depth;
}
function groupPackagesByName(packages) {
    const byName = new Map();
    for (const pkg of packages) {
        const group = byName.get(pkg.name);
        if (group)
            group.push(pkg);
        else
            byName.set(pkg.name, [pkg]);
    }
    return byName;
}
/**
 * Gives same-named workspace packages (a vendored copy of an app beside the original) distinct
 * qualified names that include their repo-relative directory; a package whose name is unique keeps
 * the name-only qualified name, so existing graphs are unaffected.
 */
function qualifyCollidingPackages(repositoryName, packages) {
    for (const group of groupPackagesByName(packages).values()) {
        if (group.length < 2)
            continue;
        for (const pkg of group) {
            pkg.qualifiedName = buildWorkspacePackageQN(repositoryName, pkg.name, pkg.relativeRoot);
        }
    }
}
function resolveWorkspacePackageExport(pkg, specifier, reader) {
    if (!pkg.packageJsonPath)
        return undefined;
    const packageJsonPath = path.join(pkg.rootPath, path.basename(pkg.packageJsonPath));
    const packageJson = readJsonFile(packageJsonPath, reader);
    if (!packageJson || typeof packageJson !== 'object')
        return undefined;
    const subpath = specifier === pkg.name
        ? '.'
        : `.${specifier.slice(pkg.name.length)}`;
    const target = resolvePackageExportTarget(packageJson.exports, subpath) ??
        (subpath === '.' ? firstString(packageJson.module, packageJson.main, packageJson.types) : undefined);
    if (!target)
        return undefined;
    return resolveFileWithExtensions(path.join(pkg.rootPath, target), reader);
}
function resolvePackageExportTarget(exportsValue, subpath) {
    if (typeof exportsValue === 'string' && subpath === '.')
        return exportsValue;
    if (!exportsValue || typeof exportsValue !== 'object')
        return undefined;
    const exportsObject = exportsValue;
    const candidate = exportsObject[subpath] ?? (subpath === '.' ? exportsObject['.'] : undefined);
    return resolveConditionalExport(candidate);
}
function resolveConditionalExport(value) {
    if (typeof value === 'string')
        return value;
    if (!value || typeof value !== 'object')
        return undefined;
    const objectValue = value;
    return firstString(objectValue.import, objectValue.default, objectValue.types, objectValue.require);
}
function firstString(...values) {
    return values.find((value) => typeof value === 'string');
}
function resolveFileWithExtensions(candidate, reader) {
    const normalized = normalizeAbsolute(candidate);
    if (reader.exists(normalized))
        return normalized;
    for (const suffix of ['.ts', '.tsx', '.js', '.jsx', '.d.ts']) {
        const withSuffix = `${normalized}${suffix}`;
        if (reader.exists(withSuffix))
            return withSuffix;
    }
    for (const indexName of ['index.ts', 'index.tsx', 'index.js', 'index.jsx']) {
        const indexPath = path.join(normalized, indexName);
        if (reader.exists(indexPath))
            return normalizeAbsolute(indexPath);
    }
    return undefined;
}
function findNearestTsConfigForRoot(tsconfigs, packageRoot, rootPath) {
    const exactConfig = tsconfigs.find(candidate => candidate.dir === packageRoot);
    if (exactConfig)
        return toRelative(rootPath, exactConfig.path);
    const config = tsconfigs
        .filter(candidate => isInside(packageRoot, candidate.path))
        .sort((a, b) => a.dir.length - b.dir.length)[0];
    return config ? toRelative(rootPath, config.path) : undefined;
}
function readPackageJsonWorkspacePatterns(packageJsonPath, reader) {
    const pkg = readJsonFile(packageJsonPath, reader);
    const workspaces = pkg?.workspaces;
    if (Array.isArray(workspaces))
        return workspaces.filter((entry) => typeof entry === 'string');
    if (workspaces && typeof workspaces === 'object' && Array.isArray(workspaces.packages)) {
        return workspaces.packages.filter((entry) => typeof entry === 'string');
    }
    return [];
}
function readPnpmWorkspacePatterns(workspacePath, reader) {
    const text = reader.read(workspacePath);
    if (text === undefined)
        return [];
    const lines = text.split(/\r?\n/);
    const patterns = [];
    let inPackages = false;
    for (const line of lines) {
        const trimmed = line.trim();
        if (trimmed === 'packages:') {
            inPackages = true;
            continue;
        }
        if (!inPackages)
            continue;
        if (trimmed.length === 0 || trimmed.startsWith('#'))
            continue;
        if (!trimmed.startsWith('-'))
            break;
        const pattern = trimmed.slice(1).trim().replace(/^['"]|['"]$/g, '');
        if (pattern && !pattern.startsWith('!'))
            patterns.push(pattern);
    }
    return patterns;
}
function expandWorkspacePatterns(rootPath, patterns, findManifests, reader, scope) {
    const roots = new Set();
    for (const pattern of patterns) {
        const normalized = pattern.replace(/\\/g, '/').replace(/\/package\.json$/, '');
        if (normalized.includes('**')) {
            const prefix = normalized.slice(0, normalized.indexOf('**')).replace(/\/$/, '');
            for (const packageJson of findManifests(path.join(rootPath, prefix), 'package.json', MAX_WORKSPACE_PACKAGES)) {
                roots.add(normalizeAbsolute(path.dirname(packageJson)));
            }
            continue;
        }
        if (normalized.includes('*')) {
            const prefix = normalized.slice(0, normalized.indexOf('*')).replace(/\/$/, '');
            const parent = path.join(rootPath, prefix);
            const directories = scope ? scope.fileSystemEntries(parent).directories : reader.entries(parent).filter(entry => entry.isDirectory()).map(entry => entry.name);
            for (const directory of directories)
                roots.add(normalizeAbsolute(path.join(parent, directory)));
            continue;
        }
        roots.add(normalizeAbsolute(path.join(rootPath, normalized)));
    }
    return [...roots];
}
function findFilesByName(dir, fileName, limit, reader) {
    const results = [];
    if (!reader.directoryExists(dir))
        return results;
    const walk = (current) => {
        if (results.length >= limit)
            return;
        for (const entry of reader.entries(current)) {
            if (results.length >= limit)
                return;
            const full = path.join(current, entry.name);
            if (entry.isDirectory()) {
                if (!SKIP_DIRS.has(entry.name))
                    walk(full);
            }
            else if (entry.isFile() && entry.name === fileName) {
                results.push(normalizeAbsolute(full));
            }
        }
    };
    walk(dir);
    return results;
}
function readJsonFile(filePath, reader) {
    try {
        const text = reader.read(filePath);
        return text === undefined ? null : JSON.parse(text);
    }
    catch {
        return null;
    }
}
function looksLikeAlias(config, specifier) {
    if (specifier.startsWith('.') || specifier.startsWith('/'))
        return false;
    const pathKeys = Object.keys(config.paths ?? {});
    return pathKeys.some(key => {
        const prefix = key.replace(/\*.*$/, '');
        return prefix.length > 0 && specifier.startsWith(prefix);
    });
}
function getPackageNameFromSpecifier(specifier) {
    if (specifier.startsWith('@')) {
        const [scope, name] = specifier.split('/');
        return name ? `${scope}/${name}` : specifier;
    }
    return specifier.split('/')[0] || specifier;
}
function buildWorkspacePackageQN(repositoryName, packageName, relativeRoot) {
    const qualifiedName = `typescript-package:${repositoryName}:${packageName}`;
    return relativeRoot === undefined ? qualifiedName : `${qualifiedName}#${relativeRoot || '.'}`;
}
function buildNpmPackageQN(packageName) {
    return `npm:${packageName}`;
}
function toWorkspacePackageDto(pkg) {
    return {
        name: pkg.name,
        qualifiedName: pkg.qualifiedName,
        rootPath: pkg.relativeRoot,
        kind: pkg.kind,
        packageJsonPath: pkg.packageJsonPath,
        tsconfigPath: pkg.tsconfigPath,
    };
}
function dedupeEdges(edges) {
    const seen = new Set();
    const unique = [];
    for (const edge of edges) {
        const key = `${edge.sourceQN}\u0000${edge.targetQN}\u0000${edge.type}\u0000${JSON.stringify(edge.properties ?? {})}`;
        if (seen.has(key))
            continue;
        seen.add(key);
        unique.push(edge);
    }
    return unique;
}
function shouldSkipRelativePath(filePath) {
    return filePath.includes('node_modules/') ||
        filePath.endsWith('.d.ts') ||
        filePath.endsWith('.spec.ts') ||
        filePath.includes('.generated.');
}
function isNodeModulePath(filePath) {
    return filePath.split(/[\\/]/).includes('node_modules');
}
function isInside(parent, candidate) {
    const rel = path.relative(normalizeAbsolute(parent), normalizeAbsolute(candidate));
    return rel === '' || (!!rel && !rel.startsWith('..') && !path.isAbsolute(rel));
}
function normalizeAbsolute(filePath) {
    return path.resolve(filePath).replace(/\\/g, '/');
}
function toRelative(rootPath, filePath) {
    return path.relative(rootPath, filePath).replace(/\\/g, '/');
}
function flattenDiagnostic(diagnostic) {
    return ts.flattenDiagnosticMessageText(diagnostic.messageText, ' ');
}
function moduleResolutionName(value) {
    if (value === undefined)
        return undefined;
    return ts.ModuleResolutionKind[value];
}
function mergePackageKinds(left, right) {
    if (left === right)
        return left;
    if (left === 'root_package')
        return right;
    if (right === 'root_package')
        return left;
    return `${left},${right}`;
}
