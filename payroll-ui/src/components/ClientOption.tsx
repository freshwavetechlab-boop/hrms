import './ClientOption.css'

type ClientChoice<Value extends string | number> = { value: Value; label: string; code?: string | null; disabled?: boolean }

export const clientChoice = <Value extends string | number>(client: { id: Value; name: string; code?: string | null }) =>
  ({ value: client.id, label: client.name, code: client.code ?? '' })

export const clientSearchText = (name: string, code?: string | null) => [name, code?.trim()].filter(Boolean).join(' ')

export function ClientOptionName({ name, code }: { name: string; code?: string | null }) {
  return <span className="client-choice-copy"><span>{name}</span>{code?.trim() && <small>{code}</small>}</span>
}

/** Keep selection compact. Only the open list adds the code underneath the name. */
export function clientSelectProps<Value extends string | number>(choices: ClientChoice<Value>[]) {
  return {
    showSearch: true,
    optionFilterProp: 'searchText',
    optionLabelProp: 'selectedLabel',
    options: choices.map(choice => ({
      ...choice, selectedLabel: choice.label, searchText: clientSearchText(choice.label, choice.code),
      label: choice.code ? <ClientOptionName name={choice.label} code={choice.code} /> : choice.label,
    })),
  }
}
