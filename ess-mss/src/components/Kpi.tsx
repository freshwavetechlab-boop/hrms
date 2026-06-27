export function Kpi({ label, value, hint }: { label: string; value: string; hint: string }) {
  return <article className="kpi"><span>{label}</span><strong>{value}</strong><small>{hint}</small></article>
}

export function Feature({ title, items }: { title: string; items: string[] }) {
  return <section className="compact-card"><h3>{title}</h3>{items.map(item => <button key={item}>{item}<b>&gt;</b></button>)}</section>
}
