import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdirSync, copyFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import https from 'node:https';
import http from 'node:http';
import { installScenario } from '/source/scripts/logging/installed.ts';
import { readPackage, checkDependencies } from '/source/scripts/logging/package-archive.ts';
import { equivalentRow, semantic } from '/current/scripts/logging/live-qualification.ts';

process.umask(0o077);
const sha=b=>createHash('sha256').update(b).digest('hex');
const lock=JSON.parse(readFileSync('/current/scripts/logging/accepted-candidate.json'));
const m=lock.manifest, client=JSON.parse(readFileSync('/work/client-private.json'));
const plan=JSON.parse(readFileSync('/work/phase-plan.json'));
const path='/work/private-state.json', phase=process.argv[2];
let state=phase==='prepare'?{requests:[],actions:[],readbacks:[],products:{},proofs:[],started:new Date().toISOString()}:JSON.parse(readFileSync(path));
const save=()=>writeFileSync(path,JSON.stringify(state,null,2)+'\n',{mode:0o600});
function file(name,value){writeFileSync('/work/'+name,JSON.stringify(value,null,2)+'\n',{mode:0o600});}
const decode=t=>JSON.parse(Buffer.from(t.split('.')[1],'base64url'));
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
function check(value,reason){assert.ok(value,reason);state.proofs.push(reason);}
async function request(route,method='GET',body,token,key=client.publishable,short=false){
 assert.ok(state.requests.length<100,'local-request-budget');
 if(route.startsWith('/rest/'))assert.ok((state.actionAttempts??0)+state.requests.filter(v=>v.path.startsWith('/rest/')).length<client.targetBudgetRemaining,'aggregate target remaining');
 if(route.includes('/health'))assert.ok(state.requests.filter(v=>v.path.includes('/health')).length<client.healthBudgetRemaining,'aggregate health remaining');
 const url=new URL(route,short?'http://auth-short:9999':'https://gateway');
 assert.ok(url.origin===(short?'http://auth-short:9999':'https://gateway'));
 const headers={Accept:'application/json'};
 if(key)headers.apikey=key;if(token)headers.Authorization='Bearer '+token;
 if(body!==undefined)headers['Content-Type']='application/json';
 const receipt={index:state.requests.length+1,phase,method,path:url.pathname,status:null,bytes:null,SHA256:null};state.requests.push(receipt);save();
 const value=await new Promise((resolve,reject)=>{
  const req=(short?http:https).request(url,{method,headers,agent:false,signal:AbortSignal.timeout(10000)},async res=>{
   try{let bytes=0,parts=[];for await(const b of res){bytes+=b.length;assert.ok(bytes<=65536,'response-cap');parts.push(b);}
    const raw=Buffer.concat(parts);let parsed;try{parsed=JSON.parse(raw);}catch{parsed=null;}resolve({status:res.statusCode,body:parsed,bytes,hash:sha(raw)});
   }catch(e){reject(e);}
  });req.on('error',reject);req.end(body===undefined?undefined:JSON.stringify(body));
 });
 Object.assign(receipt,{status:value.status,bytes:value.bytes,SHA256:value.hash});save();
 return value;
}
function query(subject,ids,preflight=false){
 const q=new URLSearchParams({select:preflight?'writer_id,event_id':'writer_id,event_id,schema_version,product,product_version,invocation_id,occurred_at,occurred_at_residual_tick,received_at,execution,outcome,usage,extensions',writer_id:'eq.'+subject,event_id:'in.('+ids.join(',')+')',limit:'5'});
 return '/rest/v1/telemetry_events?'+q;
}
const subjects=()=>({A:decode(state.tokens.A).sub,B:decode(state.tokens.B).sub});
async function rows(who,selected,ids,expected,preflight=false){
 const r=await request(query(subjects()[selected],ids,preflight),'GET',undefined,state.tokens[who]);
 assert.equal(r.status,200);assert.ok(Array.isArray(r.body));assert.equal(r.body.length,expected);
 state.readbacks.push({phase,principal:who,selected,ids,rows:r.body,completeMatch:expected===0});save();return r.body;
}
async function action(name,record,who='A',override={}){
 const index=state.actions.length+1;assert.ok(index<=12,'Action-budget');assert.ok((state.actionAttempts??0)+state.requests.filter(v=>v.path.startsWith('/rest/')).length<client.targetBudgetRemaining,'aggregate target remaining');
 state.actionAttempts=(state.actionAttempts??0)+1;save();
 const resultFile='/work/action-'+index+'.json';
 const env={PATH:process.env.PATH,NODE_EXTRA_CA_CERTS:'/trust/ca.crt',SSL_CERT_FILE:'/trust/ca.crt',
  INPUT_ADAPTER:'supabase','INPUT_TELEMETRY-JSON':JSON.stringify(record),'INPUT_SUPABASE-URL':'https://gateway',
  'INPUT_SUPABASE-PUBLISHABLE-KEY':client.publishable,'INPUT_SUPABASE-ACCESS-TOKEN':state.tokens[who],
  'INPUT_STRICT-DELIVERY':'false','INPUT_MAXIMUM-ATTEMPTS':'1','INPUT_TIMEOUT-MS':'10000','INPUT_RETRY-DELAY-MS':'0',
  'INPUT_CLI-VERSION':m.version,'INPUT_CLI-ARTIFACT-URL':'https://gateway/artifact/cli.tar.gz','INPUT_CLI-ARTIFACT-SHA256':m.payload.sha256,
  'INPUT_ARTIFACT-READ-TOKEN':'',RESULT_FILE:resultFile,...override};
 const r=spawnSync(process.execPath,['/work/action-bridge.mjs'],{env,timeout:65000,maxBuffer:8192});
 assert.equal(r.status,0,'warnmode original Action exit');assert.equal(r.signal,null);assert.equal(r.stderr.length,0);
 const result=JSON.parse(readFileSync(resultFile));state.actions.push({index,name,who,eventId:record.eventId,...result});save();return result;
}
function expected(r,status,code='none'){assert.equal(r.deliveryStatus,status);assert.equal(r.diagnosticCode,code);assert.equal(r.attempts,1);}
function compare(list,records,who){for(const row of list){const original=records.find(v=>v.eventId===row.event_id);assert.ok(original);equivalentRow(row,original,subjects()[who]);}state.readbacks.at(-1).completeMatch=true;save();}
async function readiness(short=false){for(let i=0;i<30;i++){
 try{const r=await request(short?'/health':'/auth/v1/health','GET',undefined,undefined,client.publishable,short);if(r.status===200)return;}catch{}
 await sleep(1000);
}throw Error('gateway-auth-readiness');}
try{
 if(phase==='prepare'){
  assert.equal(process.platform,'linux');assert.equal(process.arch,'x64');assert.equal(process.version,'v24.21.0');
  const sdk=spawnSync('dotnet',['--version'],{encoding:'utf8'});assert.equal(sdk.status,0);assert.equal(sdk.stdout.trim(),'10.0.109');
  assert.equal(spawnSync('git',['-c','safe.directory=/source','-C','/source','rev-parse','HEAD'],{encoding:'utf8'}).stdout.trim(),m.source.commit);
  for(const[f,h]of Object.entries(m.action.files))assert.equal(sha(readFileSync('/source/'+f)),h);
  mkdirSync('/work/feed');mkdirSync('/work/baseline');mkdirSync('/work/installed');
  const packages=[...m.packages,{id:m.agentApi.id,version:m.agentApi.version,file:m.agentApi.id+'.'+m.agentApi.version+'.nupkg',sha256:m.agentApi.packageSha256,dllSha256:m.agentApi.dllSha256,dependencies:{}}];
  state.acquisition=[];
  for(const p of packages){const data=readFileSync('/inputs/'+p.file);assert.equal(sha(data),p.sha256);const dest=(p.id===m.agentApi.id?'/work/baseline/':'/work/feed/')+p.file;copyFileSync('/inputs/'+p.file,dest);const info=readPackage(dest,p.id,p.version);assert.equal(info.dllSha256,p.dllSha256);checkDependencies(info.dependencies,p.dependencies);state.acquisition.push({id:p.id,version:p.version,bytes:data.length,SHA256:p.sha256,dllSHA256:p.dllSha256});}
  assert.equal(sha(readFileSync('/inputs/solus-logging-cli-0.1.0-internal.1-linux-x64.tar.gz')),m.payload.sha256);
  state.installed=[];
  for(const name of ['Scribe','Apr']){
   const v=installScenario('/source/tests/TelemetryConsumers/'+name,'/work/installed',name,'/work/feed','/work/baseline',m,{packageSha256:m.agentApi.packageSha256,dllSha256:m.agentApi.dllSha256});
   const shape=name.toLowerCase(),event=shape==='scribe'?plan.eventS:plan.eventP,marker='/work/'+shape+'.marker';
   const r=spawnSync('dotnet',[v.executable,event,JSON.stringify({team:'local-synthetic',qualification:{issue:124,owner:client.owner},nested:[true,null,{text:'你好😀'}]}),marker],{cwd:v.directory,env:v.env,timeout:30000,maxBuffer:300000});
   assert.equal(r.status,shape==='apr'?7:0);assert.equal(r.signal,null);assert.equal(readFileSync(marker,'utf8'),shape+'-once\n');
   const output=JSON.parse(r.stdout);assert.equal(output.telemetryDiagnostic,'None');assert.equal(output.business.status,shape==='apr'?'failed':'succeeded');
   const record=JSON.parse(output.telemetryJson);assert.equal(record.eventId,event);assert.equal(record.usage.runs.length,3);
   state.products[shape]=record;state.installed.push({shape,businessExit:r.status,once:1,resolved:v.resolved,wireSHA256:sha(output.telemetryJson),exactExecutedDLLs:true});
  }
  const usage=JSON.stringify([state.products.scribe.usage,state.products.apr.usage]);check(usage.includes('9007199254740993'),'exact unsafe-integer decimal string');check(usage.includes('null'),'nullable usage facts retained');
  writeFileSync('/work/action-bridge.mjs',`import {runAction} from '/source/actions/logging/main.mjs';\nimport {writeFileSync} from 'node:fs';\nconst result=await runAction(process.env,()=>{});\nwriteFileSync(process.env.RESULT_FILE,JSON.stringify(result)+'\\n',{mode:0o600});\nprocess.exitCode=result.exitCode;\n`,{mode:0o600});
  state.platform={os:process.platform,arch:process.arch,node:process.version,sdk:sdk.stdout.trim()};save();
 }else if(phase==='main'){
  await readiness();state.tokens={};
  for(const who of ['A','B']){
   const body={email:client['email'+who],password:client['password'+who]};
   const signup=await request('/auth/v1/signup','POST',body);assert.equal(signup.status,200);
   const login=await request('/auth/v1/token?grant_type=password','POST',body);assert.equal(login.status,200);assert.equal(typeof login.body.access_token,'string');
   state.tokens[who]=login.body.access_token;const claim=decode(state.tokens[who]);assert.equal(claim.role,'authenticated');assert.equal(claim.iss,'https://gateway/auth/v1');assert.ok(claim.exp>Math.floor(Date.now()/1000)+1800);assert.equal(JSON.parse(Buffer.from(state.tokens[who].split('.')[0],'base64url')).alg,'ES256');
  }
  assert.notEqual(subjects().A,subjects().B);save();
  const s=state.products.scribe,p=state.products.apr,ids=[plan.eventS,plan.eventP],all=[...ids,plan.eventT,plan.eventN];
  await rows('A','A',all,0,true);await rows('B','B',all,0,true);
  expected(await action('A Scribe stored',s),'stored');expected(await action('A Apr stored preserves business7',p),'stored');
  const first=await rows('A','A',ids,2);compare(first,[s,p],'A');await rows('B','A',ids,0);
  expected(await action('A Scribe duplicate',s),'duplicate');expected(await action('A Apr duplicate',p),'duplicate');
  let current=await rows('A','A',ids,2);compare(current,[s,p],'A');check(semantic(current.sort((a,b)=>a.event_id.localeCompare(b.event_id)),first.sort((a,b)=>a.event_id.localeCompare(b.event_id))),'duplicates retain every field and received_at');
  expected(await action('A Scribe collision',{...s,productVersion:'local-collision-124'}),'failed','identity_conflict');expected(await action('A Apr collision',{...p,productVersion:'local-collision-124'}),'failed','identity_conflict');
  current=await rows('A','A',ids,2);compare(current,[s,p],'A');check(semantic(current.sort((a,b)=>a.event_id.localeCompare(b.event_id)),first),'collisions never overwrite');
  expected(await action('B same event ownwriter',s,'B'),'stored');compare(await rows('B','B',[plan.eventS],1),[s],'B');await rows('B','A',ids,0);await rows('A','B',ids,0);compare(await rows('A','A',ids,2),[s,p],'A');
  const t={...s,eventId:plan.eventT,occurredAt:'2026-01-02T03:04:05.1234567Z'};state.products.timestampDerivative=t;expected(await action('local timestamp derivative',t),'stored');const tr=await rows('A','A',[plan.eventT],1);compare(tr,[t],'A');assert.equal(tr[0].occurred_at_residual_tick,7);
  // Denial probes reuse an existing SAME-writer key: unexpected acceptance can only
  // duplicate, never add a fifth row. N stays a never-written sentinel.
  const n=s;state.products.denied=n;save();
  const parts=state.tokens.A.split('.');parts[2]=(parts[2][0]==='A'?'B':'A')+parts[2].slice(1);const bad=parts.join('.');
  expected(await action('invalid realJWT signature',n,'A',{'INPUT_SUPABASE-ACCESS-TOKEN':bad}),'failed','authentication_failed');
  expected(await action('wrong publishable key',n,'A',{'INPUT_SUPABASE-PUBLISHABLE-KEY':'sb_publishable_UNREGISTERED_124_TEST'}),'failed','authentication_failed');
  for(const [name,route,method,body,token,key]of [
   ['anonymous GET',query(subjects().A,ids),'GET',undefined,undefined,client.publishable],
   ['anonymous RPC','/rest/v1/rpc/ingest_telemetry','POST',{event:n},undefined,client.publishable],
   ['missing APIkey',query(subjects().A,ids),'GET',undefined,state.tokens.A,''],
   ['invalidJWT GET',query(subjects().A,ids),'GET',undefined,bad,client.publishable],
   ['wrongkey GET',query(subjects().A,ids),'GET',undefined,state.tokens.A,'sb_publishable_UNREGISTERED_124_TEST']]){
   const r=await request(route,method,body,token,key);check([401,403].includes(r.status),name+' denied with '+r.status);
  }save();
 }else if(phase==='expiry'){
  await readiness(true);const r=await request('/token?grant_type=password','POST',{email:client.emailA,password:client.passwordA},undefined,undefined,true);assert.equal(r.status,200);const token=r.body.access_token,c=decode(token);assert.equal(c.sub,subjects().A);assert.ok(c.exp*1000>Date.now()&&c.exp*1000-Date.now()<=120000);
  const before=await request(query(subjects().A,[plan.eventS]),'GET',undefined,token);assert.equal(before.status,200);assert.equal(before.body.length,1);state.expiredTokenEvidence={source:'real GoTrue samekeys short-expiry instance/password grant',subjectMatchesA:true,usableBeforeExpiry:true,remainingAtCaptureMs:c.exp*1000-Date.now()};save();
  // PostgREST v14 documents 30 seconds of exp/iat/nbf clock-skew tolerance.
  await sleep(Math.max(0,c.exp*1000-Date.now()+35000));state.expiredTokenEvidence.documentedClockSkewSeconds=30;state.expiredTokenEvidence.pastExpBeforeActionMs=Date.now()-c.exp*1000;save();
  expected(await action('expired realGoTrueJWT beyond documented skew',state.products.denied,'A',{'INPUT_SUPABASE-ACCESS-TOKEN':token}),'failed','authentication_failed');
  const after=await request(query(subjects().A,[plan.eventS]),'GET',undefined,token);assert.equal(after.status,401);state.expiredTokenEvidence.deniedAfterActualExpiry=true;save();
 }else if(phase==='outage'){
  const r=await action('owned REST backend stopped',state.products.denied);assert.equal(r.deliveryStatus,'failed');assert.equal(r.diagnosticCode,'attempts_exhausted');assert.equal(r.attempts,1);save();
 }else if(phase==='final'){
  for(let i=0;i<20;i++){const r=await request(query(subjects().A,[plan.eventS]),'GET',undefined,state.tokens.A);if(r.status===200)break;if(i===19)throw Error('REST-restart-readiness');await sleep(1000);}
  const beforeA=state.readbacks.find(v=>v.phase==='main'&&v.principal==='A'&&v.selected==='A'&&v.rows.length===2).rows;
  const beforeT=state.readbacks.find(v=>v.phase==='main'&&v.ids.length===1&&v.ids[0]===plan.eventT).rows;
  const beforeB=state.readbacks.find(v=>v.phase==='main'&&v.principal==='B'&&v.selected==='B'&&v.rows.length===1).rows;
  const ids=[plan.eventS,plan.eventP,plan.eventT,plan.eventN];const a=await rows('A','A',ids,3);compare(a,[state.products.scribe,state.products.apr,state.products.timestampDerivative],'A');const b=await rows('B','B',ids,1);compare(b,[state.products.scribe],'B');await rows('A','B',ids,0);await rows('B','A',ids,0);
  const ordered=v=>[...v].sort((x,y)=>x.event_id.localeCompare(y.event_id));check(semantic(ordered(a),ordered([...beforeA,...beforeT]))&&semantic(b,beforeB),'all denial/outage probes retain complete row set and received_at');
  assert.equal(state.actions.length,12);assert.ok(state.requests.length<=100);check(!a.concat(b).some(r=>r.event_id===plan.eventN),'unused sentinel absent; total4 owned visible rows');
  file('proof.json',{profile:'LOCAL Docker genuine Supabase Auth/Envoy/PostgREST/Postgres',state:'PASS',source:m.source,action:m.action,version:m.version,candidate:m.candidateSha256,packageSet:m.packageSetSha256,CLI:m.payload,registryAccessVerified:false,hostedGitHubExecution:null,platform:state.platform,acquisition:state.acquisition,installed:state.installed,auth:{realGoTrue:true,algorithm:'ES256',distinctOrdinaryUsers:true,subjectsSHA256:Object.fromEntries(Object.entries(subjects()).map(([k,v])=>[k,sha(v)])),expiry:state.expiredTokenEvidence},actions:state.actions,requestCount:state.requests.length,requestMetadata:state.requests,fullReadbackComparisons:state.readbacks.map(({rows,...v})=>({...v,rows:rows.length})),proofs:state.proofs,rows:{A:3,B:1,total4:4,unusedSentinel:0},timestampFixture:'additional synthetic derivative, not another product invocation',secretsOrRawRowsIncluded:false,noCloudNoRegistryNoModel:true});
 }else throw Error('unknown phase');
 save();console.log(JSON.stringify({phase,state:'PASS',actions:state.actions.length,requests:state.requests.length}));
}catch(error){save();file('failure-'+phase+'.json',{phase,errorName:error.name,message:error.message,stack:error.stack});console.error(JSON.stringify({phase,state:'FAILED',errorName:error.name,privateFailureFile:'failure-'+phase+'.json'}));process.exitCode=1;}
