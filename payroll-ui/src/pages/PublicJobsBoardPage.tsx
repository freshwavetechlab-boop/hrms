import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { ArrowRightOutlined, BankOutlined, ClockCircleOutlined, CloseOutlined, DollarOutlined, EnvironmentOutlined, FilterOutlined, SearchOutlined, TeamOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Drawer, Empty, Input, Pagination, Select, Skeleton } from 'antd'
import { apiUrl } from '../services/apiClient'
import { apiUtcDate } from '../utils/apiDateTime'
import { emptyPublicJobFilters, filterPublicJobs, formatPublicJobSalary, publicJobApplyUrl, publicJobFiltersFromSearch, publicJobTitle, sortPublicJobs, type PublicCareerJob, type PublicJobFilters } from '../utils/publicJobsBoard'
import './PublicJobsBoardPage.css'

const pageSize = 10
const date = (value?: string | null) => {
  if (!value) return ''
  const parsed = apiUtcDate(value)
  return Number.isNaN(parsed.getTime()) ? '' : parsed.toLocaleDateString('en-IN', { day: 'numeric', month: 'short', year: 'numeric' })
}
const initials = (name: string) => name.split(/\s+/).filter(Boolean).slice(0, 2).map(word => word[0]).join('').toUpperCase()

export default function PublicJobsBoardPage() {
  const [params, setParams] = useSearchParams()
  const filters = useMemo(() => publicJobFiltersFromSearch(params), [params])
  const [jobs, setJobs] = useState<PublicCareerJob[]>([]), [loading, setLoading] = useState(true), [error, setError] = useState('')
  const [brand, setBrand] = useState<{ name: string; logoDataUrl: string } | null>(null)
  const [refresh, setRefresh] = useState(0), [mobileFilters, setMobileFilters] = useState(false)
  const [search, setSearch] = useState(filters.q), [location, setLocation] = useState(filters.location)
  const sort = params.get('sort') === 'title' ? 'title' : 'recent'
  useEffect(() => {
    const previousTitle = document.title
    document.title = `Careers | ${brand?.name || 'GA Digital'}`
    return () => { document.title = previousTitle }
  }, [brand?.name])
  useEffect(() => { setSearch(filters.q); setLocation(filters.location) }, [filters.q, filters.location])
  useEffect(() => {
    const controller = new AbortController()
    void fetch(apiUrl('/api/public/organization-brand'), { credentials: 'omit', signal: controller.signal }).then(response => response.ok ? response.json() : null).then(value => {
      if (!controller.signal.aborted && value && typeof value.name === 'string' && typeof value.logoDataUrl === 'string') setBrand(value)
    }).catch(() => { /* The organization name remains readable if branding is unavailable. */ })
    return () => controller.abort()
  }, [])
  useEffect(() => {
    let active = true
    const controller = new AbortController()
    setLoading(true); setError('')
    void fetch(apiUrl('/api/public/recruitment/jobs'), { credentials: 'omit', signal: controller.signal, headers: { Accept: 'application/json' } }).then(async response => {
      if (!response.ok) throw new Error('Jobs are unavailable')
      return response.json() as Promise<PublicCareerJob[]>
    }).then(result => {
      if (!active) return
      if (Array.isArray(result)) setJobs(result.filter(job => !!publicJobApplyUrl(job)))
      else { setJobs([]); setError('We could not load the open jobs. Please try again.') }
      setLoading(false)
    }).catch(() => { if (active) { setJobs([]); setError('We could not load the open jobs. Please try again.'); setLoading(false) } })
    return () => { active = false; controller.abort() }
  }, [refresh])
  useEffect(() => {
    const tick = () => { if (!document.hidden) setRefresh(value => value + 1) }
    const timer = window.setInterval(tick, 120000)
    window.addEventListener('focus', tick)
    return () => { window.clearInterval(timer); window.removeEventListener('focus', tick) }
  }, [])

  const update = (patch: Partial<PublicJobFilters>, reset = false) => {
    const next = new URLSearchParams(params)
    const values = reset ? emptyPublicJobFilters : { ...filters, ...patch }
    for (const [key, value] of Object.entries(values)) {
      if (value) next.set(key, key === 'salary' ? 'disclosed' : String(value)); else next.delete(key)
    }
    next.delete('page'); setParams(next)
  }
  const filtered = useMemo(() => sortPublicJobs(filterPublicJobs(jobs, filters), sort), [jobs, filters, sort])
  const pages = Math.max(1, Math.ceil(filtered.length / pageSize))
  const requestedPage = Number(params.get('page') || 1)
  const page = Math.min(pages, Number.isSafeInteger(requestedPage) && requestedPage > 0 ? requestedPage : 1)
  const visible = filtered.slice((page - 1) * pageSize, page * pageSize)
  const selected = jobs.find(job => job.publicSlug === params.get('job'))
  const selectedMissing = !!params.get('job') && !selected && !loading && !error
  const chooseJob = (slug?: string) => { const next = new URLSearchParams(params); if (slug) next.set('job', slug); else next.delete('job'); setParams(next) }
  const options = (key: 'clientName' | 'jobLocation' | 'employmentType' | 'workMode' | 'experienceRange') => [...new Set(jobs.map(job => job[key]).filter(Boolean))].sort().map(value => ({ value, label: value }))
  const activeCount = [filters.client, filters.type, filters.mode, filters.experience, filters.salary].filter(Boolean).length
  const organization = brand?.name || 'GA Digital Web Word Pvt. Ltd.'
  const filterControls = <div className="public-jobs-filter-fields">
    <FilterSelect label="Client / organization" value={filters.client} options={options('clientName')} onChange={value => update({ client: value })} />
    <FilterSelect label="Experience" value={filters.experience} options={options('experienceRange')} onChange={value => update({ experience: value })} />
    <FilterSelect label="Employment type" value={filters.type} options={options('employmentType')} onChange={value => update({ type: value })} />
    <FilterSelect label="Work mode" value={filters.mode} options={options('workMode')} onChange={value => update({ mode: value })} />
    <Checkbox checked={filters.salary} onChange={event => update({ salary: event.target.checked })}>Salary / CTC specified</Checkbox>
  </div>

  return <main className="public-jobs-board">
    <header className="public-jobs-header"><div className="public-jobs-header-inner">
      <a className="public-jobs-brand" href="/careers" aria-label={`${organization} careers home`}>
        {brand?.logoDataUrl ? <img src={brand.logoDataUrl} alt="" /> : <span className="public-jobs-brand-mark" aria-hidden="true">GA</span>}
        <span><strong>{organization}</strong><small>Careers & opportunities</small></span>
      </a><a className="public-jobs-nav" href="#open-jobs">Explore jobs <ArrowRightOutlined /></a>
    </div></header>
    <section className="public-jobs-hero"><div className="public-jobs-hero-inner">
      <div className="public-jobs-hero-copy"><span className="public-jobs-eyebrow">YOUR NEXT CHAPTER STARTS HERE</span><h1>Good work. <span>Great possibilities.</span></h1><p>Explore opportunities across our client network. Find a role that fits your skills and take your next step with us.</p></div>
      <div className="public-jobs-hero-note"><span className="public-jobs-live-dot" /> Open opportunities<span>One place. All our hiring clients.</span></div>
      <form className="public-jobs-search" onSubmit={event => { event.preventDefault(); update({ q: search.trim(), location }) }}>
        <label className="public-jobs-keywords"><span className="public-jobs-visually-hidden">Job title, skills or client</span><SearchOutlined /><Input aria-label="Job title, skills or client" value={search} onChange={event => setSearch(event.target.value)} placeholder="Job title, skills or client" bordered={false} allowClear /></label>
        <label className="public-jobs-location"><EnvironmentOutlined /><Select aria-label="Job location" showSearch allowClear placeholder="All locations" value={location || undefined} options={options('jobLocation')} onChange={value => setLocation(value || '')} bordered={false} optionFilterProp="label" /></label>
        <Button type="primary" htmlType="submit" size="large" icon={<SearchOutlined />}>Search jobs</Button>
      </form>
    </div></section>
    <div className="public-jobs-content" id="open-jobs">
      <aside className="public-jobs-filter-sidebar" aria-label="Filter jobs"><div className="public-jobs-filter-title"><h2><FilterOutlined /> Filters</h2><button type="button" onClick={() => update({}, true)}>Reset all</button></div>{filterControls}<div className="public-jobs-filter-note"><BankOutlined /><p>Opportunities from every hiring client, brought together in one place.</p></div></aside>
      <section className="public-jobs-results" aria-labelledby="public-jobs-results-title">
        <div className="public-jobs-results-heading"><div><span className="public-jobs-section-label">FIND YOUR FIT</span><h2 id="public-jobs-results-title">{loading && !jobs.length ? 'Open opportunities' : `${filtered.length} open ${filtered.length === 1 ? 'opportunity' : 'opportunities'}`}</h2><p>{filters.q ? `Results for “${filters.q}”` : 'Explore roles across our hiring clients'}{filters.location ? ` · ${filters.location}` : ''}</p></div>
          <div className="public-jobs-result-controls"><Button className="public-jobs-mobile-filter" icon={<FilterOutlined />} onClick={() => setMobileFilters(true)}>Filters{activeCount ? ` (${activeCount})` : ''}</Button><Select aria-label="Sort jobs" value={sort} options={[{ value: 'recent', label: 'Newest first' }, { value: 'title', label: 'Job title A–Z' }]} onChange={value => { const next = new URLSearchParams(params); next.set('sort', value); next.delete('page'); setParams(next) }} /></div>
        </div>
        {(Object.values(filters).some(Boolean)) && <div className="public-jobs-active-filters"><span>Filtered results</span><button type="button" onClick={() => update({}, true)}>Clear all filters <CloseOutlined /></button></div>}
        {selectedMissing && <Alert type="info" showIcon message="This role is no longer listed. Explore the current opportunities below." closable onClose={() => chooseJob()} />}
        {error ? <div className="public-jobs-empty"><Empty description={error} /><Button type="primary" onClick={() => setRefresh(value => value + 1)}>Try again</Button></div>
          : loading && !jobs.length ? <div className="public-jobs-card-list" aria-label="Loading jobs">{[1, 2, 3].map(key => <div className="public-jobs-card" key={key}><Skeleton active paragraph={{ rows: 4 }} /></div>)}</div>
          : !filtered.length ? <div className="public-jobs-empty"><SearchOutlined /><h3>{jobs.length ? 'No roles match your search' : 'No open roles right now'}</h3><p>{jobs.length ? 'Try another keyword, location or filter to discover more opportunities.' : 'Please check back soon for new opportunities across our clients.'}</p>{jobs.length > 0 && <Button onClick={() => update({}, true)}>Clear filters</Button>}</div>
          : <div className="public-jobs-card-list" aria-label="Open jobs">{visible.map(job => <JobCard key={job.publicSlug} job={job} onView={() => chooseJob(job.publicSlug)} />)}</div>}
        {!error && filtered.length > pageSize && <Pagination className="public-jobs-pagination" current={page} pageSize={pageSize} total={filtered.length} showSizeChanger={false} onChange={value => { const next = new URLSearchParams(params); next.set('page', String(value)); setParams(next) }} />}
      </section>
    </div>
    <footer className="public-jobs-footer"><span>{organization} · Careers</span><span>Powered by <b>Frevo</b></span></footer>
    <Drawer open={mobileFilters} onClose={() => setMobileFilters(false)} title="Filter jobs" placement="left" width="min(360px, 94vw)" rootClassName="public-jobs-filter-drawer" footer={<div className="public-jobs-drawer-actions"><Button onClick={() => update({}, true)}>Reset filters</Button><Button type="primary" onClick={() => setMobileFilters(false)}>Show {filtered.length} {filtered.length === 1 ? 'job' : 'jobs'}</Button></div>}><div className="public-jobs-board public-jobs-filter-body">{filterControls}</div></Drawer>
    <Drawer open={!!selected} onClose={() => chooseJob()} width="min(800px, 100vw)" title="Job details" rootClassName="public-jobs-detail-drawer" footer={selected && <div className="public-jobs-drawer-actions"><Button onClick={() => chooseJob()}>Back to jobs</Button><Button type="primary" href={publicJobApplyUrl(selected)} icon={<ArrowRightOutlined />}>Apply now</Button></div>}>
      {selected && <div className="public-jobs-detail"><span className="public-jobs-section-label">HIRING FOR {selected.clientName}</span><h2>{publicJobTitle(selected)}</h2><p className="public-jobs-reference">Job ID: {selected.positionCode}</p><JobFacts job={selected} />
        <div className="public-jobs-detail-grid"><DetailFact label="Employment type" value={selected.employmentType} /><DetailFact label="Work mode" value={selected.workMode} /><DetailFact label="Department" value={selected.department} /><DetailFact label="Open positions" value={selected.remainingPositions ? String(selected.remainingPositions) : ''} /><DetailFact label="Posted on" value={date(selected.publishedAtUtc)} /><DetailFact label="Apply by" value={date(selected.closesAtUtc)} /></div>
        <DetailSection title="Role overview" text={[selected.summary, selected.rolePurpose].filter((text, index, all) => !!text && all.indexOf(text) === index).join('\n\n')} />
        <DetailSection title="Key responsibilities" items={selected.responsibilities} /><DetailSection title="Skills" items={selected.skills} /><DetailSection title="Qualifications" items={selected.qualifications} /><DetailSection title="Certifications" items={selected.certifications} /><DetailSection title="Languages" items={selected.languages} /><DetailSection title="Benefits" items={selected.benefits} />
        <p className="public-jobs-application-note">Ready to apply? Continue to this job’s application page to submit your details and resume.</p>
      </div>}
    </Drawer>
  </main>
}

