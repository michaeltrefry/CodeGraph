import Fastify, { FastifyInstance } from 'fastify';
import * as fs from 'fs';
import * as path from 'path';
import { finished } from 'stream/promises';
import { extractProject } from './extractor';
import { BoundedTextReader, validateSourceScope, SourceAdmissionError } from './boundedTextReader';
import { lintProject } from './linter';
import type { ExtractProjectRequest, ExtractProjectResponse } from './protocol';
import type { LintProjectRequest, LintProjectResponse } from './linter';

/**
 * Request bodies may carry an inline discovery scope (thousands of paths); CodeGraph passes large
 * scopes through `includedFilesPath`, and this ceiling keeps an inline one from being rejected
 * with 413 well before it is unreasonable.
 */
export const BODY_LIMIT_BYTES = 64 * 1024 * 1024;

const defaultLogsDir = path.join(__dirname, '..', 'logs');

function openLogFile(logsDir: string, repositoryName: string): fs.WriteStream {
  fs.mkdirSync(logsDir, { recursive: true });
  const now = new Date();
  const ts = now.getFullYear().toString()
    + String(now.getMonth() + 1).padStart(2, '0')
    + String(now.getDate()).padStart(2, '0')
    + String(now.getHours()).padStart(2, '0')
    + String(now.getMinutes()).padStart(2, '0');
  const safeName = repositoryName.replace(/[^A-Za-z0-9._-]/g, '_');
  const file = path.join(logsDir, `${ts}-${safeName}.log`);
  return fs.createWriteStream(file, { flags: 'w' });
}

/** The discovery scope of a request: the file named by `includedFilesPath`, else the inline list. */
export function readIncludedFiles(request: ExtractProjectRequest): string[] | undefined {
  if (request.includedFilesPath) {
    const absolute = path.resolve(request.includedFilesPath);
    const reader = new BoundedTextReader(path.dirname(absolute), file => file === absolute, { maxFileSizeKb: 65536, maxAnalysisTextKb: 65536 });
    const text = reader.read(absolute, true);
    if (text === undefined) throw new SourceAdmissionError('includedFilesPath could not be read within the regular-file scope budget');
    let parsed: unknown;
    try { parsed = JSON.parse(text); } catch { throw new SourceAdmissionError('includedFilesPath is not valid JSON'); }
    if (!Array.isArray(parsed) || parsed.some(entry => typeof entry !== 'string')) {
      throw new SourceAdmissionError('includedFilesPath must hold a JSON array of path strings');
    }
    validateSourceScope(parsed as string[]);
    return parsed as string[];
  }
  validateSourceScope(request.includedFiles);
  return request.includedFiles;
}

export function buildApp(logsDir: string = defaultLogsDir): FastifyInstance {
  const app = Fastify({ logger: false, bodyLimit: BODY_LIMIT_BYTES });

  app.get('/health', async () => ({ status: 'ok', sourceBoundsVersion: 7 }));

  app.post<{ Body: ExtractProjectRequest; Reply: ExtractProjectResponse }>(
    '/extract-project',
    async (request, reply) => {
      const { repositoryName, rootPath, tsconfigPath } = request.body;
      const logStream = openLogFile(logsDir, repositoryName);
      const log = (msg: string) => {
        const line = `[${new Date().toISOString()}] ${msg}`;
        logStream.write(line + '\n');
        console.error(line);
      };

      try {
        const includedFiles = readIncludedFiles(request.body);
        log(`Starting extraction: ${repositoryName} root=${rootPath} tsconfig=${tsconfigPath} includedFiles=${includedFiles?.length ?? '(disk walk)'}`);

        const t0 = Date.now();
        const result = extractProject(repositoryName, rootPath, tsconfigPath, log, includedFiles, request.body);
        const elapsed = Date.now() - t0;

        // Log diagnostics
        const edgeTypes: Record<string, number> = {};
        for (const e of result.edges) edgeTypes[e.type] = (edgeTypes[e.type] || 0) + 1;
        const httpEdges = result.edges.filter(e => e.type === 'HTTP_CALLS');

        log(`Completed in ${elapsed}ms: ${result.nodes.length} nodes, ${result.edges.length} edges — ${JSON.stringify(edgeTypes)}`);
        if (httpEdges.length > 0) {
          for (const e of httpEdges) log(`  HTTP_CALLS: ${e.sourceQN} -> ${e.targetQN}`);
        } else {
          log(`  No HTTP_CALLS edges found`);
        }

        if (result.diagnostics?.length) {
          for (const d of result.diagnostics) log(`  diag: ${d}`);
        }

        return result;
      } catch (err) {
        const message = err instanceof SourceAdmissionError ? err.message : 'invalid or unreadable extraction request';
        log(`ERROR: ${message}`);
        reply.status(500).send({ error: message } as never);
      } finally {
        // Complete the request only after its log has closed. Callers may immediately remove
        // an ephemeral log directory or inspect diagnostics once extraction has returned.
        logStream.end();
        await finished(logStream);
      }
    },
  );

  app.post<{ Body: LintProjectRequest; Reply: LintProjectResponse }>(
    '/lint-project',
    async (request) => {
      const { repoPath, files } = request.body;
      console.error(`[${new Date().toISOString()}] Linting: ${repoPath}`);

      const t0 = Date.now();
      const result = lintProject(repoPath, files);
      const elapsed = Date.now() - t0;

      console.error(
        `[${new Date().toISOString()}] Lint completed in ${elapsed}ms: ${result.results.length} files with issues`,
      );

      return result;
    },
  );

  return app;
}
