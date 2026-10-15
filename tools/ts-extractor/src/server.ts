import { buildApp } from './app';

const port = parseInt(process.env['CODEGRAPH_TS_PORT'] ?? '3100', 10);

buildApp().listen({ port, host: '127.0.0.1' })
  .then(address => console.log(`ts-extractor listening on ${address}`))
  .catch(err => { console.error(err); process.exit(1); });