function FilterSelect({ label, value, options, onChange }: { label: string; value: string; options: { value: string; label: string }[]; onChange: (value: string) => void }) {
  return <label className="public-jobs-filter-label"><span>{label}</span><Select aria-label={`Filter ${label}`} showSearch allowClear value={value || undefined} placeholder="Any" options={options} optionFilterProp="label" onChange={value => onChange(value || '')} /></label>
}
function JobCard({ job, onView }: { job: PublicCareerJob; onView: () => void }) {
  return <article className="public-jobs-card">
    <div className="public-jobs-card-heading"><div><span className="public-jobs-client">{job.clientName}</span><h3><button onClick={onView}>{publicJobTitle(job)}</button></h3><span className="public-jobs-reference">Job ID: {job.positionCode}</span></div><span className="public-jobs-client-mark" aria-hidden="true">{initials(job.clientName) || 'GA'}</span></div>
    <JobFacts job={job} />
    {(job.summary || job.rolePurpose) && <p className="public-jobs-summary">{job.summary || job.rolePurpose}</p>}
    <div className="public-jobs-tags">{[job.employmentType, job.workMode, ...(job.skills || []).slice(0, 4)].filter((value, index, all) => value && all.indexOf(value) === index).map(value => <span key={value}>{value}</span>)}</div>
    <div className="public-jobs-card-footer"><div>{job.remainingPositions > 0 && <span><TeamOutlined /> {job.remainingPositions} {job.remainingPositions === 1 ? 'opening' : 'openings'}</span>}{date(job.closesAtUtc) ? <span>Apply by {date(job.closesAtUtc)}</span> : date(job.publishedAtUtc) && <span>Posted {date(job.publishedAtUtc)}</span>}</div><div><Button onClick={onView}>View details</Button><Button type="primary" href={publicJobApplyUrl(job)} icon={<ArrowRightOutlined />}>Apply</Button></div></div>
  </article>
}
function JobFacts({ job }: { job: PublicCareerJob }) {
  return <div className="public-jobs-facts"><Fact icon={<ClockCircleOutlined />} label="Experience" value={job.experienceRange || 'Not specified'} /><Fact icon={<DollarOutlined />} label="Salary / CTC" value={formatPublicJobSalary(job)} /><Fact icon={<EnvironmentOutlined />} label="Location" value={job.jobLocation || 'Not specified'} /></div>
}
function Fact({ icon, label, value }: { icon: ReactNode; label: string; value: string }) { return <div><span aria-hidden="true">{icon}</span><span><small>{label}</small><strong>{value}</strong></span></div> }
function DetailFact({ label, value }: { label: string; value?: string }) { return value ? <div><small>{label}</small><strong>{value}</strong></div> : null }
function DetailSection({ title, text, items }: { title: string; text?: string; items?: string[] }) { return !text && !items?.length ? null : <section><h3>{title}</h3>{text && <p>{text}</p>}{!!items?.length && <ul>{items.map((item, index) => <li key={`${index}:${item}`}>{item}</li>)}</ul>}</section> }
