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
exports.BODY_LIMIT_BYTES = void 0;
exports.readIncludedFiles = readIncludedFiles;
exports.buildApp = buildApp;
const fastify_1 = __importDefault(require("fastify"));
const fs = __importStar(require("fs"));
const path = __importStar(require("path"));
const promises_1 = require("stream/promises");
const extractor_1 = require("./extractor");
const boundedTextReader_1 = require("./boundedTextReader");
const linter_1 = require("./linter");
/**
 * Request bodies may carry an inline discovery scope (thousands of paths); CodeGraph passes large
 * scopes through `includedFilesPath`, and this ceiling keeps an inline one from being rejected
 * with 413 well before it is unreasonable.
 */
exports.BODY_LIMIT_BYTES = 64 * 1024 * 1024;
const defaultLogsDir = path.join(__dirname, '..', 'logs');
function openLogFile(logsDir, repositoryName) {
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
function readIncludedFiles(request) {
    if (request.includedFilesPath) {
        const absolute = path.resolve(request.includedFilesPath);
        const reader = new boundedTextReader_1.BoundedTextReader(path.dirname(absolute), file => file === absolute, { maxFileSizeKb: 65536, maxAnalysisTextKb: 65536 });
        const text = reader.read(absolute, true);
        if (text === undefined)
            throw new boundedTextReader_1.SourceAdmissionError('includedFilesPath could not be read within the regular-file scope budget');
        let parsed;
        try {
            parsed = JSON.parse(text);
        }
        catch {
            throw new boundedTextReader_1.SourceAdmissionError('includedFilesPath is not valid JSON');
        }
        if (!Array.isArray(parsed) || parsed.some(entry => typeof entry !== 'string')) {
            throw new boundedTextReader_1.SourceAdmissionError('includedFilesPath must hold a JSON array of path strings');
        }
        (0, boundedTextReader_1.validateSourceScope)(parsed);
        return parsed;
    }
    (0, boundedTextReader_1.validateSourceScope)(request.includedFiles);
    return request.includedFiles;
}
function buildApp(logsDir = defaultLogsDir) {
    const app = (0, fastify_1.default)({ logger: false, bodyLimit: exports.BODY_LIMIT_BYTES });
    app.get('/health', async () => ({ status: 'ok', sourceBoundsVersion: 6 }));
    app.post('/extract-project', async (request, reply) => {
        const { repositoryName, rootPath, tsconfigPath } = request.body;
        const logStream = openLogFile(logsDir, repositoryName);
        const log = (msg) => {
            const line = `[${new Date().toISOString()}] ${msg}`;
            logStream.write(line + '\n');
            console.error(line);
        };
        try {
            const includedFiles = readIncludedFiles(request.body);
            log(`Starting extraction: ${repositoryName} root=${rootPath} tsconfig=${tsconfigPath} includedFiles=${includedFiles?.length ?? '(disk walk)'}`);
            const t0 = Date.now();
            const result = (0, extractor_1.extractProject)(repositoryName, rootPath, tsconfigPath, log, includedFiles, request.body);
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
            return result;
        }
        catch (err) {
            const message = err instanceof boundedTextReader_1.SourceAdmissionError ? err.message : 'invalid or unreadable extraction request';
            log(`ERROR: ${message}`);
            reply.status(500).send({ error: message });
        }
        finally {
            // Complete the request only after its log has closed. Callers may immediately remove
            // an ephemeral log directory or inspect diagnostics once extraction has returned.
            logStream.end();
            await (0, promises_1.finished)(logStream);
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
    return app;
}
