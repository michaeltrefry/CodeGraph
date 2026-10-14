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
exports.DiscoveryScope = void 0;
const path = __importStar(require("path"));
/**
 * The repository files the indexing pipeline's discovery admits (repo-relative). When CodeGraph
 * passes this list, it is the only source of files the sidecar reads: TypeScript sources,
 * tsconfig.json and package.json files outside it (agent worktrees, dependency directories and
 * every other discovery exclusion) are never analyzed, and no tsconfig `include`/`references`
 * glob can pull them back in.
 */
class DiscoveryScope {
    constructor(rootPath, includedFiles) {
        this.rootPath = rootPath;
        this.files = new Set();
        this.directories = new Set(['']);
        for (const file of includedFiles) {
            const relative = normalizeRelative(file);
            if (!relative || relative.startsWith('../') || path.isAbsolute(relative))
                continue;
            this.files.add(relative);
            let directory = path.posix.dirname(relative);
            while (directory !== '.' && directory !== '' && !this.directories.has(directory)) {
                this.directories.add(directory);
                directory = path.posix.dirname(directory);
            }
        }
    }
    /** Absolute paths of the admitted files accepted by `predicate`. */
    absoluteFiles(predicate = () => true) {
        return [...this.files]
            .filter(predicate)
            .sort()
            .map(relative => path.join(this.rootPath, relative));
    }
    /** True when the directory (repo-relative, '' for the root) holds at least one admitted file. */
    includesDirectory(relativeDirectory) {
        return this.directories.has(normalizeRelative(relativeDirectory));
    }
}
exports.DiscoveryScope = DiscoveryScope;
function normalizeRelative(filePath) {
    const normalized = path.posix.normalize(filePath.replace(/\\/g, '/')).replace(/^\.\/?/, '').replace(/\/$/, '');
    return normalized === '.' ? '' : normalized;
}
