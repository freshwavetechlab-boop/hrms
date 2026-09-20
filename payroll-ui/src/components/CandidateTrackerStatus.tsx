import { Alert, Tag } from 'antd'
import type { PublicCandidateApplicationTracker } from '../types/recruitmentOrchestration'
import { formatApiDateTime } from '../utils/apiDateTime'
import './CandidateTrackerStatus.css'

export default function CandidateTrackerStatus({ tracker }: { tracker: PublicCandidateApplicationTracker }) {
  return <section className="public-tracker">
      <header><div><span>Candidate</span><h2>{tracker.candidateName || 'Your applications'}</h2></div><Tag color="blue">{tracker.applications.length} application{tracker.applications.length === 1 ? '' : 's'}</Tag></header>
      <div className="public-tracker-list">{tracker.applications.map(application => <article key={application.applicationId} className="public-tracker-card">
        <div className="public-tracker-title"><div><span>{application.applicationCode}</span><h3>{application.positionTitle}</h3><small>Applied {formatApiDateTime(application.appliedAt)}</small></div><Tag color={application.currentStatus === 'Rejected' ? 'red' : application.currentStatus === 'Joined' ? 'green' : 'blue'}>{application.currentStage || application.currentStatus}</Tag></div>
        <Alert showIcon type={application.processingStatus === 'NeedsReview' ? 'warning' : application.processingStatus === 'Completed' ? 'success' : 'info'} message={application.processingMessage} />
        <div className="public-tracker-grid">
          <div><h4>Application timeline</h4><ol>{application.timeline.map((stage, index) => <li key={`${stage.stage}-${stage.changedAt}-${index}`}><i /><div><b>{stage.stage}</b><small>{formatApiDateTime(stage.changedAt)}</small></div></li>)}</ol></div>
          <div><h4>Interviews</h4>{application.interviews.length ? application.interviews.map((interview, index) => <div className="public-tracker-event" key={`${interview.round}-${index}`}><b>{interview.round}</b><span>{formatApiDateTime(interview.scheduledStart)} · {interview.mode}</span><Tag color={interview.status === 'Completed' ? 'green' : interview.status === 'Cancelled' || interview.status === 'No Show' ? 'red' : 'blue'}>{interview.status}{interview.result && interview.result !== 'Pending' ? ` · ${interview.result}` : ''}</Tag>{interview.locationOrLink && <a href={interview.locationOrLink.startsWith('http') ? interview.locationOrLink : undefined} target="_blank" rel="noreferrer">{interview.locationOrLink}</a>}</div>) : <p>No interview scheduled yet.</p>}
          {application.offer && <><h4>Offer & joining</h4><div className="public-tracker-event"><b>{application.offer.offerNumber}</b><Tag color={application.offer.status === 'Accepted' ? 'green' : 'blue'}>{application.offer.status}</Tag><span>Proposed joining: {new Date(application.offer.proposedJoiningDate).toLocaleDateString('en-IN')}</span></div></>}</div>
        </div>
      </article>)}</div>
    </section>
}

