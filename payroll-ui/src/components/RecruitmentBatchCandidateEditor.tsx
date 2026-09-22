import { useEffect, useState } from 'react'
import { Alert, Drawer, Spin } from 'antd'
import CandidateEditorDrawer from './RecruitmentCandidateEditorDrawer'
import CandidateProfileFields, { type CandidateProfileDraft } from './RecruitmentCandidateProfileFields'
import EntityAttachmentPanel from './EntityAttachmentPanel'
import { getCandidate, saveCandidate, saveCandidateProfileSections, uploadCandidateResume } from '../services/recruitmentTalentService'
import { uploadEntityAttachment } from '../services/attachmentService'
import type { Client, SaveRecruitmentCandidate } from '../types/payroll'
import type { RecruitmentProfileSubmissionBatchItem } from '../types/recruitmentCases'

export default function RecruitmentBatchCandidateEditor({ item, clients, onClose, onSaved }: {
  item: RecruitmentProfileSubmissionBatchItem; clients: Client[]; onClose: () => void; onSaved: () => Promise<void>
}) {
  const [draft, setDraft] = useState<SaveRecruitmentCandidate | null>(null)
  const [profile, setProfile] = useState<CandidateProfileDraft | null>(null)
  const [originalProfile, setOriginalProfile] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  useEffect(() => {
    let active = true
    void getCandidate(item.candidateId).then(detail => {
      if (!active) return
      if (!detail) { setError('Candidate could not be loaded. Close and retry.'); return }
      setDraft({ ...detail.candidate })
      const sections = { experience: detail.experience, education: detail.education, certifications: detail.certifications }
      setProfile(sections); setOriginalProfile(JSON.stringify(sections))
    }).catch(() => { if (active) setError('Candidate could not be loaded. Close and retry.') })
    return () => { active = false }
  }, [item.candidateId])
  const save = async () => {
    if (!draft || !profile || saving) return
    setSaving(true); setError('')
    try {
      const response = await saveCandidate(draft)
      if (!response.ok) { setError(response.error || 'Candidate details could not be saved.'); return }
      if (JSON.stringify(profile) !== originalProfile) {
        const sections = await saveCandidateProfileSections(draft.id, profile)
        if (!sections.ok) { setError(sections.error || 'Summary saved; profile sections could not be saved. Retry to finish.'); return }
      }
      await onSaved(); onClose()
    } catch { setError('Could not finish saving. Check the connection and retry.') }
    finally { setSaving(false) }
  }
  if (!draft) return <Drawer open title={`Update fields · ${item.candidateName}`} onClose={onClose}>
    {error ? <Alert type="error" message={error} /> : <Spin />}
  </Drawer>
  return <CandidateEditorDrawer draft={draft} clients={clients} onChange={setDraft} onClose={onClose}
    onSubmit={() => void save()} saving={saving} guidance={item.missingFields ? `Complete: ${item.missingFields}` : undefined}>
    {error && <Alert showIcon type="error" message={error} />}
    {profile && <CandidateProfileFields profileDraft={profile} setProfileDraft={setProfile} candidateId={draft.id} />}
    <EntityAttachmentPanel entityType="CANDIDATE" entityId={draft.id} clientId={draft.clientId} moduleCode="RECRUITMENT"
      formCodes={['CANDIDATE_APPLICATION', 'EMPLOYEE_REFERRAL', 'PRE_ONBOARDING']} title="Candidate documents"
      uploadOverride={(configuration, attachment, onProgress) => !attachment.file
        ? Promise.resolve({ ok: false, error: 'Select a file.' })
        : configuration.attributeCode === 'RESUME'
          ? uploadCandidateResume(draft.id, configuration.id, attachment.file, attachment, onProgress)
          : uploadEntityAttachment(configuration.id, 'CANDIDATE', draft.id, attachment.file, attachment, onProgress)}
      onChanged={() => void onSaved()} />
  </CandidateEditorDrawer>
}
