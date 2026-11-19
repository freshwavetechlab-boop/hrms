import fs from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'
const toolsRoot = path.dirname(fileURLToPath(import.meta.url)), root = path.resolve(toolsRoot, '../payroll-ui')
const require = createRequire(path.join(root, 'package.json')), { createServer } = await import(pathToFileURL(require.resolve('vite')).href)
const source = path.join(root, '.codex-recruitment-joining-fixture.tsx')
const baseline = [
  { id: 490, clientId: 42, employeeCode: 'EXISTING-490', name: 'Existing employee baseline', source: 'Existing record' },
  { id: 500, clientId: 42, employeeCode: 'EXISTING-500', name: 'Previously imported roster employee', source: 'Previously imported roster' },
]
function seed() {
  const normal = Array.from({length:9},(_,i) => ({sourceType:'Offer',sourceId:i+1,rowKey:`offer:${i+1}`,offerId:i+1,applicationId:100+i,candidateId:200+i,candidateCode:`TA-${String(i+1).padStart(3,'0')}`,candidateName:i===0?'Synthetic candidate with a deliberately longer full name':'Synthetic candidate '+(i+1),clientId:42,clientName:'Synthetic multi-location client',positionTitle:i%2?'Technical Assistant':'Junior Technical Assistant with extended project role',jobLocation:i%2?'Site B':'Site A',proposedJoiningDate:'2026-11-01T00:00:00',offeredCtc:240000+i*12000,currency:'INR',offerStatus:i===8?'Accepted':'Released',acceptanceRecorded:i===8,employeeId:null,employeeCode:'',importStatus:i===8?'Ready':'Pending acceptance',issue:''}))
  const roster = Array.from({length:6},(_,i) => ({sourceType:'Roster',sourceId:101+i,rowKey:`roster:${101+i}`,rosterRowId:101+i,releaseId:201+i,offerId:0,applicationId:0,candidateId:0,candidateCode:`ROSTER-${101+i}`,candidateName:'Synthetic roster candidate '+(101+i),clientId:42,clientName:'Synthetic multi-location client',positionTitle:'Field officer',jobLocation:i%2?'Site B':'Site A',proposedJoiningDate:'2026-11-01T00:00:00',offeredCtc:300000,currency:'INR',offerStatus:i===4?'Accepted':'Released',acceptanceRecorded:i===4,employeeId:i===4?500:null,employeeCode:i===4?'EXISTING-500':'',importStatus:i===4?'Imported':'Pending acceptance',issue:i===4?'Existing employee preserved':''}))
  const foreign = {...normal[0],sourceId:81,rowKey:'offer:81',offerId:81,clientId:99,clientName:'Other synthetic client',candidateName:'Other client candidate'}
  return {rows:[...normal,...roster,foreign],employees:structuredClone(baseline),acceptCalls:[],importCalls:[],cancelCalls:[],blockedWrites:0,databaseWrites:0,emailsSent:0}
}
let state = seed()
const find = (type,id) => state.rows.find(row => row.sourceType===type && row.sourceId===id)
const item = (row,status,message) => ({sourceType:row?.sourceType||'Offer',sourceId:row?.sourceId||0,rowKey:row?.rowKey||'',rosterRowId:row?.rosterRowId??null,offerId:row?.offerId||0,applicationId:row?.applicationId||0,candidateName:row?.candidateName||'Candidate',status,employeeId:row?.employeeId??null,employeeCode:row?.employeeCode||'',message})
const selected = request => [...new Set(request.offerIds||[])].map(id=>find('Offer',id)).concat([...new Set(request.rosterRowIds||[])].map(id=>find('Roster',id)))
const result = items => ({items,accepted:items.filter(i=>i.status==='Accepted').length,alreadyAccepted:items.filter(i=>i.status==='Already accepted').length,imported:items.filter(i=>i.status==='Imported').length,alreadyImported:items.filter(i=>i.status==='Already imported').length,review:items.filter(i=>i.status==='Review').length})
const plugin = {
  name:'isolated-recruitment-joining',enforce:'pre',
  resolveId(id) { if (id==='/.codex-recruitment-joining-fixture.tsx'||id.replaceAll('\\','/')===source.replaceAll('\\','/')) return source },
  async load(id) { if(id.split('?')[0].replaceAll('\\','/')===source.replaceAll('\\','/')) return fs.readFile(path.join(toolsRoot,'fixtures/RecruitmentJoining.fixture.tsx'),'utf8'); if(id.split('?')[0].replaceAll('\\','/').endsWith('/src/services/apiBaseUrl.ts')) return 'export function resolveRuntimeApiBase(){return window.location.origin}' },
  configureServer(server) { server.middlewares.use(async(req,res,next)=>{
    const url=new URL(req.url,'http://127.0.0.1:5197'), route=url.pathname
    const reply=(body,status=200)=>{res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(body))}
    const read=async()=>{let value='';for await(const chunk of req){value+=chunk;if(value.length>1024*1024)throw Error('Fixture request too large')}return JSON.parse(value)}
    try {
      if(route==='/joining-fixture'){res.writeHead(200,{'content-type':'text/html'});res.end(await server.transformIndexHtml(req.url,'<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module" src="/.codex-recruitment-joining-fixture.tsx"></script></body></html>'));return}
      if(route==='/__fixture/state')return reply({...state,baselineEmployeesUnchanged:baseline.every(before=>JSON.stringify(state.employees.find(row=>row.id===before.id))===JSON.stringify(before)),createdEmployees:state.employees.length-baseline.length})
      if(route==='/__fixture/employees')return reply(state.employees)
      if(route==='/__fixture/reset'&&req.method==='POST'){state=seed();return reply({reset:true})}
      if(!route.startsWith('/api/'))return next()
      if(req.method==='GET'&&route==='/api/auth/me'){const readonly=new URL(req.headers.referer||'http://fixture').searchParams.has('readonly');return reply({id:996,email:'verifier@example.test',displayName:'Local verifier',isActive:true,mustChangePassword:false,clientId:null,roles:['Admin'],permissions:readonly?['employees.view','recruitment.offer.view']:['settings.manage','employees.manage','recruitment.manage','recruitment.offer.issue'],clientAccessMode:'All',assignedClientIds:[]})}
      if(req.method==='GET'&&route==='/api/recruitment/joining-candidates')return reply(state.rows.filter(row=>row.clientId===Number(url.searchParams.get('clientId'))&&row.offerStatus!=='Withdrawn'))
      if(req.method==='POST'&&route==='/api/recruitment/offers/accept-selected'){
        const request=await read();state.acceptCalls.push({...request,employeeCountBefore:state.employees.length})
        const items=selected(request).map(row=>{if(!row||row.clientId!==request.clientId)return item(row,'Review','Client or source unavailable');if(row.acceptanceRecorded)return item(row,'Already accepted','Acceptance already recorded');row.acceptanceRecorded=true;row.offerStatus='Accepted';row.importStatus=row.employeeId?'Imported':'Ready';return item(row,'Accepted','Acceptance recorded; Employee Master is unchanged')})
        return reply(result(items))
      }
      if(req.method==='POST'&&route==='/api/recruitment/joining-candidates/import'){
        const request=await read();state.importCalls.push(request)
        const items=selected(request).map(row=>{if(!row||row.clientId!==request.clientId)return item(row,'Review','Client or source unavailable');if(!row.acceptanceRecorded)return item(row,'Review','Accept the released offer first');if(row.employeeId)return item(row,'Already imported','Existing employee preserved');if(row.sourceType==='Offer'&&row.sourceId===6){row.importStatus='Review';row.issue='Configured joining document is missing';return item(row,'Review',row.issue)}row.employeeId=1000+state.employees.length;row.employeeCode=row.candidateCode;row.importStatus='Imported';state.employees.push({id:row.employeeId,clientId:row.clientId,employeeCode:row.employeeCode,name:row.candidateName,source:row.sourceType});return item(row,'Imported','Employee created')})
        return reply(result(items))
      }
      if(req.method!=='GET')state.blockedWrites++
      return reply({error:'This synthetic fixture has no upstream database or delivery API.'},req.method==='GET'?404:405)
    }catch(error){return reply({error:error.message},400)}
  }) },
}
const server=await createServer({root,plugins:[plugin],server:{host:'127.0.0.1',port:5197,strictPort:true,open:false}})
await server.listen();console.log('Recruitment joining fixture: http://127.0.0.1:5197/joining-fixture\nState: http://127.0.0.1:5197/__fixture/state')
const close=async()=>{await server.close();process.exit()};process.on('SIGINT',close);process.on('SIGTERM',close)
