export type EvidenceState = 'Unknown' | 'Assumed' | 'User reviewed source'
export type ClLwpScenario = {
  clientId: number; effectiveFrom: string; cycleStart: string; cycleEnd: string; sourceReference: string
  basis: 'Unknown' | 'Worked' | 'Already payable'; eligibility: 'Unknown' | 'Full cycle' | 'Reviewed partial cycle'
  eligibleDays: number | null; attendanceDays: number | null; paidWeeklyOff: number | null; paidHolidays: number | null
  requestedCl: number | null; includedCl: number | null; availableCl: number | null; proposedLimit: number | null
  limitPeriod: 'Unknown' | 'Annual' | 'Monthly' | 'Cycle'; usedInLimitPeriod: number | null
  approval: 'Unknown' | 'Assume approved' | 'Reviewed approved days'; approvedCl: number | null
  excessAsLwp: boolean | null; divisor: number | null; evidence: EvidenceState
  sourcePayable: number | null; sourceLwp: number | null; sourceDivisor: number | null
}
export const emptyClLwpScenario = (clientId: number): ClLwpScenario => ({
  clientId,effectiveFrom:'',cycleStart:'',cycleEnd:'',sourceReference:'',basis:'Unknown',eligibility:'Unknown',eligibleDays:null,
  attendanceDays:null,paidWeeklyOff:null,paidHolidays:null,requestedCl:null,includedCl:null,availableCl:null,proposedLimit:null,
  limitPeriod:'Unknown',usedInLimitPeriod:null,approval:'Unknown',approvedCl:null,excessAsLwp:null,divisor:null,evidence:'Unknown',
  sourcePayable:null,sourceLwp:null,sourceDivisor:null,
})
const date = (value: string) => {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return null
  const n=Date.parse(`${value}T00:00:00Z`)
  return Number.isFinite(n) && new Date(n).toISOString().slice(0,10)===value ? n : null
}
const known = (value: number | null): value is number => typeof value==='number' && Number.isFinite(value)
export function simulateClLwp(s: ClLwpScenario) {
  const errors: string[]=[], reasons: string[]=[]
  let eligible: number | null=null, absent: number | null=null, paidCl: number | null=null, lwp: number | null=null, payable: number | null=null
  const from=date(s.cycleStart), to=date(s.cycleEnd), effective=date(s.effectiveFrom)
  const cycleDays=from!=null && to!=null ? (to-from)/86400000+1 : null
  if (!Number.isInteger(s.clientId) || s.clientId<=0) errors.push('Select the permitted client.')
  if (effective==null) errors.push('An explicit proposed effective date is required.')
  if (cycleDays==null || cycleDays<1 || cycleDays>31) errors.push('Enter a valid 1–31 day cycle; filename month is not enough.')
  if (effective!=null && from!=null && to!=null && effective>from) errors.push('Effective date starts after the cycle begins. Split/review the cycle; a single aggregate cannot prove the dated allocation.')
  if (!['Unknown','Worked','Already payable'].includes(s.basis) || !['Unknown','Full cycle','Reviewed partial cycle'].includes(s.eligibility)
    || !['Unknown','Annual','Monthly','Cycle'].includes(s.limitPeriod) || !['Unknown','Assume approved','Reviewed approved days'].includes(s.approval)
    || !['Unknown','Assumed','User reviewed source'].includes(s.evidence)) errors.push('Review scenario classifications.')
  for (const [key,value] of Object.entries(s)) if (typeof value==='number' && key!=='clientId' && (!Number.isFinite(value) || value<0 || value>(['proposedLimit','usedInLimitPeriod','availableCl'].includes(key)?366:31))) errors.push(`${key}: invalid non-negative day value.`)
  if (!known(s.divisor) || s.divisor<1 || !Number.isInteger(s.divisor)) errors.push('Choose an explicit whole salary divisor between 1 and 31; payable days and divisor are separate.')
  if (s.eligibility==='Full cycle' && cycleDays!=null) eligible=cycleDays
  else if (s.eligibility==='Reviewed partial cycle' && known(s.eligibleDays) && cycleDays!=null && s.eligibleDays<=cycleDays) eligible=s.eligibleDays
  else errors.push('Review join/exit eligibility; unknown eligibility cannot mean a full cycle.')
  if (!known(s.attendanceDays)) errors.push('Attendance is unknown; missing does not mean zero.')
  else if (eligible!=null && s.attendanceDays>eligible) errors.push('Entered attendance exceeds eligible calendar days.')
  if (s.evidence==='Unknown') reasons.push('Input evidence is unknown; this cannot authorize a real policy, balance or payroll change.')
  else reasons.push(`Evidence: ${s.evidence}. This is user-entered review status, not independent verification.`)
  if (s.basis==='Worked') {
    if (!known(s.paidWeeklyOff) || !known(s.paidHolidays)) errors.push('Paid weekly offs and holidays must be explicitly reviewed; unknown is not zero.')
    if (eligible!=null && known(s.attendanceDays) && known(s.paidWeeklyOff) && known(s.paidHolidays)) {
      absent=eligible-s.attendanceDays-s.paidWeeklyOff-s.paidHolidays
      if (absent<0) errors.push('Worked/off/holiday units overlap or exceed eligibility. Resolve double counting first.')
      if (!known(s.requestedCl) || !known(s.availableCl) || !known(s.proposedLimit) || !known(s.usedInLimitPeriod) || s.limitPeriod==='Unknown') errors.push('Enter proposed CL allocation, available balance, limit, limit period and already-used days. No annual allowance is inferred.')
      if (s.approval==='Unknown' || s.approval==='Reviewed approved days' && !known(s.approvedCl)) errors.push('Review the approval assumption and, where applicable, approved CL days.')
      if (known(s.requestedCl) && absent>=0 && s.requestedCl>absent) errors.push('Requested CL exceeds the comparable absence; do not create extra paid days.')
      if (known(s.requestedCl) && known(s.availableCl) && known(s.proposedLimit) && known(s.usedInLimitPeriod) && s.limitPeriod!=='Unknown'
        && s.approval!=='Unknown' && (s.approval!=='Reviewed approved days' || known(s.approvedCl))) {
        paidCl=Math.min(s.requestedCl,s.availableCl,Math.max(0,s.proposedLimit-s.usedInLimitPeriod),s.approval==='Reviewed approved days'?s.approvedCl!:s.requestedCl)
        if (paidCl<s.requestedCl) reasons.push('Proposed paid CL is limited by available balance, period allowance or approved days; the shortfall is visible.')
        if (s.excessAsLwp!==true && absent>paidCl) errors.push('Explicitly review remaining absence as LWP, or keep this scenario unresolved.')
        if (absent>=paidCl && (s.excessAsLwp===true || absent===paidCl)) { lwp=absent-paidCl;payable=eligible-lwp }
        reasons.push('CL allocation is a scenario only. No entitlement credit, approval or leave consumption is posted.')
      }
    }
  } else if (s.basis==='Already payable') {
    reasons.push('Entered days already include paid credits. Weekly offs, holidays and CL are not added again; actual worked days cannot be inferred.')
    if (known(s.requestedCl) && s.requestedCl>0) errors.push('Clear additional requested CL when attendance already represents payable days. Use Included CL only as source annotation.')
    if (known(s.includedCl) && known(s.attendanceDays) && s.includedCl>s.attendanceDays) errors.push('Included CL cannot exceed payable days.')
    if (eligible!=null && known(s.attendanceDays)) { payable=s.attendanceDays;lwp=eligible-payable;paidCl=s.includedCl }
  } else errors.push('Choose actual worked days or already-payable days; the source label “present” alone is insufficient.')
  if (payable!=null && known(s.divisor) && payable>s.divisor) errors.push('Scenario payable calendar units exceed the salary divisor. Review the calendar-to-payroll unit mapping; no automatic cap is assumed.')
  if (errors.length) { paidCl=null;lwp=null;payable=null }
  const payableVariance=payable!=null && known(s.sourcePayable)?payable-s.sourcePayable:null
  const lwpVariance=lwp!=null && known(s.sourceLwp)?lwp-s.sourceLwp:null
  if (payableVariance!=null && Math.abs(payableVariance)>.000001) reasons.push('Payable-day variance: compare CL credit, paid off/holiday eligibility, source basis and cycle before changing payroll.')
  if (lwpVariance!=null && Math.abs(lwpVariance)>.000001) reasons.push('LWP differs from source; the scenario does not overwrite the Excel lookup or cached source value.')
  if (known(s.sourceDivisor) && known(s.divisor) && s.sourceDivisor!==s.divisor) reasons.push('Salary divisor differs from source; matching payable days alone does not establish amount parity.')
  return {status:errors.length?'Unresolved':'Comparison only — policy approval pending',cycleDays,eligibleDays:eligible,absenceDays:absent,paidCl,lwp,payable,
    divisor:s.divisor,payableVariance,lwpVariance,errors,reasons,
    salaryImpact:null,salaryImpactReason:'No verified salary template/component snapshot is attached here. Day ratios cannot establish net-pay delta. Use the existing regular-payroll simulator with reviewed period inputs; no parallel salary formula is used.',
    policyBasis:'Interim / user proposed',reviewStatus:'Draft — approval pending',canActivate:false as const}
}

