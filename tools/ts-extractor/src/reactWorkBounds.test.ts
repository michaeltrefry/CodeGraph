import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { extractProject } from './extractor';
import { Project } from 'ts-morph';
import { SemanticModel } from './semanticModel';
import { analyzeReact } from './reactAnalysis';
import { ExtractionWorkLimitError } from './extractionWork';
import type { ExtractionWorkControl } from './extractionWork';
import type { ExtractProjectResponse, GraphNodeDto, PendingEdgeDto } from './protocol';

function fixture(source: string) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codegraph-react-work-'));
  fs.writeFileSync(path.join(root, 'app.tsx'), source);
  const run = (control: ExtractionWorkControl = {}) => {
    let work = 0;
    const result = extractProject('ReactWork', root, path.join(root, 'tsconfig.json'), () => {}, ['app.tsx'], undefined,
      { ...control, onWork: value => { work = value; control.onWork?.(value); } });
    return { result, work };
  };
  return { run, dispose: () => fs.rmSync(root, { recursive: true, force: true }) };
}
function emptyRefusal(result: ExtractProjectResponse, parserRefusal = false) {
  assert.equal(result.complete, false);
  assert.deepEqual(result.nodes, []); assert.deepEqual(result.edges, []); assert.deepEqual(result.processedFiles, []);
  assert.deepEqual(result.resolvedImports, []);
  if (!parserRefusal) assert.ok(!result.diagnostics?.some(message => /RangeError|Maximum call stack/i.test(message)), result.diagnostics?.join('\n'));
}
function broad(count: number): string {
  const siblings = '<Leaf/>'.repeat(count);
  return `import {createContext,useContext,useMemo} from 'react';
    function fallback(){} function outerSave(){} function innerSave(){} function unusedSave(){}
    const Ctx=createContext({save:fallback});
    function useActions(){return useContext(Ctx);}
    function Leaf(){const actions=useActions();return <button onClick={actions.save}/>;}
    function Slot({children}){return <main>{children}</main>;}
    export function App(){const value=useMemo(()=>({save:outerSave}),[]); return <Ctx.Provider value={value}>
      <Slot>${siblings}<Ctx.Provider value={{save:innerSave}}><Slot>${siblings}</Slot></Ctx.Provider></Slot>
    </Ctx.Provider>;}
    export function Unprovided(){return <Slot><Leaf/></Slot>;}
    export function UnusedProvider(){return <Ctx.Provider value={{save:unusedSave}}><aside/></Ctx.Provider>;}
  `;
}
function bindings(result: ExtractProjectResponse) {
  assert.equal(result.complete, true, result.diagnostics?.join('\n'));
  const providers = result.nodes.filter(node => node.properties.context_kind === 'provider');
  const providerFor = (name: string) => {
    const provider = providers.find(candidate => result.edges.some(edge => edge.sourceQN === candidate.qualifiedName && edge.type === 'CALLS' && edge.targetQN === `ReactWork:app.tsx#${name}`));
    assert.ok(provider, `provider value ${name} must remain linked`); return provider;
  };
  const outer = providerFor('outerSave'), inner = providerFor('innerSave'), unused = providerFor('unusedSave');
  const selected = result.edges.filter(edge => edge.sourceQN === 'ReactWork:app.tsx#useActions' && edge.properties?.context_kind === 'binding');
  assert.ok(selected.some(edge => edge.targetQN === outer.qualifiedName), 'outer provider must reach the custom hook through the children slot');
  assert.ok(selected.some(edge => edge.targetQN === inner.qualifiedName), 'nested provider override must remain a distinct alternative');
  assert.ok(selected.some(edge => edge.targetQN === 'ReactWork:app.tsx#fallback'), 'unprovided root must retain the context default');
  assert.ok(!selected.some(edge => edge.targetQN === unused.qualifiedName), 'a provider with no observed consumer cannot leak into another path');
  assert.ok(selected.every(edge => edge.properties?.candidate === true && edge.properties?.confidence_band === 'medium'));
  assert.ok(result.edges.some(edge => edge.sourceQN === 'ReactWork:app.tsx#Leaf' && edge.targetQN === 'ReactWork:app.tsx#useActions' && edge.type === 'CALLS'));
  return selected.map(edge => JSON.stringify([edge.type, edge.sourceQN, edge.targetQN, edge.properties])).sort();
}

test('21KB of 3000 nested children safely refuses at the parser or React guard without partial graph output', context => {
  const nesting = '<C>'.repeat(3000) + '<Leaf/>' + '</C>'.repeat(3000);
  assert.equal(nesting.length, 21007);
  const source = `import {createContext,useContext} from 'react';
    function save(){} const Ctx=createContext({save});
    function Child({children}){return children;} const C=Child;
    function Leaf(){useContext(Ctx);return <span/>;}
    export function App(){return <Ctx.Provider value={{save}}>${nesting}</Ctx.Provider>;}`;
  assert.ok(source.length < 512 * 1024);
  const f = fixture(source);
  try {
    const { result, work } = f.run();
    const parserRefusal = result.diagnostics?.some(message => /^sourceRead:.*(?:RangeError|Maximum call stack)/i.test(message)) ?? false;
    if (result.complete) {
      const provider = result.nodes.find(node => node.properties.context_kind === 'provider'); assert.ok(provider);
      assert.ok(result.edges.some(edge => edge.sourceQN === 'ReactWork:app.tsx#Leaf' && edge.targetQN === provider.qualifiedName && edge.properties?.context_kind === 'binding'),
        'a complete result must trace the leaf through the children chain');
    } else {
      emptyRefusal(result, parserRefusal);
      assert.ok(parserRefusal || result.diagnostics?.some(message => /depth|nesting|traversal|work limit|analysis work/i.test(message)), result.diagnostics?.join('\n'));
    }
    context.diagnostic(`3000 nested children: complete=${result.complete}, parserRefusal=${parserRefusal}, work=${work}, diagnostic=${result.diagnostics?.[0] ?? '(none)'}`);
  } finally { f.dispose(); }
});

