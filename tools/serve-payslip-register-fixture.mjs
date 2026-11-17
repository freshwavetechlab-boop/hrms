// Isolated synthetic mounted Register fixture. No upstream, database or SMTP.
// Every /api request terminates in this in-memory server.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath,pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'
const toolsRoot=path.dirname(fileURLToPath(import.meta.url))
const root=path.resolve(toolsRoot,'../payroll-ui')
const require=createRequire(path.join(root,'package.json'))
const {createServer}=await import(pathToFileURL(require.resolve('vite')).href)
const fixture=path.join(root,'.codex-payslip-register-fixture.tsx')
const clients=[{id:907,name:'Synthetic IIM client',code:'SYNTH907',isActive:true},{id:908,name:'Synthetic second client',code:'SYNTH908',isActive:true}]
const employee=(index,client)=>({id:index,employeeId:client*100+index,employeeCode:'SYNTH-'+client+'-'+index,employeeName:'Synthetic employee '+index,department:'Synthetic department',presentDays:30,payableDays:30,grossPay:1000,statutoryDeductions:100,oneTimeEarnings:25,oneTimeDeductions:0,manualTds:0,netPay:925,varianceReason:'',isSkipped:false,paymentStatus:'Pending'})
const runs=[{id:9001,clientId:907,clientName:clients[0].name,payPeriod:'2026-10',status:'Approved',employees:Array.from({length:52},(_,i)=>employee(i+1,907))},
 {id:9002,clientId:908,clientName:clients[1].name,payPeriod:'2026-09',status:'Approved',employees:Array.from({length:2},(_,i)=>employee(i+1,908))}]
const moneyWitness=JSON.stringify(runs)
let failNext=true,delayNext=false,blockedWrites=0
const calls=[]
fs.copyFileSync(path.join(toolsRoot,'fixtures/PayslipRegister.fixture.tsx'),fixture)
const html='<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module" src="/.codex-payslip-register-fixture.tsx"></script></body></html>'
const plugin={
 name:'synthetic-register-only',enforce:'pre',
 load(id){if(id.split('?')[0].replaceAll('\\','/').endsWith('/src/services/apiBaseUrl.ts'))return 'export function resolveRuntimeApiBase(){return window.location.origin}'},
 configureServer(server){server.middlewares.use(async(req,res,next)=>{
  const u=new URL(req.url,'http://127.0.0.1:5196'),p=u.pathname
  const reply=(value,status=200)=>{res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(value))}
  if(p==='/register-fixture'){res.writeHead(200,{'content-type':'text/html'});res.end(await server.transformIndexHtml(req.url,html));return}
  if(p==='/__test/state')return reply({calls,blockedWrites,financialsUnchanged:JSON.stringify(runs)===moneyWitness,realDatabaseWrites:0,realEmails:0})
  if(p==='/__fixture/control/fail-next'){failNext=true;return reply({ok:true})}
  if(p==='/__fixture/control/delay-next'){delayNext=true;return reply({ok:true})}
  if(!p.startsWith('/api/'))return next()
  if(req.method!=='GET'){blockedWrites++;return reply({error:'All real mutations are blocked by the synthetic fixture.'},405)}
  if(p==='/api/auth/me')return reply({id:900,email:'synthetic@example.test',displayName:'Synthetic reviewer',isActive:true,mustChangePassword:false,roles:['Admin'],permissions:['security.manage','settings.manage','payroll.run'],clientAccessMode:'All',clientId:null})
  if(p==='/api/clients')return reply(clients)
  if(p==='/api/pay-runs')return reply(runs.map(r=>({...r,employeeCount:r.employees.length,employees:[]})))
  const id=Number(/^\/api\/pay-runs\/(\d+)$/.exec(p)?.[1])
  if(id){
   const run=runs.find(r=>r.id===id);if(!run)return reply({error:'Synthetic run missing.'},404)
   if(failNext){failNext=false;calls.push({id,status:503});return reply({error:'Synthetic detail request failed. Retry this approved run.'},503)}
   const delay=id===9001&&delayNext?3500:0;delayNext=false
   const call={id,status:200,delay,closed:false};calls.push(call)
   res.on('close',()=>{if(!res.writableEnded)call.closed=true})
   if(delay)await new Promise(resolve=>setTimeout(resolve,delay))
   if(!res.destroyed)return reply(run)
   return
  }
  return reply({error:'API route outside the synthetic Register fixture is blocked.'},403)
 })}
}
const server=await createServer({root,plugins:[plugin],server:{host:'127.0.0.1',port:5196,strictPort:true,open:false}})
await server.listen()
console.log('Synthetic mounted Register: http://127.0.0.1:5196/register-fixture')
const close=async()=>{await server.close();fs.rmSync(fixture,{force:true});process.exit()}
process.on('SIGINT',close);process.on('SIGTERM',close)
