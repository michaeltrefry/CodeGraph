import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import {Project,Node,ArrayBindingPattern,ArrayLiteralExpression,ObjectLiteralExpression} from 'ts-morph';
import {SemanticModel} from './semanticModel';
import {StaticEvaluator,isObject} from './staticEvaluation';
import {extractProject} from './extractor';
import {ExtractionWorkLimitError,type ExtractionWorkControl} from './extractionWork';
import type {ExtractProjectResponse} from './protocol';

function arrayCorpus(count:number) {
  return `const [${Array.from({length:count},(_,i)=>`v${i}`).join(',')}]=[${Array.from({length:count},(_,i)=>`{value:${i}}`).join(',')}];const probe={method:'GET'};`;
}
function objectCorpus(count:number) {
  return `const {${Array.from({length:count},(_,i)=>`k${i}:v${i}`).join(',')}}={${Array.from({length:count},(_,i)=>`k${i}:{value:${i}}`).join(',')}};const probe={method:'GET'};`;
}
function direct(source:string,control:ExtractionWorkControl={}) {
  const root=fs.realpathSync(os.tmpdir());
  const project=new Project({useInMemoryFileSystem:true,skipFileDependencyResolution:true,compilerOptions:{noResolve:true,types:[]}});
  const file=project.createSourceFile(path.join(root,'bindings.ts'),source);
  const diagnostics:string[]=[];
  const model=new SemanticModel('Bindings',root,[file],[],message=>diagnostics.push(message),control);
  const evaluator=new StaticEvaluator(model,message=>diagnostics.push(message));
  // Resolve the test probe before observing evaluator work: ts-morph's name lookup itself
  // scans a preceding destructuring pattern, which is outside the indexed path under test.
  const probe=file.getVariableDeclarationOrThrow('probe');
  return {file,model,evaluator,run:()=>evaluator.evaluate(probe)};
}
function restore(target:object,key:PropertyKey,descriptor:PropertyDescriptor|undefined) {
  if(descriptor) Object.defineProperty(target,key,descriptor); else Reflect.deleteProperty(target,key);
}
/** Count numeric element reads from the actual materialized AST arrays. Native some/indexOf
 * traverse this Proxy too, so moving the same quadratic scan inside a helper cannot pass. */
function observe<T>(f:ReturnType<typeof direct>,action:()=>T) {
  const binding=f.file.getVariableDeclarations()[0].getNameNode();
  const source=f.file.getVariableDeclarations()[0].getInitializerOrThrow();
  const counts={patterns:0,patternWidth:0,patternCandidates:0,arrays:0,arrayWidth:0,arrayCandidates:0,objects:0,objectWidth:0,objectCandidates:0};
  const wrap=<U>(values:U[],candidate:()=>void):U[]=>new Proxy(values,{get(target,key,receiver){if(typeof key==='string'&&/^(?:0|[1-9]\d*)$/.test(key)) candidate();return Reflect.get(target,key,receiver);}});
  const pattern=ArrayBindingPattern.prototype.getElements,array=ArrayLiteralExpression.prototype.getElements,object=ObjectLiteralExpression.prototype.getProperties;
  const pd=Object.getOwnPropertyDescriptor(ArrayBindingPattern.prototype,'getElements'),ad=Object.getOwnPropertyDescriptor(ArrayLiteralExpression.prototype,'getElements'),od=Object.getOwnPropertyDescriptor(ObjectLiteralExpression.prototype,'getProperties');
  Object.defineProperty(ArrayBindingPattern.prototype,'getElements',{configurable:true,writable:true,value:function(this:ArrayBindingPattern){const result=pattern.call(this);if(this!==binding)return result;counts.patterns++;counts.patternWidth+=result.length;return wrap(result,()=>counts.patternCandidates++);}});
  Object.defineProperty(ArrayLiteralExpression.prototype,'getElements',{configurable:true,writable:true,value:function(this:ArrayLiteralExpression){const result=array.call(this);if(this!==source)return result;counts.arrays++;counts.arrayWidth+=result.length;return wrap(result,()=>counts.arrayCandidates++);}});
  Object.defineProperty(ObjectLiteralExpression.prototype,'getProperties',{configurable:true,writable:true,value:function(this:ObjectLiteralExpression){const result=object.call(this);if(this!==source)return result;counts.objects++;counts.objectWidth+=result.length;return wrap(result,()=>counts.objectCandidates++);}});
  try{return {value:action(),counts};}
  finally{restore(ArrayBindingPattern.prototype,'getElements',pd);restore(ArrayLiteralExpression.prototype,'getElements',ad);restore(ObjectLiteralExpression.prototype,'getProperties',od);}
}
function fixture(source:string) {
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'codegraph-binding-index-'));fs.writeFileSync(path.join(root,'bindings.ts'),source);
  return {run:(control:ExtractionWorkControl={})=>{let work=0;const result=extractProject('Bindings',root,path.join(root,'tsconfig.json'),()=>{},['bindings.ts'],undefined,{...control,onWork:value=>{work=value;control.onWork?.(value);}});return {result,work};},dispose:()=>fs.rmSync(root,{recursive:true,force:true})};
}
function empty(result:ExtractProjectResponse) {
  assert.equal(result.complete,false);assert.deepEqual(result.nodes,[]);assert.deepEqual(result.edges,[]);assert.deepEqual(result.processedFiles,[]);assert.deepEqual(result.resolvedImports,[]);
}
function canonical(result:ExtractProjectResponse) {
  assert.equal(result.complete,true,result.diagnostics?.join('\n'));
  return {nodes:result.nodes.map(value=>JSON.stringify(value)).sort(),edges:result.edges.map(value=>JSON.stringify(value)).sort(),processed:result.processedFiles};
}
const routes=(result:ExtractProjectResponse,owner:string)=>result.edges.filter(edge=>edge.type==='HTTP_CALLS'&&edge.sourceQN===`Bindings:bindings.ts#${owner}`).map(edge=>edge.targetQN).sort();