export type ClLwpDraft = { schema: 1; userId: number; clientId: number; revision: number; savedAt: string; scenario: ClLwpScenario; policyBasis: 'Interim / user proposed'; reviewStatus: 'Draft — approval pending' }
export interface DraftStorage { getItem(key:string): string|null; setItem(key:string,value:string): void }
const key = (userId:number,clientId:number) => `hrms:cl-lwp:proposal:v1:${userId}:${clientId}`
export function loadClLwpDraft(storage:DraftStorage,userId:number,clientId:number): ClLwpDraft|null {
  if (!Number.isInteger(userId)||userId<=0||!Number.isInteger(clientId)||clientId<=0) throw Error('A signed-in client scope is required.')
  const raw=storage.getItem(key(userId,clientId));if(!raw)return null
  const parsed=JSON.parse(raw) as ClLwpDraft
  if(parsed.schema!==1 || parsed.userId!==userId || parsed.clientId!==clientId || parsed.scenario?.clientId!==clientId || !Number.isInteger(parsed.revision) || parsed.revision<1
    || parsed.reviewStatus!=='Draft — approval pending' || parsed.policyBasis!=='Interim / user proposed') throw Error('Saved proposal scope/version is invalid; it was not applied or overwritten.')
  return parsed
}
export function saveClLwpDraft(storage:DraftStorage,userId:number,scenario:ClLwpScenario,expectedRevision:number):ClLwpDraft {
  const current=loadClLwpDraft(storage,userId,scenario.clientId)
  if ((current?.revision||0)!==expectedRevision) throw Error('This browser draft changed in another view. Reopen it before saving; no policy was changed.')
  if(current && JSON.stringify(current.scenario)===JSON.stringify(scenario))return current
  if(scenario.sourceReference.length>240 || JSON.stringify(scenario).length>12000)throw Error('Use bounded file/sheet/cell evidence only; do not put employee identifiers in this draft.')
  const draft:ClLwpDraft={schema:1,userId,clientId:scenario.clientId,revision:expectedRevision+1,savedAt:new Date().toISOString(),scenario,policyBasis:'Interim / user proposed',reviewStatus:'Draft — approval pending'}
  storage.setItem(key(userId,scenario.clientId),JSON.stringify(draft));return draft
}
