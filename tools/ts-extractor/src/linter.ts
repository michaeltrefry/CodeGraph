import { execSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';

export interface LintProjectRequest {
  repoPath: string;
  files?: string[];
}

export interface FileLintResult {
  filePath: string;
  errorCount: number;
  warningCount: number;
}

export interface LintProjectResponse {
  results: FileLintResult[];
  diagnostics?: string[];
}

interface EslintJsonEntry {
  filePath: string;
  errorCount: number;
  warningCount: number;
  messages?: unknown[];
}

const SKIP_DIRS = new Set([
  'node_modules', 'dist', 'build', 'coverage', '.next', '.nuxt', '.git', '.angular', 'cypress', 'e2e'
]);

const LINTABLE_EXTENSIONS = new Set(['.ts', '.tsx', '.js', '.jsx']);

function hasEslintConfig(repoPath: string): boolean {
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
    const pkg = JSON.parse(fs.readFileSync(packageJsonPath, 'utf-8')) as { eslintConfig?: unknown };
    return !!pkg.eslintConfig;
  } catch {
    return false;
  }
}

function findLintableFiles(repoPath: string): string[] {
  const results: string[] = [];

  const walk = (dir: string) => {
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

function parseEslintOutput(jsonOutput: string, repoPath: string): FileLintResult[] {
  try {
    const entries = JSON.parse(jsonOutput) as EslintJsonEntry[];
    const results: FileLintResult[] = [];

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
  } catch {
    return [];
  }
}

export function lintProject(repoPath: string, files?: string[]): LintProjectResponse {
  const diagnostics: string[] = [];

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
      .map(f => `"${path.relative(repoPath, f).replace(/\\/g, '/')}"`)
      .join(' ');

    const cmd = `npx --no-install eslint --format json --no-error-on-unmatched-pattern ${target}`;
    diagnostics.push(`Running: ${cmd} in ${repoPath}`);

    const stdout = execSync(cmd, {
      cwd: repoPath,
      timeout: 30_000,
      stdio: ['pipe', 'pipe', 'pipe'],
      encoding: 'utf-8',
    });

    // eslint exit 0 = no issues
    const results = parseEslintOutput(stdout, repoPath);
    return { results, diagnostics };
  } catch (err: unknown) {
    // eslint exits with code 1 when it finds lint errors — stdout still has valid JSON
    if (err && typeof err === 'object' && 'stdout' in err) {
      const stdout = (err as { stdout?: string }).stdout;
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
    } else {
      diagnostics.push(`ESLint not available: ${message.substring(0, 200)}`);
    }

    return { results: [], diagnostics };
  }
}
