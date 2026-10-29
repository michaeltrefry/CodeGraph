import { strict as assert } from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test } from 'node:test';
import { buildApp } from './app';
import type { ExtractProjectResponse } from './protocol';

function writeFixture(): string {
  const rootPath = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-app-'));
  fs.writeFileSync(path.join(rootPath, 'tsconfig.json'), '{}');
  fs.mkdirSync(path.join(rootPath, 'src'));
  fs.writeFileSync(path.join(rootPath, 'src', 'app.ts'), 'export class App {}\n');
  fs.writeFileSync(path.join(rootPath, 'src', 'other.ts'), 'export class Other {}\n');
  return rootPath;
}

test('an inline discovery scope larger than 1 MiB is accepted and honoured', async () => {
  const rootPath = writeFixture();
  const logsDir = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-logs-'));
  const app = buildApp(logsDir);
  try {
    // ~1.5 MiB of admitted (non-source) paths, as a 15k+ file repository would send.
    const padding = Array.from({ length: 20000 }, (_, i) => `docs/section-${i}/a-reasonably-long-page-name-${i}.md`);
    const payload = {
      repositoryName: 'Demo',
      rootPath,
      tsconfigPath: path.join(rootPath, 'tsconfig.json'),
      includedFiles: ['tsconfig.json', 'src/app.ts', ...padding],
    };
    assert.ok(Buffer.byteLength(JSON.stringify(payload)) > 1024 * 1024);

    const response = await app.inject({ method: 'POST', url: '/extract-project', payload });

    assert.equal(response.statusCode, 200, response.body);
    assert.deepEqual((response.json() as ExtractProjectResponse).processedFiles, ['src/app.ts']);
  } finally {
    await app.close();
    fs.rmSync(rootPath, { recursive: true, force: true });
    fs.rmSync(logsDir, { recursive: true, force: true });
  }
});

test('a discovery scope passed by file path takes precedence over the inline list', async () => {
  const rootPath = writeFixture();
  const logsDir = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-ts-logs-'));
  const scopeFile = path.join(logsDir, 'scope.json');
  fs.writeFileSync(scopeFile, JSON.stringify(['tsconfig.json', 'src/other.ts']));
  const app = buildApp(logsDir);
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

    assert.equal(response.statusCode, 200, response.body);
    assert.deepEqual((response.json() as ExtractProjectResponse).processedFiles, ['src/other.ts']);
  } finally {
    await app.close();
    fs.rmSync(rootPath, { recursive: true, force: true });
    fs.rmSync(logsDir, { recursive: true, force: true });
  }
});
