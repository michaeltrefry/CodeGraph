"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
const app_1 = require("./app");
const port = parseInt(process.env['CODEGRAPH_TS_PORT'] ?? '3100', 10);
(0, app_1.buildApp)().listen({ port, host: '127.0.0.1' })
    .then(address => console.log(`ts-extractor listening on ${address}`))
    .catch(err => { console.error(err); process.exit(1); });
