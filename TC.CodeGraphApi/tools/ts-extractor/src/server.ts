import Fastify from 'fastify';
import * as fs from 'fs';
import * as path from 'path';
import { extractProject } from './extractor';
import { lintProject } from './linter';
import type { ExtractProjectRequest, ExtractProjectResponse } from './protocol';
import type { LintProjectRequest, LintProjectResponse } from './linter';

const port = parseInt(process.env['CODEGRAPH_TS_PORT'] ?? '3100', 10);
const logsDir = path.join(__dirname, '..', 'logs');

function openLogFile(projectName: string): fs.WriteStream {
  fs.mkdirSync(logsDir, { recursive: true });
  const now = new Date();
  const ts = now.getFullYear().toString()
    + String(now.getMonth() + 1).padStart(2, '0')
    + String(now.getDate()).padStart(2, '0')
    + String(now.getHours()).padStart(2, '0')
    + String(now.getMinutes()).padStart(2, '0');
  const file = path.join(logsDir, `${ts}-${projectName}.log`);
  return fs.createWriteStream(file, { flags: 'w' });
}

const app = Fastify({ logger: false });

app.get('/health', async () => ({ status: 'ok' }));

app.post<{ Body: ExtractProjectRequest; Reply: ExtractProjectResponse }>(
  '/extract-project',
  async (request, reply) => {
    const { projectName, rootPath, tsconfigPath } = request.body;
    const logStream = openLogFile(projectName);
    const log = (msg: string) => {
      const line = `[${new Date().toISOString()}] ${msg}`;
      logStream.write(line + '\n');
      console.error(line);
    };

    log(`Starting extraction: ${projectName} root=${rootPath} tsconfig=${tsconfigPath}`);

    try {
      const t0 = Date.now();
      const result = extractProject(projectName, rootPath, tsconfigPath, log);
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

      logStream.end();
      return result;
    } catch (err) {
      log(`ERROR: ${String(err)}`);
      logStream.end();
      reply.status(500).send({ error: String(err) } as never);
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

app.listen({ port, host: '127.0.0.1' })
  .then(address => console.log(`ts-extractor listening on ${address}`))
  .catch(err => { console.error(err); process.exit(1); });
