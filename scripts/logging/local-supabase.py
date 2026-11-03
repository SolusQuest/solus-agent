"""Opt-in issue124 real LOCAL Supabase proof. Never contacts a cloud target.
Original packages/source and exact official SDK archive must be provided privately.
No work occurs on import. All runtime configuration/logs are private0600.
"""
import argparse, hashlib, json, os, pathlib, secrets, shutil, subprocess, sys, time, urllib.request
ROOT=pathlib.Path(__file__).resolve().parents[2]
IMAGE_PINS={'node:24.21.0-bookworm-slim': 'node@sha256:d6aa754f16b3197301076f047b5def2f02ea1dbbc2ca920407d46d7ec7f87b20', 'mcr.microsoft.com/dotnet/runtime-deps:10.0-noble': 'mcr.microsoft.com/dotnet/runtime-deps@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832', 'supabase/postgres:17.6.1.136': 'supabase/postgres@sha256:f371b5f3f2ac0a05703f33d6e6134515fb2498cab708fb948a0aeb7481467c00', 'supabase/gotrue:v2.197.0': 'supabase/gotrue@sha256:1736a63078f5922b198c4cbe50f80ab9a2d3b54fe8b7b6cfb2e9dc5dbbc12c6b', 'postgrest/postgrest:v14.18': 'postgrest/postgrest@sha256:c7cd7e265a85b05248abf53f4473fcec7e327c282d0bd0cf60f1551996d09fcf', 'envoyproxy/envoy:v1.39.1': 'envoyproxy/envoy@sha256:57e14a549d7bd43c8d3f6d03e8cfa653e037d4b38e133acd9b54f38c524401b4', 'caddy:2.10.2': 'caddy@sha256:c3d7ee5d2b11f9dc54f947f68a734c84e9c9666c92c88a7f30b9cba5da182adb'}
CONFIG_SHA256={'envoy.yaml': '3697f23b0be9ec5b829f937c600eb9b878f1f778ab510b42ad5e4f14742447e9', 'cds.yaml': '1d7514b891370ed27c25911df008887402e16ab09273e6e433225bb7f09f7905', 'lds.template.yaml': 'f6b7e2c7a58abf54fe0f5eba6ad144c9f0d2a8c445ab66e05b298fb64bf6fa32', 'docker-entrypoint.sh': '8a0c9503764de32ba39e0d81e73629457b13ec42ef19897e30e4918c996d0508'}
SDK_SHA512='5648e8edbdd8127452828964ca8be631ba19b75aa4020bc3352f3ab861b8b5982d823fd6a3bb3b741ec4e3fdc6c0de9ec8a52e8dcedacccf9e8754250579dc2d'
OFFICIAL_SHA="831d6302c7361c0a5115a91e953bdf084eb85330"

def require_local_endpoint(endpoint):
 if not isinstance(endpoint,str) or not endpoint.startswith('unix:///'):
  raise ValueError('only a local Unix Docker socket is permitted')

def verify_inputs(source,inputs,sdk):
 m=json.loads((ROOT/'scripts/logging/accepted-candidate.json').read_text())['manifest']
 actual=subprocess.check_output(['git','-c','safe.directory='+str(source),'-C',str(source),'rev-parse','HEAD'],text=True).strip()
 if actual!=m['source']['commit']:raise ValueError('source must be exact accepted c96')
 if subprocess.check_output(['git','-c','safe.directory='+str(source),'-C',str(source),'status','--porcelain'],text=True).strip():raise ValueError('accepted source must be clean')
 files=[(p['file'],p['sha256'])for p in m['packages']]+[(m['agentApi']['id']+'.'+m['agentApi']['version']+'.nupkg',m['agentApi']['packageSha256']),('solus-logging-cli-0.1.0-internal.1-linux-x64.tar.gz',m['payload']['sha256'])]
 for name,digest in files:
  if hashlib.sha256((inputs/name).read_bytes()).hexdigest()!=digest:raise ValueError('original archive hash mismatch')
 for name,digest in m['action']['files'].items():
  if hashlib.sha256((source/name).read_bytes()).hexdigest()!=digest:raise ValueError('original Action bytes mismatch')
 if hashlib.sha512(sdk.read_bytes()).hexdigest()!=SDK_SHA512:raise ValueError('exact official SDK109 hash mismatch')

