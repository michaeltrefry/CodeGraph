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
exports.lintProject = lintProject;
const child_process_1 = require("child_process");
const fs = __importStar(require("fs"));
const path = __importStar(require("path"));
const SKIP_DIRS = new Set([
    'node_modules', 'dist', 'build', 'coverage', '.next', '.nuxt', '.git', '.angular', 'cypress', 'e2e'
]);
const LINTABLE_EXTENSIONS = new Set(['.ts', '.tsx', '.js', '.jsx']);
function parseEslintOutput(jsonOutput, repoPath) {
    try {
        const entries = JSON.parse(jsonOutput);
        const results = [];
        for (const entry of entries) {
            if (entry.errorCount > 0 || entry.warningCount > 0) {
                results.push({
                    filePath: path.relative(repoPath, entry.filePath).replace(/\\/g, '/'),
                    errorCount: entry.errorCount,
                    warningCount: entry.warningCount,
                });
            }
        }
        return results;
    }
    catch {
        return [];
    }
}
function hasEslintConfig(repoPath) {
    const configFiles = [
        'eslint.config.js',
        'eslint.config.mjs',
        'eslint.config.cjs',
        '.eslintrc',
        '.eslintrc.js',
        '.eslintrc.cjs',
        '.eslintrc.json',
        '.eslintrc.yml',
        '.eslintrc.yaml',
    ];
    for (const file of configFiles) {
        if (fs.existsSync(path.join(repoPath, file)))
            return true;
    }
    const packageJsonPath = path.join(repoPath, 'package.json');
    if (!fs.existsSync(packageJsonPath))
        return false;
    try {
        const pkg = JSON.parse(fs.readFileSync(packageJsonPath, 'utf-8'));
        return !!pkg.eslintConfig;
    }
    catch {
        return false;
    }
}
function findLintableFiles(repoPath) {
    const results = [];
    const walk = (dir) => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
            if (entry.isDirectory()) {
                if (!SKIP_DIRS.has(entry.name))
                    walk(path.join(dir, entry.name));
                continue;
            }
            const ext = path.extname(entry.name);
            if (!LINTABLE_EXTENSIONS.has(ext))
                continue;
            if (entry.name.endsWith('.d.ts') || entry.name.endsWith('.spec.ts'))
                continue;
            results.push(path.join(dir, entry.name));
        }
    };
    walk(repoPath);
    return results;
}
function lintProject(repoPath, files) {
    const diagnostics = [];
    try {
        if (!hasEslintConfig(repoPath)) {
            diagnostics.push('ESLint skipped: no config found in repo root');
            return { results: [], diagnostics };
        }
        const requestedFiles = files && files.length > 0
            ? files.map(f => path.isAbsolute(f) ? f : path.join(repoPath, f))
            : findLintableFiles(repoPath);
        if (requestedFiles.length === 0) {
            diagnostics.push('ESLint skipped: no lintable JS/TS source files found');
            return { results: [], diagnostics };
        }
        const target = requestedFiles
            .map(f => `"${path.relative(repoPath, f).replace(/\\/g, '/')
            }"`)
            .join(' ');
        const cmd = `npx --no-install eslint --format json --no-error-on-unmatched-pattern ${target}`;
        diagnostics.push(`Running: ${cmd} in ${repoPath}`);
        const stdout = (0, child_process_1.execSync)(cmd, {
            cwd: repoPath,
            timeout: 30000,
            stdio: ['pipe', 'pipe', 'pipe'],
            encoding: 'utf-8',
        });
        // eslint exit 0 = no issues
        const results = parseEslintOutput(stdout, repoPath);
        return { results, diagnostics };
    }
    catch (err) {
        // eslint exits with code 1 when it finds lint errors — stdout still has valid JSON
        if (err && typeof err === 'object' && 'stdout' in err) {
            const stdout = err.stdout;
            if (stdout && stdout.trim().startsWith('[')) {
                const results = parseEslintOutput(stdout, repoPath);
                diagnostics.push(`ESLint found issues in ${results.length} files`);
                return { results, diagnostics };
            }
        }
        // ESLint not installed, no config, timeout, etc. — graceful degradation
        const message = err instanceof Error ? err.message : String(err);
        if (message.includes('could not determine executable to run') || message.includes('command not found')) {
            diagnostics.push('ESLint skipped: eslint is not installed in this repo');
            return { results: [], diagnostics };
        }
        if (message.includes('ETIMEDOUT') || message.includes('timeout')) {
            diagnostics.push('ESLint timed out after 30s');
        }
        else {
            diagnostics.push(`ESLint not available: ${message.substring(0, 200)}`);
        }
        return { results: [], diagnostics };
    }
}