test('parsed 512-level JSX reaches the React depth guard and poisons the analysis allowance', () => {
  const nesting = '<C>'.repeat(512) + '<Leaf/>' + '</C>'.repeat(512);
  const text = `import {createContext,useContext} from 'react';
    function save(){} const Ctx=createContext({save});
    function Child({children}){return children;} const C=Child;
    function Leaf(){useContext(Ctx);return <span/>;}
    export function App(){return <Ctx.Provider value={{save}}>${nesting}</Ctx.Provider>;}`;
  const project = new Project({ useInMemoryFileSystem: true, skipFileDependencyResolution: true,
    compilerOptions: { noResolve: true, types: [] } });
  const file = project.createSourceFile('/repo/app.tsx', text);
  const model = new SemanticModel('ReactWork', '/repo', [file], [], () => {});
  const nodes: GraphNodeDto[] = [], edges: PendingEdgeDto[] = [];
  model.extract(nodes, edges, []);
  const before = model.work.used; let failure: unknown;
  assert.throws(() => analyzeReact(model, nodes, edges, () => {}), error => {
    failure = error;
    return error instanceof ExtractionWorkLimitError && error.code === 'extraction_work_incomplete' && error.reason === 'depth_limit';
  });
  assert.ok(model.work.used > before, 'the parsed input must actually enter the iterative React index');
  assert.throws(() => model.work.charge(0), error => error === failure, 'depth refusal must poison subsequent work in this run');
});

test('wide React sibling trees use proportional work and preserve providers, hooks, overrides and defaults', context => {
  const measure = (count: number) => {
    const f = fixture(broad(count));
    try {
      const first = f.run(); const canonical = bindings(first.result);
      const next = f.run(); assert.deepEqual(bindings(next.result), canonical, 'fresh equivalent runs retain the same scoped binding evidence');
      assert.equal(next.work, first.work, 'work accounting must reset and remain deterministic');
      return first.work;
    } finally { f.dispose(); }
  };
  const smaller = measure(32), larger = measure(128);
  assert.ok(larger <= smaller * 6, `4x sibling source must not trigger quadratic ancestor/render scans: ${smaller} -> ${larger}`);
  context.diagnostic(`32 siblings per provider: ${smaller} work; 128 siblings per provider: ${larger}`);
});

test('React work/cancellation refusal returns no partial graph and following analyses start fresh', () => {
  const f = fixture(broad(32));
  try {
    const healthy = f.run(); const canonical = bindings(healthy.result);
    const threshold = Math.floor(healthy.work / 2); assert.ok(threshold > 0);
    emptyRefusal(f.run({ maxWork: 1 }).result);
    emptyRefusal(f.run({ maxWork: threshold }).result);
    let cancelled = false;
    const cancelledRun = f.run({ shouldCancel: () => cancelled, onWork: work => { if (work >= threshold) cancelled = true; } });
    emptyRefusal(cancelledRun.result);
    assert.ok(cancelledRun.result.diagnostics?.some(message => /work limit|cancell|analysis work/i.test(message)), cancelledRun.result.diagnostics?.join('\n'));
    const recovered = f.run(); assert.deepEqual(bindings(recovered.result), canonical);
    assert.equal(recovered.work, healthy.work); assert.deepEqual(recovered.result.processedFiles, ['app.tsx']);
  } finally { f.dispose(); }
});

test('equivalent children slots share their provider effect instead of allocating a slots-by-children product', context => {
  const measure = (slots: number, children: number) => {
    const f = fixture(`import {createContext,useContext} from 'react';
      function save(){} const Ctx=createContext(null);
      function Leaf(){useContext(Ctx);return <span/>;}
      function ManySlots({children}){return <section>${'{children}'.repeat(slots)}</section>;}
      export function App(){return <Ctx.Provider value={{save}}><ManySlots>${'<Leaf/>'.repeat(children)}</ManySlots></Ctx.Provider>;}`);
    try {
      const { result, work } = f.run(); assert.equal(result.complete, true, result.diagnostics?.join('\n'));
      const provider = result.nodes.find(node => node.properties.context_kind === 'provider'); assert.ok(provider);
      assert.ok(result.edges.some(edge => edge.sourceQN === provider.qualifiedName && edge.targetQN === 'ReactWork:app.tsx#save' && edge.type === 'CALLS'));
      const selected = result.edges.filter(edge => edge.sourceQN === 'ReactWork:app.tsx#Leaf' && edge.properties?.context_kind === 'binding');
      assert.equal(selected.length, 1); assert.equal(selected[0].targetQN, provider.qualifiedName);
      assert.equal(selected[0].properties?.confidence_band, 'high'); assert.notEqual(selected[0].properties?.candidate, true);
      return work;
    } finally { f.dispose(); }
  };
  const small = measure(10, 10), large = measure(1000, 1000);
  assert.ok(large <= small * 150, `100x slot/child input must not require 10000x product work: ${small} -> ${large}`);
  context.diagnostic(`10 slots x 10 children: ${small} work; 1000 x 1000: ${large}`);
});