def main():
 parser=argparse.ArgumentParser(description=__doc__)
 for field in ['source','inputs','sdk','cache','output']:parser.add_argument('--'+field,required=True)
 parser.add_argument('--deadline-epoch',type=float,required=True)
 parser.add_argument('--target-budget-remaining',type=int,required=True)
 parser.add_argument('--health-budget-remaining',type=int,required=True)
 args=parser.parse_args()
 if not 35<=args.target_budget_remaining<=100 or not 2<=args.health_budget_remaining<=40 or not time.time()<args.deadline_epoch<=time.time()+2701:raise ValueError('insufficient bounded local window')
 require_local_endpoint(os.environ.get('DOCKER_HOST','unix:///default'))
 context=subprocess.check_output(['docker','context','show'],text=True).strip()
 endpoint=subprocess.check_output(['docker','context','inspect',context,'--format','{{.Endpoints.docker.Host}}'],text=True).strip();require_local_endpoint(endpoint)
 SOURCE=pathlib.Path(args.source).resolve();INPUTS=pathlib.Path(args.inputs).resolve()
 verify_inputs(SOURCE,INPUTS,pathlib.Path(args.sdk).resolve())
 cache=pathlib.Path(args.cache).resolve();cache.mkdir(parents=True,exist_ok=True,mode=0o700)
 for name,digest in CONFIG_SHA256.items():
  f=cache/('official-'+name)
  if not f.exists():
   with urllib.request.urlopen('https://raw.githubusercontent.com/supabase/supabase/'+OFFICIAL_SHA+'/docker/volumes/api/envoy/'+name,timeout=30)as r:data=r.read(1048576)
   f.write_bytes(data);f.chmod(0o600)
  if hashlib.sha256(f.read_bytes()).hexdigest()!=digest:raise ValueError('official gateway config hash mismatch')
 os.umask(0o077)
 HERE=pathlib.Path(args.cache).resolve()
 RUN=pathlib.Path(args.output).resolve()
 RUN.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
 RUN.mkdir(mode=0o700)  # Refuse reuse; a failed run is never silently replaced.
 OWNER=secrets.token_hex(8); PROJECT='solus-l124-'+OWNER
 DEADLINE=time.monotonic()+min(2700,args.deadline_epoch-time.time());CLEANING=False
 LOG=RUN/'commands';LOG.mkdir()
 def write(path,value):
  path.write_text(value if isinstance(value,str) else json.dumps(value,indent=2)+'\n');path.chmod(0o600)
 def command(name,args,input=None,timeout=1800,check=True):
  if not CLEANING:timeout=max(1,min(timeout,DEADLINE-time.monotonic()))
  r=subprocess.run(args,input=input,capture_output=True,timeout=timeout)
  write(LOG/(name+'.stdout'),r.stdout.decode(errors='replace'));write(LOG/(name+'.stderr'),r.stderr.decode(errors='replace'))
  if check and r.returncode:raise RuntimeError(name+': exit '+str(r.returncode))
  return r
 def compose(*args,**kw):return command('compose-'+args[0]+'-'+str(time.time_ns()),['docker','compose','-p',PROJECT,'-f',str(RUN/'compose.json'),*args],**kw)
 labels={'com.solus.local.issue':'124','com.solus.local.owner':OWNER}
 refs=['node:24.21.0-bookworm-slim','mcr.microsoft.com/dotnet/runtime-deps:10.0-noble','supabase/postgres:17.6.1.136','supabase/gotrue:v2.197.0','postgrest/postgrest:v14.18','envoyproxy/envoy:v1.39.1','caddy:2.10.2']
 images={}
 for ref in refs:
  raw=json.loads(command('image-'+str(len(images)),['docker','image','inspect',IMAGE_PINS[ref]]).stdout)[0]
  images[ref]={'digest':raw['RepoDigests'][0],'id':raw['Id'],'architecture':raw['Architecture']}
 write(RUN/'images.json',images)
 write(RUN/'ownership.json',{'owner':OWNER,'project':PROJECT,'labels':labels,'networkInternal':True,'publishedPorts':[],'maxTelemetryRows':4,'targetBusinessRemainingAtAdmission':args.target_budget_remaining,'healthRemainingAtAdmission':args.health_budget_remaining,'globalDeadlineEpoch':args.deadline_epoch,'noAggregateReset':True})
 keyjs=r'''const c=require('node:crypto');const k=c.generateKeyPairSync('ec',{namedCurve:'P-256'}),kid=c.randomUUID(),secret=c.randomBytes(32).toString('hex');
 const priv={...k.privateKey.export({format:'jwk'}),kid,alg:'ES256',use:'sig',key_ops:['sign','verify'],ext:true},pub={...k.publicKey.export({format:'jwk'}),kid,alg:'ES256',use:'sig',key_ops:['verify'],ext:true};
 const b=v=>Buffer.from(JSON.stringify(v)).toString('base64url');
 function jwt(role,ec){const data=b({alg:ec?'ES256':'HS256',typ:'JWT',...(ec?{kid}:{})})+'.'+b({role,iss:'supabase',iat:Math.floor(Date.now()/1000),exp:Math.floor(Date.now()/1000)+7200});return data+'.'+(ec?c.sign('sha256',Buffer.from(data),{key:k.privateKey,dsaEncoding:'ieee-p1363'}):c.createHmac('sha256',secret).update(data).digest()).toString('base64url');}
 const opaque=p=>{const v=p+c.randomBytes(17).toString('base64url').slice(0,22);return v+'_'+c.createHash('sha256').update('supabase-self-hosted|'+v).digest('base64url').slice(0,8);};
 console.log(JSON.stringify({secret,privateKeys:JSON.stringify([priv,{kty:'oct',k:Buffer.from(secret).toString('base64url'),alg:'HS256'}]),jwks:JSON.stringify({keys:[pub,{kty:'oct',k:Buffer.from(secret).toString('base64url'),alg:'HS256'}]}),anon:jwt('anon',false),service:jwt('service_role',false),anonEC:jwt('anon',true),serviceEC:jwt('service_role',true),publishable:opaque('sb_publishable_'),serverSecret:opaque('sb_secret_')}));'''
 keys=json.loads(subprocess.check_output(['node','-e',keyjs]));password=secrets.token_hex(24)
 write(RUN/'bootstrap-private.json',keys)
 tls=RUN/'tls';tls.mkdir()
 command('ca',['openssl','req','-x509','-newkey','rsa:3072','-nodes','-keyout',str(tls/'ca.key'),'-out',str(tls/'ca.crt'),'-days','2','-subj','/CN='+PROJECT+'-ca'])
 command('server-csr',['openssl','req','-newkey','rsa:2048','-nodes','-keyout',str(tls/'server.key'),'-out',str(tls/'server.csr'),'-subj','/CN=gateway'])
 write(tls/'server.ext','subjectAltName=DNS:gateway,DNS:localhost,IP:127.0.0.1\nextendedKeyUsage=serverAuth\nbasicConstraints=critical,CA:FALSE\n')
 command('server-sign',['openssl','x509','-req','-in',str(tls/'server.csr'),'-CA',str(tls/'ca.crt'),'-CAkey',str(tls/'ca.key'),'-CAcreateserial','-out',str(tls/'server.crt'),'-days','2','-extfile',str(tls/'server.ext')])
 command('tls-chain-verify',['openssl','verify','-CAfile',str(tls/'ca.crt'),'-purpose','sslserver',str(tls/'server.crt')])
 command('tls-host-verify',['node','-e',"const {X509Certificate}=require('node:crypto'),fs=require('node:fs');const c=new X509Certificate(fs.readFileSync(process.argv[1]));if(c.checkHost('gateway')!=='gateway')process.exit(1);console.log('gateway SAN verified');",str(tls/'server.crt')])
 work=RUN/'work';work.mkdir();envoy=RUN/'envoy';envoy.mkdir()
 for name in ['envoy.yaml','cds.yaml','lds.template.yaml','docker-entrypoint.sh']:shutil.copyfile(HERE/('official-'+name),envoy/name)
 write(RUN/'Caddyfile','{\n admin off\n auto_https off\n}\nhttps://gateway {\n tls /tls/server.crt /tls/server.key\n handle_path /artifact/* {\n root * /artifacts\n file_server\n }\n handle {\n reverse_proxy api-gw:8000\n }\n}\n')
 build=RUN/'build';build.mkdir();sdk=pathlib.Path(args.sdk).resolve();selection={'hash':SDK_SHA512}
 assert hashlib.sha512(sdk.read_bytes()).hexdigest()==selection['hash'];os.link(sdk,build/'sdk.tar.gz')
 write(build/'Dockerfile',f'''FROM {images[refs[0]]['digest']} AS node
 FROM {images[refs[1]]['digest']}
 RUN apt-get update && apt-get install -y --no-install-recommends git unzip libatomic1 && rm -rf /var/lib/apt/lists/*
 COPY --from=node /usr/local/bin/node /usr/local/bin/node
 ADD sdk.tar.gz /usr/share/dotnet/
 ENV DOTNET_ROOT=/usr/share/dotnet PATH=/usr/share/dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin DOTNET_EnableDiagnostics=0 DOTNET_CLI_TELEMETRY_OPTOUT=1
 RUN test "$(dotnet --version)" = 10.0.109 && test "$(node --version)" = v24.21.0
 LABEL com.solus.local.issue=124 com.solus.local.owner={OWNER}
 ''')
 runner_image=PROJECT+'-runner:local'
 command('build-runner',['docker','build','--platform','linux/amd64','-t',runner_image,str(build)],timeout=1800)
 def svc(image,mem='256m',cpu='.4'):
  return {'image':image,'labels':labels,'mem_limit':mem,'cpus':cpu,'pids_limit':256,'restart':'no'}
 def bind(source,target,ro=True):return {'type':'bind','source':str(source.resolve()),'target':target,'read_only':ro}
 write(RUN/'required-roles.sql',"\\set pgpass `echo \"$POSTGRES_PASSWORD\"`\nALTER USER authenticator WITH PASSWORD :'pgpass';\nALTER USER supabase_auth_admin WITH PASSWORD :'pgpass';\n")
 db=svc(images[refs[2]]['digest'],'768m','1');db.update({'environment':{'POSTGRES_PASSWORD':password,'PGPASSWORD':password,'POSTGRES_DB':'postgres','PGDATABASE':'postgres','PGPORT':'5432','POSTGRES_PORT':'5432','POSTGRES_HOST':'/var/run/postgresql','JWT_EXP':'3600'},'volumes':['db-data:/var/lib/postgresql/data','db-config:/etc/postgresql-custom',bind(RUN/'required-roles.sql','/docker-entrypoint-initdb.d/init-scripts/99-roles.sql')],'command':['postgres','-c','config_file=/etc/postgresql/postgresql.conf','-c','log_min_messages=fatal']})
 authenv={'GOTRUE_API_HOST':'0.0.0.0','GOTRUE_API_PORT':'9999','API_EXTERNAL_URL':'https://gateway/auth/v1','GOTRUE_DB_DRIVER':'postgres','GOTRUE_DB_DATABASE_URL':'postgres://supabase_auth_admin:'+password+'@db:5432/postgres','GOTRUE_SITE_URL':'https://gateway','GOTRUE_DISABLE_SIGNUP':'false','GOTRUE_JWT_ADMIN_ROLES':'service_role','GOTRUE_JWT_AUD':'authenticated','GOTRUE_JWT_DEFAULT_GROUP_NAME':'authenticated','GOTRUE_JWT_EXP':'3600','GOTRUE_JWT_SECRET':keys['secret'],'GOTRUE_JWT_KEYS':keys['privateKeys'],'GOTRUE_JWT_ISSUER':'https://gateway/auth/v1','GOTRUE_EXTERNAL_EMAIL_ENABLED':'true','GOTRUE_EXTERNAL_ANONYMOUS_USERS_ENABLED':'false','GOTRUE_MAILER_AUTOCONFIRM':'true','GOTRUE_RATE_LIMIT_EMAIL_SENT':'100','GOTRUE_RATE_LIMIT_SIGN_UPS':'100','GOTRUE_SMTP_MAX_FREQUENCY':'0s'}
 auth=svc(images[refs[3]]['digest']);auth['environment']=authenv
 short=svc(images[refs[3]]['digest']);short['environment']={**authenv,'GOTRUE_JWT_EXP':'60'}
 rest=svc(images[refs[4]]['digest'],'128m');rest['environment']={'PGRST_DB_URI':'postgres://authenticator:'+password+'@db:5432/postgres','PGRST_DB_SCHEMAS':'public','PGRST_DB_ANON_ROLE':'anon','PGRST_JWT_SECRET':keys['jwks'],'PGRST_DB_MAX_ROWS':'5','PGRST_ADMIN_SERVER_PORT':'3001','PGRST_DB_USE_LEGACY_GUCS':'false'}
 api=svc(images[refs[5]]['digest']);api.update({'entrypoint':['/bin/sh','/docker-entrypoint.sh'],'command':['--concurrency','1'],'volumes':[bind(envoy/n,'/etc/envoy/'+n)for n in ['envoy.yaml','cds.yaml','lds.template.yaml']]+[bind(envoy/'docker-entrypoint.sh','/docker-entrypoint.sh')],'environment':{'ANON_KEY':keys['anon'],'SERVICE_ROLE_KEY':keys['service'],'ANON_KEY_ASYMMETRIC':keys['anonEC'],'SERVICE_ROLE_KEY_ASYMMETRIC':keys['serviceEC'],'SUPABASE_PUBLISHABLE_KEY':keys['publishable'],'SUPABASE_SECRET_KEY':keys['serverSecret'],'SUPABASE_PUBLIC_URL':'https://gateway','DASHBOARD_USERNAME':'local-'+OWNER,'DASHBOARD_PASSWORD':secrets.token_hex(16)}})
 gateway=svc(images[refs[6]]['digest'],'128m','.2');gateway['volumes']=[bind(RUN/'Caddyfile','/etc/caddy/Caddyfile'),bind(tls/'server.crt','/tls/server.crt'),bind(tls/'server.key','/tls/server.key'),bind(INPUTS/'solus-logging-cli-0.1.0-internal.1-linux-x64.tar.gz','/artifacts/cli.tar.gz')]
 runner=svc(runner_image,'2g','2');runner.update({'platform':'linux/amd64','command':['sleep','infinity'],'working_dir':'/work','environment':{'NODE_EXTRA_CA_CERTS':'/trust/ca.crt','SSL_CERT_FILE':'/trust/ca.crt','DOTNET_EnableDiagnostics':'0'},'volumes':[bind(SOURCE,'/source'),bind(INPUTS,'/inputs'),bind(work,'/work',False),bind(tls/'ca.crt','/trust/ca.crt'),bind(ROOT/'scripts/logging/local-supabase-driver.mjs','/harness/driver.mjs')]+[bind(ROOT/'scripts/logging'/n,'/current/scripts/logging/'+n)for n in ['live-qualification.ts','accepted-candidate.json']]})
 cfg={'services':{'db':db,'auth':auth,'auth-short':short,'rest':rest,'api-gw':api,'gateway':gateway,'runner':runner},'networks':{'default':{'internal':True,'labels':labels}},'volumes':{'db-data':{'labels':labels},'db-config':{'labels':labels}}}
 write(RUN/'compose.json',cfg)
 client={'publishable':keys['publishable'],'passwordA':secrets.token_hex(20),'passwordB':secrets.token_hex(20),'emailA':'l124-a-'+OWNER+'@example.test','emailB':'l124-b-'+OWNER+'@example.test','owner':OWNER,'targetBudgetRemaining':args.target_budget_remaining,'healthBudgetRemaining':args.health_budget_remaining}
 write(work/'client-private.json',client)
 write(work/'phase-plan.json',{'owner':OWNER,'eventS':str(__import__('uuid').uuid4()),'eventP':str(__import__('uuid').uuid4()),'eventT':str(__import__('uuid').uuid4()),'eventN':str(__import__('uuid').uuid4())})
 started=time.monotonic();result={'state':'IN_PROGRESS','owner':OWNER,'project':PROJECT}
 def containers():
  ids=command('owned-list',['docker','ps','-aq','--filter','label=com.solus.local.owner='+OWNER]).stdout.decode().split();return ids
 def cleanup():
  records=[]
  for cid in containers():
   q=json.loads(command('cleanup-inspect-'+cid,['docker','inspect',cid]).stdout)[0]
   assert q['Config']['Labels'].get('com.solus.local.owner')==OWNER and q['Name'].startswith('/'+PROJECT+'-')
   command('cleanup-rm-'+cid,['docker','rm','-f',cid]);records.append({'containerId':cid,'name':q['Name'],'removed':True})
  for kind in ['network','volume']:
   ids=command('owned-'+kind,['docker',kind,'ls','-q','--filter','label=com.solus.local.owner='+OWNER]).stdout.decode().split()
   for value in ids:
    q=json.loads(command('inspect-'+kind,['docker',kind,'inspect',value]).stdout)[0];assert q['Labels'].get('com.solus.local.owner')==OWNER and q['Name'].startswith(PROJECT)
    command('remove-'+kind,['docker',kind,'rm',value]);records.append({kind:q['Name'],'removed':True})
  assert not containers()
  for kind in ['network','volume']:assert not command('verify-no-'+kind,['docker',kind,'ls','-q','--filter','label=com.solus.local.owner='+OWNER]).stdout.strip()
  image=json.loads(command('cleanup-image-inspect',['docker','image','inspect',runner_image]).stdout)[0];assert image['Config']['Labels'].get('com.solus.local.owner')==OWNER
  command('cleanup-own-image',['docker','image','rm',runner_image]);records.append({'image':runner_image,'removed':True})
  write(RUN/'cleanup.json',{'ownedResources':records,'allOwnedContainersGone':True,'allOwnedNetworksVolumesImagesGone':True,'noGlobalPrune':True,'officialImagesRetained':True})
 try:
  compose('up','-d','db')
  for i in range(40):
   q=compose('exec','-T','db','pg_isready','-h','127.0.0.1','-U','postgres',check=False,timeout=10)
   if q.returncode==0:break
   ids=containers()
   if ids and command('db-state',['docker','inspect','--format','{{.State.Status}}',ids[0]]).stdout.strip() in [b'exited',b'dead']:raise RuntimeError('owned-database-exited-before-ready')
   time.sleep(2)
  else:raise RuntimeError('database-readiness-limit')
  q=compose('exec','-T','db','psql','-h','127.0.0.1','-U','postgres','-At','-v','ON_ERROR_STOP=1','-c',"select 'auth.uid()='||'auth.uid()'::regprocedure::text;select rolname from pg_roles where rolname in ('authenticated','anon','supabase_auth_admin','authenticator') order by rolname;")
  assert b'auth.uid()=' in q.stdout and b'authenticated' in q.stdout
  compose('exec','-T','db','psql','-h','127.0.0.1','-U','postgres','-v','ON_ERROR_STOP=1',input=(ROOT/'database/logging/001_telemetry.sql').read_bytes())
  compose('up','-d','auth','rest','api-gw','gateway','runner')
  compose('exec','-T','runner','node','/harness/driver.mjs','prepare',timeout=600)
  compose('exec','-T','runner','node','/harness/driver.mjs','main',timeout=600)
  compose('up','-d','auth-short')
  compose('exec','-T','runner','node','/harness/driver.mjs','expiry',timeout=180)
  compose('stop','rest')
  compose('exec','-T','runner','node','/harness/driver.mjs','outage',timeout=90)
  compose('start','rest')
  compose('exec','-T','runner','node','/harness/driver.mjs','final',timeout=180)
  result.update({'state':'PASS','elapsedSeconds':round(time.monotonic()-started,2)})
 except Exception as error:
  result.update({'state':'FAILED','errorClass':type(error).__name__,'fixedStage':str(error)[:120],'elapsedSeconds':round(time.monotonic()-started,2)})
 finally:
  CLEANING=True
  for cid in containers():command('container-log-'+cid,['docker','logs',cid],check=False,timeout=30)
  write(RUN/'result.json',result)
  cleanup()
 print(json.dumps({'state':result['state'],'resultFile':str(RUN/'result.json'),'cleanupFile':str(RUN/'cleanup.json')}))
 sys.exit(0 if result['state']=='PASS' else 1)

if __name__=='__main__':main()