test('array alias indexing materializes each pattern/RHS once with linear actual candidate visits',context=>{
  const measure=(count:number)=>{
    const f=direct(arrayCorpus(count));const start=f.model.work.used;
    const observed=observe(f,()=>{const first=f.run();assert.ok(isObject(first));assert.equal(first.properties.get('method'),'GET');f.run();return f.model.work.used-start;});
    const c=observed.counts;
    assert.equal(c.patterns,1);assert.equal(c.patternWidth,count);assert.equal(c.arrays,1);assert.equal(c.arrayWidth,count);
    assert.ok(c.patternCandidates<=count*4,`binding candidate accesses ${c.patternCandidates}/${count}`);
    assert.ok(c.arrayCandidates<=count*4,`RHS candidate accesses ${c.arrayCandidates}/${count}`);
    return {work:observed.value,...c};
  };
  const small=measure(200),large=measure(600);
  assert.ok(large.work<=small.work*4,JSON.stringify({small,large}));context.diagnostic(JSON.stringify({small,large}));
});

test('object alias selection indexes one source property view instead of scanning every property per binding',context=>{
  const measure=(count:number)=>{
    const f=direct(objectCorpus(count));const start=f.model.work.used;
    const observed=observe(f,()=>{const value=f.run();assert.ok(isObject(value));assert.equal(value.properties.get('method'),'GET');return f.model.work.used-start;});
    assert.equal(observed.counts.objects,1);assert.equal(observed.counts.objectWidth,count);
    assert.ok(observed.counts.objectCandidates<=count*5,JSON.stringify(observed.counts));
    return {work:observed.value,...observed.counts};
  };
  const small=measure(100),large=measure(300);assert.ok(large.work<=small.work*4,JSON.stringify({small,large}));context.diagnostic(JSON.stringify({small,large}));
});

