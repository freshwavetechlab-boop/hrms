import { useId, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { ArrowRightOutlined, CheckOutlined, ClockCircleOutlined, RollbackOutlined } from '@ant-design/icons'
import { Empty, Tag } from 'antd'
import { recruitmentJourney, type JourneyVisit } from '../utils/recruitmentJourney'
import './RecruitmentJourneyPath.css'

type Props<T extends JourneyVisit> = {
  visits: T[]
  renderDetails: (visit: T) => ReactNode
}

export default function RecruitmentJourneyPath<T extends JourneyVisit>({ visits, renderDetails }: Props<T>) {
  const rows = recruitmentJourney(visits)
  const pathId = useId().replaceAll(':', '')
  const list = useRef<HTMLOListElement>(null)
  const [geometry, setGeometry] = useState({ width: 140, height: 0, points: [] as { x: number; y: number }[] })
  const signature = rows.map(({ row }) => `${row.id}:${row.displayOrder}:${row.status}`).join('|')
  useLayoutEffect(() => {
    const root = list.current
    if (!root) return
    const measure = () => {
      const width = Number.parseFloat(getComputedStyle(root).getPropertyValue('--journey-track-width'))
      const markers = [...root.querySelectorAll<HTMLElement>('.recruitment-journey-node')]
      const rootBox = root.getBoundingClientRect()
      const points = markers.map(marker => { const box = marker.getBoundingClientRect(); return { x: box.left - rootBox.left + box.width / 2, y: box.top - rootBox.top + box.height / 2 } })
      setGeometry(current => JSON.stringify(current) === JSON.stringify({ width, height: root.scrollHeight, points }) ? current : { width, height: root.scrollHeight, points })
    }
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(root)
    root.querySelectorAll('li').forEach(row => observer.observe(row))
    return () => observer.disconnect()
  }, [signature])
  if (!rows.length) return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No stage visits recorded yet." />
  const orders = rows.map(({ row }, index) => row.displayOrder ?? index)
  const min = Math.min(...orders), max = Math.max(...orders)
  const returns = rows.filter(row => row.direction === 'back').length
  const current = rows.find(({ row }) => ['Active', 'Paused'].includes(row.status))
  return <section className="recruitment-journey" aria-label="Pipeline journey">
    <header className="recruitment-journey-heading"><div><span>PIPELINE JOURNEY</span><h3>Every move, in order</h3><p>Follow the arrows. Each return keeps its own visit and time.</p></div><div className="recruitment-journey-counts"><b>{rows.length}<small>Visits</small></b><b>{returns}<small>Returns</small></b></div></header>
    {current && <div className="recruitment-journey-current"><span className="recruitment-journey-live-dot" /> Current · <strong>{current.row.stageName}</strong></div>}
    <ol ref={list} className="recruitment-journey-list">
      <svg className="recruitment-journey-track" aria-hidden="true" width={geometry.width} height={geometry.height}>
        <defs>{['forward', 'back', 'repeat'].map(direction => <marker key={direction} id={`${pathId}-${direction}`} viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M 1 1 L 8 5 L 1 9" fill="none" stroke={direction === 'back' ? '#d97706' : '#6b4eff'} strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" /></marker>)}</defs>
        {geometry.points.slice(1, rows.length).map((point, index) => {
          const previous = geometry.points[index], direction = rows[index + 1]?.direction || 'forward'
          const startY = previous.y + 18, endY = point.y - 22, middleY = (startY + endY) / 2
          return <path key={rows[index + 1].row.id} data-journey-direction={direction} className={`recruitment-journey-link is-${direction}`} d={`M ${previous.x} ${startY} C ${previous.x} ${middleY}, ${point.x} ${middleY}, ${point.x} ${endY}`} markerEnd={`url(#${pathId}-${direction === 'start' ? 'forward' : direction})`} />
        })}
      </svg>
      {rows.map(({ row, direction, visit, sequence }, index) => <li key={row.id} data-testid={`journey-visit-${row.id}`} data-direction={direction} className={`recruitment-journey-visit is-${direction}${['Active', 'Paused'].includes(row.status) ? ' is-current' : ''}`}>
        <span className="recruitment-journey-node" style={{ left: `calc(22px + (var(--journey-track-width) - 50px) * ${max === min ? 0.5 : (orders[index] - min) / (max - min)})` }} aria-hidden="true">{direction === 'back' ? <RollbackOutlined /> : row.status === 'Completed' ? <CheckOutlined /> : <ClockCircleOutlined />}</span>
        <article className="recruitment-journey-card"><div className="recruitment-journey-move"><span>MOVE {String(sequence).padStart(2, '0')}</span><Tag color={direction === 'back' ? 'orange' : direction === 'start' ? 'default' : 'purple'} icon={direction === 'back' ? <RollbackOutlined /> : <ArrowRightOutlined />}>{direction === 'start' ? 'Started' : direction === 'back' ? 'Moved back' : direction === 'repeat' ? 'Reopened' : 'Moved forward'}</Tag>{visit > 1 && <span className="recruitment-journey-revisit">Visit {visit}</span>}</div><div className="recruitment-journey-title"><h4>{row.stageName}</h4><Tag color={row.status === 'Completed' ? 'green' : row.status === 'Paused' ? 'gold' : 'blue'}>{row.status === 'Active' ? 'Current' : row.status}</Tag></div><time dateTime={row.enteredAtUtc!}>{new Date(row.enteredAtUtc!).toLocaleString('en-IN', { dateStyle: 'medium', timeStyle: 'short' })}</time><div className="recruitment-journey-details">{renderDetails(row)}</div></article>
      </li>)}
    </ol>
  </section>
}
