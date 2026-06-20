import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import './App.css'

type Organization = {
  id?: number
  name: string
  legalName: string
  businessType: string
  pan: string
  gstin: string
  fiscalYearStart: string
  addressLine1: string
  addressLine2: string
  city: string
  state: string
  postalCode: string
  country: string
  bankName: string
  accountNumber: string
  ifscCode: string
  createdAt?: string
  updatedAt?: string
}

const initialOrganization: Organization = {
  name: '',
  legalName: '',
  businessType: '',
  pan: '',
  gstin: '',
  fiscalYearStart: 'April',
  addressLine1: '',
  addressLine2: '',
  city: '',
  state: '',
  postalCode: '',
  country: 'India',
  bankName: '',
  accountNumber: '',
  ifscCode: '',
}

const API_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:5062'

function App() {
  const [organization, setOrganization] = useState<Organization>(initialOrganization)
  const [status, setStatus] = useState<string>('Loading payroll organization setup...')
  const [isSaving, setIsSaving] = useState(false)
  const [hasLoaded, setHasLoaded] = useState(false)

  useEffect(() => {
    const loadOrganization = async () => {
      try {
        const response = await fetch(`${API_URL}/api/organization`)
        if (response.ok) {
          const data = await response.json()
          setOrganization({
            ...initialOrganization,
            ...data,
            fiscalYearStart: data.fiscalYearStart || 'April',
            country: data.country || 'India',
          })
          setStatus('Organization data loaded. You can update the settings below.')
        } else if (response.status === 404) {
          setStatus('Start by setting up your organization details.')
        } else {
          setStatus('Unable to load organization details. Please check the API.')
        }
      } catch {
        setStatus('Failed to connect to payroll API. Ensure the backend is running.')
      } finally {
        setHasLoaded(true)
      }
    }

    loadOrganization()
  }, [])

  const handleFieldChange = (field: keyof Organization, value: string) => {
    setOrganization(prev => ({ ...prev, [field]: value }))
  }

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setIsSaving(true)
    setStatus('Saving your organization setup...')

    try {
      const response = await fetch(`${API_URL}/api/organization`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(organization),
      })

      if (response.ok) {
        const data = await response.json()
        setOrganization(prev => ({ ...prev, ...data }))
        setStatus('Organization setup saved successfully. Payroll is ready to continue.')
      } else {
        const error = await response.json().catch(() => null)
        setStatus(
          error?.error ?? 'Could not save organization setup. Please verify the form and try again.',
        )
      }
    } catch {
      setStatus('Unable to save. Check the backend connection and try again.')
    } finally {
      setIsSaving(false)
    }
  }

  return (
    <div className="page-shell">
      <header className="hero-panel">
        <div>
          <span className="eyebrow">Organization setup</span>
          <h1>Build your payroll foundation.</h1>
          <p>
            Capture company details, legal identifiers, fiscal year, and bank information for
            payroll compliance.
          </p>
        </div>
        <div className="hero-chip">Module 1</div>
      </header>

      <main className="form-panel">
        <section className="status-panel">
          <h2>Organization status</h2>
          <p>{status}</p>
        </section>

        <form onSubmit={handleSubmit} className="organization-form">
          <div className="section-card">
            <h3>Company details</h3>
            <div className="grid-two">
              <label>
                Organization name
                <input
                  value={organization.name}
                  onChange={e => handleFieldChange('name', e.target.value)}
                  placeholder="Acme Payroll Private Limited"
                  required
                />
              </label>
              <label>
                Legal name
                <input
                  value={organization.legalName}
                  onChange={e => handleFieldChange('legalName', e.target.value)}
                  placeholder="Acme Payroll Pvt Ltd"
                />
              </label>
            </div>
            <div className="grid-two">
              <label>
                Business type
                <input
                  value={organization.businessType}
                  onChange={e => handleFieldChange('businessType', e.target.value)}
                  placeholder="Private Limited Company"
                />
              </label>
              <label>
                Fiscal year start
                <select
                  value={organization.fiscalYearStart}
                  onChange={e => handleFieldChange('fiscalYearStart', e.target.value)}
                >
                  <option>April</option>
                  <option>January</option>
                  <option>July</option>
                  <option>October</option>
                </select>
              </label>
            </div>
          </div>

          <div className="section-card">
            <h3>Compliance identifiers</h3>
            <div className="grid-two">
              <label>
                PAN
                <input
                  value={organization.pan}
                  onChange={e => handleFieldChange('pan', e.target.value)}
                  placeholder="AAAAA1234A"
                />
              </label>
              <label>
                GSTIN
                <input
                  value={organization.gstin}
                  onChange={e => handleFieldChange('gstin', e.target.value)}
                  placeholder="22AAAAA0000A1Z5"
                />
              </label>
            </div>
          </div>

          <div className="section-card">
            <h3>Registered address</h3>
            <label>
              Address line 1
              <input
                value={organization.addressLine1}
                onChange={e => handleFieldChange('addressLine1', e.target.value)}
                placeholder="123 Corporate Avenue"
              />
            </label>
            <label>
              Address line 2
              <input
                value={organization.addressLine2}
                onChange={e => handleFieldChange('addressLine2', e.target.value)}
                placeholder="Suite 500"
              />
            </label>
            <div className="grid-three">
              <label>
                City
                <input
                  value={organization.city}
                  onChange={e => handleFieldChange('city', e.target.value)}
                />
              </label>
              <label>
                State
                <input
                  value={organization.state}
                  onChange={e => handleFieldChange('state', e.target.value)}
                />
              </label>
              <label>
                Postal code
                <input
                  value={organization.postalCode}
                  onChange={e => handleFieldChange('postalCode', e.target.value)}
                />
              </label>
            </div>
            <label>
              Country
              <input
                value={organization.country}
                onChange={e => handleFieldChange('country', e.target.value)}
              />
            </label>
          </div>

          <div className="section-card">
            <h3>Bank details</h3>
            <div className="grid-two">
              <label>
                Bank name
                <input
                  value={organization.bankName}
                  onChange={e => handleFieldChange('bankName', e.target.value)}
                  placeholder="National Bank"
                />
              </label>
              <label>
                Account number
                <input
                  value={organization.accountNumber}
                  onChange={e => handleFieldChange('accountNumber', e.target.value)}
                  placeholder="XXXX XXXX XXXX"
                />
              </label>
            </div>
            <label>
              IFSC code
              <input
                value={organization.ifscCode}
                onChange={e => handleFieldChange('ifscCode', e.target.value)}
                placeholder="ABCD0123456"
              />
            </label>
          </div>

          <footer className="form-actions">
            <button className="primary-button" type="submit" disabled={isSaving || !hasLoaded}>
              {isSaving ? 'Saving organization...' : 'Save organization setup'}
            </button>
          </footer>
        </form>
      </main>
    </div>
  )
}

export default App
