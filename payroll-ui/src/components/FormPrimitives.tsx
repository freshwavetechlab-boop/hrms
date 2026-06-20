import type { ReactNode } from 'react'
import AppIcon from './AppIcon'

export function Card(p: { t: string; children: ReactNode }) { return <section className="card"><header><i className="blue"><AppIcon name="check" /></i><div><h3>{p.t}</h3><p>Settings</p></div></header>{p.children}</section> }
export function F(p: { l: string; w?: boolean; children: ReactNode }) { return <label className={p.w ? 'wide' : ''}><span>{p.l}</span>{p.children}</label> }
export function Sel(p: { v: string; set: (v: string) => void; a: string[] }) { return <select value={p.v} onChange={event => p.set(event.target.value)}><option value="">Select</option>{p.a.map(item => { const [value, ...label] = item.split(':'); return <option value={label.length ? value : item} key={item}>{label.join(':') || item}</option> })}</select> }
export function Chk(p: { l: string; v: boolean; set: (v: boolean) => void }) { return <label><span>{p.l}</span><input type="checkbox" checked={p.v} onChange={event => p.set(event.target.checked)} /></label> }