test('structural binding admission refuses and poisons before materializing a wide pattern or RHS',()=>{
  const source=arrayCorpus(200);const baseline=direct(source).model.work.used;
  for(const control of [{maxArrayReferences:0},{maxWork:baseline+1}] satisfies ExtractionWorkControl[]) {
    const f=direct(source,control);const observed=observe(f,()=>assert.throws(f.run,ExtractionWorkLimitError));
    assert.equal(observed.counts.patterns,0);assert.equal(observed.counts.arrays,0);
    assert.throws(()=>f.model.work.charge(0),ExtractionWorkLimitError);
  }
  let cancel=false;const f=direct(source,{shouldCancel:()=>cancel});cancel=true;
  const observed=observe(f,()=>assert.throws(f.run,(error:unknown)=>error instanceof ExtractionWorkLimitError&&error.reason==='cancelled'));
  assert.equal(observed.counts.patterns,0);assert.equal(observed.counts.arrays,0);assert.throws(()=>f.model.work.charge(0),ExtractionWorkLimitError);
});

test('array position/default/rest/spread mutations retain selected-object identity and independent siblings',()=>{
  const f=fixture(`
    export function positional(){const first={method:'GET'},second={method:'POST'};const holder=[first,second];const [,alias]=holder;alias.method='PATCH';fetch('/api/first',first);return fetch('/api/second',second);}
    export function defaulted(){const options={method:'GET'};const [alias=options]=[];alias.method='DELETE';return fetch('/api/default',options);}
    export function rest(){const options={method:'GET'};const holder=[null,options];const [,...tail]=holder;tail[0].method='DELETE';return fetch('/api/rest',options);}
    export function spread(items){const options={method:'GET'};const holder=[...items,options];const [alias]=holder;alias.method='DELETE';return fetch('/api/spread',options);}
    export function immutable(){const options={method:'GET'};const holder=[options];const [alias]=holder;return fetch('/api/immutable',options);}
    export function shadow(){const options={method:'GET'};{const options={method:'POST'};const [alias]=[options];alias.method='PATCH';}return fetch('/api/shadow',options);}
  `);
  try{const result=f.run().result;canonical(result);assert.deepEqual(routes(result,'positional'),['route:*:GET:/api/first']);for(const name of ['defaulted','rest','spread'])assert.deepEqual(routes(result,name),[],name);assert.deepEqual(routes(result,'immutable'),['route:*:GET:/api/immutable']);assert.deepEqual(routes(result,'shadow'),['route:*:GET:/api/shadow']);}
  finally{f.dispose();}
});

test('structural caches preserve caller-specific environments and reset between models with identical binding names',()=>{
  const source=`function wrapper({path,options}){const holder=[options];const [alias]=holder;const nested={path,alias};return fetch(nested.path,options);}
    export function caller(){wrapper({path:'/api/first',options:{method:'PUT'}});wrapper({path:'/api/second',options:{method:'DELETE'}});}`;
  const f=fixture(source);try{const result=f.run().result;canonical(result);assert.deepEqual(routes(result,'caller'),['route:*:DELETE:/api/second','route:*:PUT:/api/first']);}finally{f.dispose();}
  for(const method of ['GET','POST','GET']) {const f=direct(`const [alias]=[{method:'${method}'}];const probe={method:'${method}'};`);const value=f.run();assert.ok(isObject(value));assert.equal(value.properties.get('method'),method);}
  const f2=direct(`function select({path}){return path;}const probe={};`);const fn=f2.file.getFunctionOrThrow('select');const expression=fn.getBodyOrThrow().getDescendants().find(Node.isReturnStatement)?.getExpression();assert.ok(expression);
  for(const value of ['/one','/two']) {const env=f2.evaluator.bind(fn,[{kind:'object',properties:new Map([['path',value]]),unknownKeys:false}]);assert.equal(f2.evaluator.evaluate(expression,env),value);}
});

test('binding-index work refusal and cancellation discard all extraction output and recover freshly',()=>{
  const f=fixture(arrayCorpus(200)+`const endpoint='/api/healthy';export function load(){return fetch(endpoint);}`);
  try{const healthy=f.run();const expected=canonical(healthy.result);empty(f.run({maxArrayReferences:0}).result);empty(f.run({maxWork:Math.floor(healthy.work/2)}).result);
    let cancel=false;empty(f.run({shouldCancel:()=>cancel,onWork:used=>{if(used>healthy.work/2)cancel=true;}}).result);assert.deepEqual(canonical(f.run().result),expected);assert.equal(f.run().work,healthy.work);}
  finally{f.dispose();}
});
