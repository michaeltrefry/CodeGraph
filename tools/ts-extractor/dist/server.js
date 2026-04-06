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
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
const fastify_1 = __importDefault(require("fastify"));
const fs = __importStar(require("fs"));
const path = __importStar(require("path"));
const extractor_1 = require("./extractor");
const linter_1 = require("./linter");
const port = parseInt(process.env['CODEGRAPH_TS_PORT'] ?? '3100', 10);
const logsDir = path.join(__dirname, '..', 'logs');
function openLogFile(projectName) {
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
const app = (0, fastify_1.default)({ logger: false });
app.get('/health', async () => ({ status: 'ok' }));
app.post('/extract-project', async (request, reply) => {
    const { projectName, rootPath, tsconfigPath } = request.body;
    const logStream = openLogFile(projectName);
    const log = (msg) => {
        const line = `[${new Date().toISOString()}] ${msg}`;
        logStream.write(line + '\n');
        console.error(line);
    };
    log(`Starting extraction: ${projectName} root=${rootPath} tsconfig=${tsconfigPath}`);
    try {
        const t0 = Date.now();
        const result = (0, extractor_1.extractProject)(projectName, rootPath, tsconfigPath, log);
        const elapsed = Date.now() - t0;
        // Log diagnostics
        const edgeTypes = {};
        for (const e of result.edges)
            edgeTypes[e.type] = (edgeTypes[e.type] || 0) + 1;
        const httpEdges = result.edges.filter(e => e.type === 'HTTP_CALLS');
        log(`Completed in ${elapsed}ms: ${result.nodes.length} nodes, ${result.edges.length} edges — ${JSON.stringify(edgeTypes)}`);
        if (httpEdges.length > 0) {
            for (const e of httpEdges)
                log(`  HTTP_CALLS: ${e.sourceQN} -> ${e.targetQN}`);
        }
        else {
            log(`  No HTTP_CALLS edges found`);
        }
        if (result.diagnostics?.length) {
            for (const d of result.diagnostics)
                log(`  diag: ${d}`);
        }
        logStream.end();
        return result;
    }
    catch (err) {
        log(`ERROR: ${String(err)}`);
        logStream.end();
        reply.status(500).send({ error: String(err) });
    }
});
app.post('/lint-project', async (request) => {
    const { repoPath, files } = request.body;
    console.error(`[${new Date().toISOString()}] Linting: ${repoPath}`);
    const t0 = Date.now();
    const result = (0, linter_1.lintProject)(repoPath, files);
    const elapsed = Date.now() - t0;
    console.error(`[${new Date().toISOString()}] Lint completed in ${elapsed}ms: ${result.results.length} files with issues`);
    return result;
});
app.listen({ port, host: '127.0.0.1' })
    .then(address => console.log(`ts-extractor listening on ${address}`))
    .catch(err => { console.error(err); process.exit(1); });
