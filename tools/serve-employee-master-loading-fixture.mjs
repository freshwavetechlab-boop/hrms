import fs from 'node:fs/promises'
import path from 'node:path'
import {createRequire} from 'node:module'
import {fileURLToPath,pathToFileURL} from 'node:url'
const toolsRoot=path.dirname(fileURLToPath(import.meta.url)),root=path.resolve(toolsRoot,'../payroll-ui')
const require=createRequire(path.join(root,'package.json')),{createServer}=await import(pathToFileURL(require.resolve('vite')).href)
const virtual=path.join(root,'.codex-employee-loading.tsx'),calls=[]
const clients=[{id:42,name:'Research Office · Employees available',code:'RO',isActive:true},{id:43,name:'Regional Office · Employees available',code:'REG',isActive:true},{id:44,name:'Empty client · No employees',code:'EMPTY',isActive:true}]
const employees=[{id:101,clientId:42,employeeCode:'TEST-101',firstName:'Synthetic',lastName:'Employee One'},{id:102,clientId:42,employeeCode:'TEST-102',firstName:'Synthetic',lastName:'Employee Two'},{id:103,clientId:43,employeeCode:'TEST-103',firstName:'Synthetic',lastName:'Other Client'}].map(row=>({...row,isActive:true,workEmail:`employee${row.id}@example.test`,department:'Project services',designation:'Assistant',grade:'',workLocationId:row.clientId===42?1:2,reportingManagerId:0,salaryStructureId:'',annualCtc:0,dateOfJoining:'2026-11-02',portalAccess:true,personalJson:'{}',paymentJson:'{}',salaryJson:'{}',salaryComponents:{},personalDetails:{},paymentDetails:{}}))
const html='<!doctype html><html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head><body><div id="root"></div><script type="module" src="/.codex-employee-loading.tsx"></script></body></html>'
const plugin={name:'employee-loading-isolation',enforce:'pre',resolveId(id){if(id==='/.codex-employee-loading.tsx'||id.replaceAll('\\','/')===virtual.replaceAll('\\','/'))return virtual},async load(id){const clean=id.split('?')[0].replaceAll('\\','/');if(clean===virtual.replaceAll('\\','/'))return fs.readFile(path.join(toolsRoot,'fixtures/EmployeeLoading.fixture.tsx'),'utf8');if(clean.endsWith('/src/services/apiBaseUrl.ts'))return 'export function resolveRuntimeApiBase(){return window.location.origin}'},configureServer(server){server.middlewares.use(async(req,res,next)=>{
 const url=new URL(req.url,'http://127.0.0.1:5202'),route=url.pathname
 const reply=(value,status=200)=>{res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(value))}
 if(route==='/employee-loading-fixture'){res.writeHead(200,{'content-type':'text/html'});res.end(await server.transformIndexHtml(req.url,html));return}
 if(route==='/__fixture/calls')return reply(calls)
 if(!route.startsWith('/api/'))return next()
 const call={method:req.method,path:route,query:url.search,started:Date.now(),finished:null};calls.push(call)
 const finish=(value,status=200)=>{call.finished=Date.now();reply(value,status)}
 if(route==='/api/auth/me')return finish({id:995,email:'employee-ui@example.test',displayName:'Local verifier',isActive:true,mustChangePassword:false,clientId:null,roles:['super_admin'],permissions:['security.manage','employees.view','employees.manage','settings.manage','employee.communication.send'],clientAccessMode:'Legacy',assignedClientIds:[]})
 if(req.method==='GET'&&route==='/api/clients')return finish(clients)
 if(req.method==='GET'&&route==='/api/employees'){setTimeout(()=>finish(employees),1500);return}
 if(req.method==='GET'&&route==='/api/work-locations')return finish([{id:1,clientId:42,name:'New Delhi',isActive:true},{id:2,clientId:43,name:'Regional Office',isActive:true}])
 if(req.method==='GET'&&route==='/api/employees/field-configuration/catalog')return finish({fields:[],forms:[],values:{}})
 if(req.method==='GET'&&route==='/api/employees/profile-completion')return finish(employees.filter(e=>!Number(url.searchParams.get('clientId'))||e.clientId===Number(url.searchParams.get('clientId'))).map(e=>({employeeId:e.id,clientId:e.clientId,missingFields:[],canSendEmail:false,disabledReason:'Synthetic complete profile',additionalMissing:[]})))
 if(req.method==='POST'&&route==='/api/employees/profile-completion/setup'){setTimeout(()=>finish({firstEditEnabled:true,template:{id:1,name:'Synthetic missing information',templateCode:'EMPLOYEE_MISSING_INFORMATION',channel:'Email',isActive:true,subjectTemplate:'Complete your profile',bodyTemplate:'<p>Synthetic template</p>',variables:[{variableKey:'essUrl',fallbackValue:'https://gad-ess.frevo.co.in/'}]}}),4000);return}
 if(req.method==='GET'&&route==='/api/setup'){setTimeout(()=>finish({error:'Synthetic no salary settings'},404),4000);return}
 if(req.method==='GET'&&['/api/dropdowns','/api/employees/manager-users'].includes(route))return finish([])
 return finish({error:'Synthetic fixture has no upstream API, database or mail service.'},req.method==='GET'?404:405)
 })}}
const server=await createServer({root,plugins:[plugin],server:{host:'127.0.0.1',port:5202,strictPort:true,open:false}})
await server.listen();console.log('Synthetic Employee Master: http://127.0.0.1:5202/employee-loading-fixture')
const close=async()=>{await server.close();process.exit()};process.on('SIGINT',close);process.on('SIGTERM',close)
