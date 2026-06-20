import { useMemo, useState } from 'react'

export type Column<T> = { key: keyof T; label: string; render?: (row: T) => string }

export default function DataTable<T extends { id: number }>({ rows, columns, onEdit }: { rows: T[]; columns: Column<T>[]; onEdit: (row: T) => void }) {
  const [q, setQ] = useState(''), [sort, setSort] = useState<keyof T>(columns[0].key), [desc, setDesc] = useState(false)
  const data = useMemo(() => rows.filter(r => JSON.stringify(r).toLowerCase().includes(q.toLowerCase())).sort((a, b) => {
    const x = String(a[sort] ?? ''), y = String(b[sort] ?? '')
    return (desc ? -1 : 1) * x.localeCompare(y)
  }), [rows, q, sort, desc])
  const click = (key: keyof T) => { setDesc(sort === key ? !desc : false); setSort(key) }
  return <div className="data-table"><input className="table-filter" placeholder="Filter..." value={q} onChange={e => setQ(e.target.value)} /><table><thead><tr>{columns.map(c => <th onClick={() => click(c.key)} key={String(c.key)}>{c.label}{sort === c.key ? (desc ? ' ↓' : ' ↑') : ''}</th>)}<th /></tr></thead><tbody>{data.map(r => <tr key={r.id}>{columns.map(c => <td key={String(c.key)}>{c.render ? c.render(r) : String(r[c.key] ?? '')}</td>)}<td><button type="button" onClick={() => onEdit(r)}>Edit</button></td></tr>)}</tbody></table>{!data.length && <p className="empty">No records</p>}</div>
}
