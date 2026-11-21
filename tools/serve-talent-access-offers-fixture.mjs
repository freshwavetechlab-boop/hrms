import fs from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'
const here=path.dirname(fileURLToPath(import.meta.url)),root=path.resolve(here,'../payroll-ui')
const require=createRequire(path.join(root,'package.json')),{createServer}=await import(pathToFileURL(require.resolve('vite')).href)
const entry=path.join(root,'.codex-talent-access-offers.tsx')
const joining=Array.from({length:24},(_,i)=>({sourceType:'Roster',sourceId:i+1,rosterRowId:i+1,rowKey:`roster:${i+1}`,releaseId:i+101,offerId:0,offerNumber:`SYN-OFFER-${i+1}`,candidateId:i+201,candidateCode:`SYN-CAN-${i+1}`,candidateName:i===0?'Synthetic employee with a deliberately long full name':'Synthetic employee '+(i+1),clientId:55,clientName:'Synthetic client',positionTitle:i%2?'Research scientist':'Technical support with an extended project title',jobLocation:i%2?'Synthetic research institute - Site A':'Synthetic field posting - Site B',proposedJoiningDate:'2026-11-02',offeredCtc:0,currency:'INR',offerStatus:i<12?'Released':'Accepted',acceptanceRecorded:i>=12,employeeId:i+301,employeeCode:`SYN-EMP-${i+1}`,importStatus:'Imported',issue:''}))
const fields=['candidateName','candidateEmail','positionTitle','joiningDate','monthlyBasic','hraRate','monthlyGross','contractEndDate'].map(key=>({key,label:key,type:key.includes('Date')?'date':'text',required:false,defaultValue:'',sourceHeaders:[]}))
const requisition={id:99,clientId:55,clientName:'Synthetic client',rfrNumber:'RFR-SYN-2026-00045',requestDate:'2026-11-17',requestedByEmployeeId:5,requestedByUserId:3,requesterUserId:null,requestedByName:'Legacy requester',positionTitle:'Synthetic research scientist',department:'Research',positionCategory:'Research',employmentType:'Contract',hiringType:'New position',numberOfOpenings:3,branchId:17,jobLocation:'Synthetic field posting',workMode:'Office',project:'Synthetic study',hiringPriority:'Normal',experienceRange:'2–5 years',qualification:'Graduate',requiredSkills:'Research',businessJustification:'Approved synthetic requirement',reasonForHiring:'Research vacancy',currency:'INR',salaryMin:0,salaryMax:0,budgetAvailable:false,budgetAmount:0,status:'Approved',hasLinkedOpenPosition:true,hasLinkedJobDescription:true,createdAt:'2026-11-17',updatedAt:'2026-11-21'}
const limits=Array.from((await fs.readFile(path.resolve(here,'../Payroll.API/Models/RecruitmentRequisitionTextLimits.cs'),'utf8')).matchAll(/new\("([^"]+)", "([^"]+)", (\d+)(?:, "([^"]+)")?\)/g),m=>({field:m[1],label:m[2],maximum:Number(m[3]),unit:m[4]||'characters'}))
const template={id:1,clientId:55,templateName:'Synthetic offer',fields,importPresets:[]}
const roster=joining.map(row=>({id:row.sourceId,clientId:55,candidateId:row.candidateId,candidateCode:row.candidateCode,candidateName:row.candidateName,email:`synthetic${row.sourceId}@example.invalid`,phone:'',sourceGroup:'Synthetic batch',workLocationId:17,workLocation:row.jobLocation,offerNumber:row.offerNumber,releasedAt:'2026-11-20',templateId:1,templateName:template.templateName,sourceFileName:'synthetic.xlsx',sourceSheetName:'Employees',sourceRow:row.sourceId+1,values:{candidateName:row.candidateName,candidateEmail:`synthetic${row.sourceId}@example.invalid`,positionTitle:row.positionTitle,joiningDate:'2026-11-02',monthlyBasic:'56000',hraRate:'30%',monthlyGross:'72800',contractEndDate:'2026-12-31'},documentIssues:[],revision:'a'.repeat(64),sourceRevision:'b'.repeat(64),employeeId:row.employeeId,employeeCode:row.employeeCode,syncedEmployeeId:row.employeeId,employeeManagedFields:['candidateName','candidateEmail','positionTitle','joiningDate','monthlyBasic','hraRate','monthlyGross']}))
const plugin={name:'isolated-ta-access-offers',enforce:'pre',resolveId(id){if(id==='/.codex-talent-access-offers.tsx')return entry},async load(id){if(id.split('?')[0].replaceAll('\\','/')===entry.replaceAll('\\','/')){this.addWatchFile(path.join(here,'fixtures/TalentAccessOffers.fixture.tsx'));return fs.readFile(path.join(here,'fixtures/TalentAccessOffers.fixture.tsx'),'utf8')};if(id.split('?')[0].replaceAll('\\','/').endsWith('/src/services/apiBaseUrl.ts'))return 'export function resolveRuntimeApiBase(){return window.location.origin}'},configureServer(server){server.middlewares.use(async(req,res,next)=>{
  const url=new URL(req.url,'http://127.0.0.1:5198'),route=url.pathname
  const reply=(body,status=200)=>{res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(body))}
  if(route==='/ta-verification'){res.writeHead(200,{'content-type':'text/html'});res.end(await server.transformIndexHtml(req.url,'<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="root"></div><script type="module" src="/.codex-talent-access-offers.tsx"></script></body></html>'));return}
  if(!route.startsWith('/api/'))return next()
  if(req.method==='POST'&&route==='/api/recruitment-orchestration/job-descriptions'){let body='';for await(const chunk of req)body+=chunk;return reply({...JSON.parse(body),id:91,status:'Draft',versionNumber:1})}
  if(req.method!=='GET')return reply({error:'Synthetic validation: openings cannot be lower than filled positions. No live data was changed.'},400)
  if(route==='/api/auth/me'){const readonly=new URL(req.headers.referer||'http://fixture').searchParams.has('readonly');return reply({id:81,clientId:null,email:'verifier@example.invalid',displayName:'Synthetic copied administrator',roles:['employee','super_admin_copy'],permissions:readonly?['recruitment.rfr.view','recruitment.offer.view']:['settings.manage'],isActive:true,mustChangePassword:false,clientAccessMode:'Legacy',assignedClientIds:[],recruitmentScopeMode:'Client'})}
  if(route==='/api/clients')return reply([{id:55,name:'Synthetic client',code:'SYN',isActive:true}])
  if(route==='/api/work-locations'||route.endsWith('/work-locations'))return reply([{id:17,clientId:55,name:'Synthetic field posting',isActive:true}])
  if(route==='/api/recruitment/joining-candidates')return reply(joining)
  if(route==='/api/recruitment/requisitions')return reply([requisition])
  if(route==='/api/recruitment/requisitions/field-limits')return reply(limits)
  if(route==='/api/recruitment/requesters')return reply([{id:81,displayName:'Synthetic approver',email:'verifier@example.invalid',isActive:true,clientId:null}])
  if(route.startsWith('/api/recruitment/masters/'))return reply(['Research','Contract','New position','Normal','2–5 years'])
  if(route.endsWith('/document-roster/rows'))return reply(roster)
  if(route.endsWith('/document-roster/templates'))return reply([template])
  if(route.endsWith('/document-roster/mail-jobs'))return reply({items:[]})
  if(route.endsWith('/lookups'))return reply({lookupSources:[],attachmentConfigurations:[],attachmentFieldConfigurations:[],workflows:[],forms:[],positions:[],atsScoringProfiles:[]})
  if(route.endsWith('/document-roster/signing-asset'))return reply({available:false,fileName:'',contentType:'',width:0,height:0,sha256:'',updatedAt:null})
  if(route.endsWith('/final-offer-settings'))return reply({clientId:55})
  if(route==='/api/attachment-configurations/effective')return reply([])
  return reply([])
})}}
const server=await createServer({root,configFile:false,plugins:[plugin],server:{host:'127.0.0.1',port:5198,strictPort:true},cacheDir:'C:/Users/APPC0024/AppData/Local/Temp/frevo-ta-vite-cache'})
await server.listen();console.log('Synthetic TA verification: http://127.0.0.1:5198/ta-verification')
