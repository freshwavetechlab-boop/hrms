export function quickPreparationFixture(count=2) {
 const headers=['Employee Code','Employee Name','Work Location','Total Days','Present Days','Payable Days','Monthly Basic','Basic','PF','Net Pay']
 const kinds=['employeeCode','employeeName','info','info','info','info','info','earning','deduction','netPay']
 const columns=headers.map((sourceHeader,columnIndex)=>({columnIndex,sourceHeader,label:sourceHeader,kind:kinds[columnIndex]}))
 const rows=Array.from({length:count},(_,i)=>({id:`row-${i+1}`,sourceRow:i+2,employeeCode:`TEST-${i+1}`,employeeName:`Synthetic employee ${i+1}`,email:'',information:[{label:'Work Location',value:'Test location'}],earnings:[{label:'Basic',amount:800}],deductions:[{label:'PF',amount:96}],employerContributions:[],netPay:704,declaredGross:800,declaredDeductions:96,warnings:[]}))
 const batch={id:'1234567890abcdef1234567890abcdef',clientId:42,clientName:'Synthetic client',month:'2026-10',sourceFileName:'Synthetic.xlsx',sheetName:'Salary',headerRow:1,createdBy:'test',createdAtUtc:'2026-10-01T00:00:00Z',rows}
 const source={sheetName:'Salary',headerRow:1,columnCount:10,columns,excludedSourceRows:[],rows:[{sourceRow:1,cells:headers.map(value=>({value}))},...rows.map(row=>({sourceRow:row.sourceRow,cells:[{value:row.employeeCode},{value:row.employeeName},{value:'Test location'},{value:'30'},{value:'24'},{value:'24'},{value:'1000'},{value:'800',formulaText:`G${row.sourceRow}/D${row.sourceRow}*F${row.sourceRow}`},{value:'96',formulaText:`H${row.sourceRow}*12%`},{value:'704',formulaText:`H${row.sourceRow}-I${row.sourceRow}`}]}))]}
 const component=(id,code,name,category,statutoryType)=>({id,code,name,category,componentRole:category==='Earning'?'Regular Earning':'Regular Deduction',statutoryType,calculationType:'Fixed Amount',value:'0',formula:'',baseComponent:'',proRata:true,taxable:category==='Earning',active:true,priority:100,payType:'Fixed Pay'})
 const context={source,components:[component('101','BASIC','Basic','Earning','None'),component('102','PF','PF','Deduction','PF Employee')],workWeeks:[{value:'Sunday off',label:'Sunday off'}],workLocations:[{id:1,name:'Test location'}],employees:rows.map((row,i)=>({id:i+1,employeeCode:row.employeeCode,employeeName:row.employeeName,workLocationId:1,workLocation:'Test location',designation:'Test designation'})),
  roster:{rows:rows.map((row,i)=>({rowId:row.id,sourceRow:row.sourceRow,employeeName:row.employeeName,employeeCode:row.employeeCode,status:'Existing',employeeId:i+1,message:'Matched'})),missingEmployees:[]},appliedSetups:[],templateApplicabilitySupported:true,sharedHeaderDivisorSupported:true,migrationRequired:false,
  defaults:{department:'Test department',designation:'Test designation',grade:'Test grade',workWeek:'Sunday off',attendanceCycleStartDay:1,attendanceCycleEndDay:31,payrollReportGenerationDay:3,attendanceSource:'Existing client setup',notes:[]},
  quickPreparation:{version:1,month:batch.month,sourceHash:'a'.repeat(64),attendanceSnapshotHash:'b'.repeat(64),cycleStart:'2026-10-01',cycleEnd:'2026-10-31',monthlyAttendance:[],dailyEmployeeIds:[]}}
 return {batch,context,columns}
}

export function reviewedQuickBasis(request,columns) {
 return {clientId:42,month:'2026-09',sourceColumns:structuredClone(columns),savedRequest:{...request,transferReviewVersion:1,templateScopeReviewVersion:1,presentDaysColumnIndex:4,reviewedFixedInputs:true,reviewedPolicyScope:true,importAttendance:true,
  templateScope:{reviewed:true,effectiveTo:'',openEnded:true,locationMode:'AllClientLocations',workLocationIds:[],newLocationNames:[],categoryKind:'AllClientCategories',categories:[],divisorMeaning:'ContractFixedDays',payableMeaning:'AlreadyPayable',policyBasis:'SavedSourceRules',policyVersion:''},
  attendanceReview:{mode:'SourceSeed',sourceHash:'0'.repeat(64),sourceMonth:'2026-09',cycleStart:'2026-09-01',cycleEnd:'2026-09-30',payableMeaning:'AlreadyPayable',presentMeaning:'ActualWorked',lwpBasis:'',reviewed:true},
  roundingReviews:{old:'old-proof'},templateDecisions:[{variantKey:'old',existingTemplateId:'template-1',createVariant:false}]}}
}

export function quickPreview(request) {
 return {planHash:'c'.repeat(64),canApply:request.templateDecisions?.length>0,migrationRequired:false,coverage:'Synthetic core preview',issues:[],
  rows:request.rowIds.map(rowId=>({rowId,employeeId:Number(rowId.split('-')[1]),employeeCode:`TEST-${rowId.split('-')[1]}`,employeeName:`Synthetic employee ${rowId.split('-')[1]}`,action:request.employeeDecisions.find(d=>d.rowId===rowId)?.allowCreate?'Create':'Update',payrollDays:30,presentDays:24,payableDays:24,annualCtc:12000,gross:800,deductions:96,benefits:0,net:704,lines:[{code:'BASIC',sourceAmount:800,calculatedAmount:800,variance:0}]})),
  transfer:{version:1,sourceMonth:'2026-10',sourceBatchId:request.batchId,sourceHash:'a'.repeat(64),effectiveFrom:request.effectiveFrom,preparationStatus:'Ready',parityStatus:'Not verified',tolerance:0,components:[],formulas:[],attendanceProofs:[],amountComparisons:[],templates:[{variantKey:'rules',templateId:'template-1',name:'Synthetic salary',action:'Reuse',rowIds:request.rowIds,existingResults:[],keptAssignments:[],candidates:[{id:'template-1',name:'Synthetic salary',exact:true,configurationExact:true,differences:[],details:[]}]}]}}
}
