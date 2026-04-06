import { execSync } from 'child_process';
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
    const target = files && files.length > 0
      ? files.map(f => `"${f}"`).join(' ')
      : '.';

    const cmd = `npx eslint --format json ${target}`;
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
    if (message.includes('ETIMEDOUT') || message.includes('timeout')) {
      diagnostics.push('ESLint timed out after 30s');
    } else {
      diagnostics.push(`ESLint not available: ${message.substring(0, 200)}`);
    }

    return { results: [], diagnostics };
  }
}
