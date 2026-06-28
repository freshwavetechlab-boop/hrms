import type { ReactNode } from 'react'
import SearchSelect from './SearchSelect'

export function Card(p: { t: string; children: ReactNode }) { return <section className="card section-card" aria-label={p.t}>{p.children}</section> }
export function F(p: { l: ReactNode; w?: boolean; children: ReactNode }) { return <label className={p.w ? 'wide' : ''}><span>{p.l}</span>{p.children}</label> }
export function Sel(p: { v: string | number; set: (v: string) => void; a: string[] }) { return <SearchSelect value={p.v} onChange={p.set} options={[{ value: '', label: 'Select' }, ...p.a.map(item => { const [value, ...label] = item.split(':'); return { value: label.length ? value : item, label: label.join(':') || item } })]} /> }
export function Chk(p: { l: string; v: boolean; set: (v: boolean) => void }) { return <label><span>{p.l}</span><input type="checkbox" checked={p.v} onChange={event => p.set(event.target.checked)} /></label> }
