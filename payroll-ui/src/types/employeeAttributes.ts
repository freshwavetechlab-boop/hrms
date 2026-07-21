export type EmployeeAttributeFieldTypeCode =
  | 'TEXT'
  | 'TEXTAREA'
  | 'NUMBER'
  | 'DATE'
  | 'DATETIME'
  | 'EMAIL'
  | 'PHONE'
  | 'SELECT'
  | 'SEARCH_SELECT'
  | 'MULTI_SELECT'
  | 'RADIO'
  | 'CHECKBOX'
  | 'UPLOAD'

export type EmployeeAttributeOption = {
  id: number
  fieldId: number
  optionCode: string
  optionLabel: string
  displayOrder: number
  isActive: boolean
}

export type EmployeeAttributeAttachmentConstraints = {
  allowMultiple: boolean
  maximumFileCount: number
  maximumFileSizeBytes: number
  maximumTotalSizeBytes?: number | null
  allowedExtensions: string[]
  allowedMimeTypes: string[]
}

export type EmployeeAttributeField = {
  id: number
  formVersionId: number
  sectionId: number
  fieldTypeCode: EmployeeAttributeFieldTypeCode
  stableFieldCode: string
  label: string
  placeholder: string
  helpText: string
  isRequired: boolean
  displayOrder: number
  widthColumns: number
  minimumLength?: number | null
  maximumLength?: number | null
  minimumNumber?: number | null
  maximumNumber?: number | null
  minimumDate?: string | null
  maximumDate?: string | null
  attachmentFieldConfigurationId?: number | null
  attachmentConstraints?: EmployeeAttributeAttachmentConstraints | null
  lookupSourceCode: string
  isActive: boolean
  options: EmployeeAttributeOption[]
}

export type EmployeeAttributeSection = {
  id: number
  formVersionId: number
  sectionCode: string
  sectionLabel: string
  description: string
  displayOrder: number
  fields: EmployeeAttributeField[]
}

export type EmployeeAttributeForm = {
  id: number
  formDefinitionId: number
  formCode: string
  formName: string
  versionNumber: number
  status: string
  sections: EmployeeAttributeSection[]
}

export type EmployeeAttributeValue = {
  fieldId: number
  textValue?: string | null
  integerValue?: number | null
  decimalValue?: number | null
  dateValue?: string | null
  dateTimeValue?: string | null
  booleanValue?: boolean | null
  selectedOptionIds?: number[]
  selectedOptionValues?: string[]
}

export type EmployeeAttributeContext = {
  employeeId: number
  clientId: number
  infotypeCode: string
  forms?: EmployeeAttributeForm[] | null
  values?: EmployeeAttributeValue[] | null
  files?: EmployeeAttributeFile[] | null
}

export type EmployeeAttributeFile = {
  publicId: string
  fieldConfigurationId: number
  originalFileName: string
  fileSizeBytes?: number
  uploadedAtUtc?: string
}

export type EmployeeAttributeLookupOption = {
  value: string
  label: string
  description?: string
}

export type SaveEmployeeAttributeValuesRequest = {
  clientId: number
  infotypeCode: string
  changeReason: string
  values: EmployeeAttributeValue[]
}

export type SaveEmployeeAttributeValuesResult = {
  employeeId: number
  savedCount: number
  values: EmployeeAttributeValue[]
}
