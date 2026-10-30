import { createContext, useContext } from 'react'
import type { PayslipCodeMatch, PayslipIdentityMatch } from '../services/excelPayslipService'

export type CodePreviewContext = { enabled: boolean; status: 'Checking' | 'Ready' | 'Error'; matches: Map<string, PayslipCodeMatch>; identities:Map<number,PayslipIdentityMatch>;identityMode:boolean; existingFirst: boolean; setExistingFirst: (value: boolean) => void; retry: () => void; orderKey: string }
export const PayslipCodePreviewContext = createContext<CodePreviewContext>({ enabled: false, status: 'Checking', matches: new Map(),identities:new Map(),identityMode:false, existingFirst: true, setExistingFirst: () => {}, retry: () => {}, orderKey: '' })
export const usePayslipCodePreview = () => useContext(PayslipCodePreviewContext)
