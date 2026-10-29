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
const assert_1 = require("assert");
const fs = __importStar(require("fs"));
const os = __importStar(require("os"));
const path = __importStar(require("path"));
const node_test_1 = require("node:test");
const app_1 = require("./app");
function writeFixture() {
    const rootPath = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-app-'));
    fs.writeFileSync(path.join(rootPath, 'tsconfig.json'), '{}');
    fs.mkdirSync(path.join(rootPath, 'src'));
    fs.writeFileSync(path.join(rootPath, 'src', 'app.ts'), 'export class App {}\n');
    fs.writeFileSync(path.join(rootPath, 'src', 'other.ts'), 'export class Other {}\n');
    return rootPath;
}
(0, node_test_1.test)('an inline discovery scope larger than 1 MiB is accepted and honoured', async () => {
    const rootPath = writeFixture();
    const logsDir = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-logs-'));
    const app = (0, app_1.buildApp)(logsDir);
    try {
        // ~1.5 MiB of admitted (non-source) paths, as a 15k+ file repository would send.
        const padding = Array.from({ length: 20000 }, (_, i) => `docs/section-${i}/a-reasonably-long-page-name-${i}.md`);
        const payload = {
            repositoryName: 'Demo',
            rootPath,
            tsconfigPath: path.join(rootPath, 'tsconfig.json'),
            includedFiles: ['tsconfig.json', 'src/app.ts', ...padding],
        };
        assert_1.strict.ok(Buffer.byteLength(JSON.stringify(payload)) > 1024 * 1024);
        const response = await app.inject({ method: 'POST', url: '/extract-project', payload });
        assert_1.strict.equal(response.statusCode, 200, response.body);
        assert_1.strict.deepEqual(response.json().processedFiles, ['src/app.ts']);
    }
    finally {
        await app.close();
        fs.rmSync(rootPath, { recursive: true, force: true });
        fs.rmSync(logsDir, { recursive: true, force: true });
    }
});
(0, node_test_1.test)('a discovery scope passed by file path takes precedence over the inline list', async () => {
    const rootPath = writeFixture();
    const logsDir = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-logs-'));
    const scopeFile = path.join(logsDir, 'scope.json');
    fs.writeFileSync(scopeFile, JSON.stringify(['tsconfig.json', 'src/other.ts']));
    const app = (0, app_1.buildApp)(logsDir);
    try {
        const response = await app.inject({
            method: 'POST',
            url: '/extract-project',
            payload: {
                repositoryName: 'Demo',
                rootPath,
                tsconfigPath: path.join(rootPath, 'tsconfig.json'),
                includedFiles: ['tsconfig.json', 'src/app.ts'],
                includedFilesPath: scopeFile,
            },
        });
        assert_1.strict.equal(response.statusCode, 200, response.body);
        assert_1.strict.deepEqual(response.json().processedFiles, ['src/other.ts']);
    }
    finally {
        await app.close();
        fs.rmSync(rootPath, { recursive: true, force: true });
        fs.rmSync(logsDir, { recursive: true, force: true });
    }
});
