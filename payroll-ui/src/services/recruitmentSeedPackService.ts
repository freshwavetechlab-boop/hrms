import { postForm } from './apiClient'
import { downloadXlsx, type XlsxSheet } from '../utils/xlsx'

export type RecruitmentSeedPackImportItem = {
  sheet: string
  rowNumber: number
  sourceKey: string
  entity: string
  outcome: 'Created' | 'Updated' | 'Reused' | 'Deferred' | 'Failed'
  message: string
  requisitionId?: number | null
  jobDescriptionId?: number | null
  candidateId?: number | null
  applicationId?: number | null
  atsScore?: number | null
}

export type RecruitmentSeedPackImportResult = {
  totalRows: number
  created: number
  updated: number
  reused: number
  deferred: number
  failed: number
  success: boolean
  items: RecruitmentSeedPackImportItem[]
}

const emptyResult: RecruitmentSeedPackImportResult = { totalRows: 0, created: 0, updated: 0, reused: 0, deferred: 0, failed: 0, success: false, items: [] }

export function importRecruitmentSeedPack(workbook: File, resumes: File[] = []) {
  const body = new FormData()
  body.append('workbook', workbook)
  resumes.forEach(file => body.append('resumes', file))
  return postForm('/api/recruitment/seed-pack/import', body, emptyResult, { timeoutMs: 180000, successMessage: 'Recruitment seed pack processed.' })
}

export function downloadHiringSeedTemplate() {
  downloadXlsx('Frevo_Hiring_Request_JD_ATS_Seed_Template.xlsx', hiringSheets())
}

export function downloadCandidateSeedTemplate() {
  downloadXlsx('Frevo_Candidate_ATS_Seed_Template.xlsx', candidateSheets())
}

function hiringSheets(): XlsxSheet[] {
  return [
    {
      name: 'Instructions', rows: [
        ['Frevo Hiring Request + JD + ATS Seed Pack', 'Version 1'],
        ['How to use', 'Replace EXAMPLE source keys, complete the rows, then upload this workbook from Hiring Requests.'],
        ['Safe rerun', 'Source Key is the idempotency key. Uploading the same completed pack updates editable drafts and never clones approved records.'],
        ['Request Action', 'Draft keeps the request editable. Submit starts the configured approval workflow.'],
        ['JD Action', 'Draft keeps the JD editable. Submit uses the default JD workflow after request approval. Approve is admin-only and explicit.'],
        ['ATS', 'Every ATS Skills row becomes a weighted JD skill. At least one skill must be marked Yes under Must Have.'],
        ['Multiple roles', 'Use one Source Key per role and repeat that key in all JD/ATS sheets.'],
      ],
    },
    {
      name: 'Hiring Requests', rows: [
        ['Source Key', 'Client Code', 'Requester Employee Code', 'Request Date', 'Position Title', 'Department', 'Openings', 'Hiring Type', 'Employment Type', 'Position Category', 'Priority', 'Work Location', 'Work Mode', 'Target Joining Date', 'Project', 'Business Unit', 'Cost Center', 'Budget Available', 'Budget Amount', 'Currency', 'CTC Flexibility Percent', 'Experience', 'Qualification', 'Required Skills', 'Preferred Skills', 'Certifications', 'Languages', 'Benefits', 'Business Justification', 'Hiring Notes', 'External Position Code', 'Source Type', 'Source Reference', 'Source Document', 'Source Document Date', 'Source Authority', 'Client Approval State', 'Source Notes', 'Request Action'],
        ['EXAMPLE-ROLE-001', 'UIDAI', 'REQUESTER-EMPLOYEE-CODE', '2026-09-09', 'Senior Application Developer', 'Information Technology', '1', 'New', 'Contract', 'Technical', 'High', 'Technology Centre Bengaluru', 'Office', '2026-11-01', 'Digital Platform', '', '', 'Yes', '1500000', 'INR', '30', '5-8 years', 'B.E./B.Tech', 'React, Node.js, SQL', 'Docker, Kubernetes', 'Cloud certification', 'English, Hindi', 'Medical insurance', 'Approved delivery requirement', 'Replace this example row with source-backed details.', 'CLIENT-POSITION-001', 'Formal Approval Letter', 'CLIENT-FILE-001', 'approval-letter.pdf', '2026-09-01', 'Client HR', 'Approved for Hiring', '', 'Draft'],
      ],
    },
    {
      name: 'JD Overview', rows: [
        ['Source Key', 'Title', 'Summary', 'Role Purpose', 'Qualifications', 'Certifications', 'Languages', 'Benefits', 'JD Action'],
        ['EXAMPLE-ROLE-001', 'Senior Application Developer', 'Build and maintain secure enterprise applications.', 'Own delivery from design through production support.', 'B.E./B.Tech|MCA', 'Cloud certification', 'English|Hindi', 'Medical insurance|Learning budget', 'Draft'],
      ],
    },
    {
      name: 'JD Responsibilities', rows: [
        ['Source Key', 'Responsibility', 'Order'],
        ['EXAMPLE-ROLE-001', 'Design, build and review production-ready application features.', '10'],
        ['EXAMPLE-ROLE-001', 'Collaborate with product, security and operations teams.', '20'],
      ],
    },
    {
      name: 'ATS Skills', rows: [
        ['Source Key', 'Skill', 'Must Have', 'Minimum Years', 'Proficiency', 'Weight Percent', 'Order'],
        ['EXAMPLE-ROLE-001', 'React', 'Yes', '3', 'Advanced', '40', '10'],
        ['EXAMPLE-ROLE-001', 'Node.js', 'Yes', '3', 'Advanced', '35', '20'],
        ['EXAMPLE-ROLE-001', 'Docker', 'No', '1', 'Intermediate', '25', '30'],
      ],
    },
  ]
}

function candidateSheets(): XlsxSheet[] {
  return [
    {
      name: 'Instructions', rows: [
        ['Frevo Candidate + ATS Seed Pack', 'Version 1'],
        ['Prerequisite', 'The Position Code must belong to an active approved position with an approved JD.'],
        ['Upload', 'Select this completed workbook and every resume named in Resume File in one upload.'],
        ['Identity safety', 'Expected Email and/or Expected Phone is checked against the resume before any candidate is created.'],
        ['ATS automation', 'A successful row creates or reuses the candidate/application, parses the resume, runs ATS and initializes the configured pipeline.'],
        ['Safe rerun', 'Candidate identity, document hash and candidate-position link prevent duplicate profiles, resumes and applications.'],
      ],
    },
    {
      name: 'Candidates', rows: [
        ['Candidate Key', 'Client Code', 'Position Code', 'Resume File', 'Expected Email', 'Expected Phone', 'Source Type'],
        ['EXAMPLE-CANDIDATE-001', 'UIDAI', 'CLIENT-POSITION-001', 'candidate-resume.pdf', 'candidate@example.com', '9876543210', 'Structured seed pack'],
      ],
    },
  ]
}
