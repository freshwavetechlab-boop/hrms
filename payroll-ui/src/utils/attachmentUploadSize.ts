import type { AttachmentFieldConfiguration, EntityAttachment } from '../types/payroll'

const sizeLabel = (bytes: number) => bytes >= 1024 * 1024 ? `${Number((bytes / 1024 / 1024).toFixed(2))} MB` : `${Number((bytes / 1024).toFixed(2))} KB`

export function attachmentUploadSizeError(
  configuration: Pick<AttachmentFieldConfiguration, 'id' | 'fieldLabel' | 'maximumFileSizeBytes' | 'maximumTotalSizeBytes'>,
  file: Pick<File, 'size'>,
  existing: Pick<EntityAttachment, 'fieldConfigurationId' | 'fileSizeBytes'>[] = [],
) {
  const label = configuration.fieldLabel || 'Document'
  if (!file.size) return `${label}: choose a non-empty file.`
  if (configuration.maximumFileSizeBytes > 0 && file.size > configuration.maximumFileSizeBytes)
    return `${label}: file size must not exceed ${sizeLabel(configuration.maximumFileSizeBytes)}.`
  const currentBytes = existing.filter(item => item.fieldConfigurationId === configuration.id).reduce((sum, item) => sum + item.fileSizeBytes, 0)
  if (configuration.maximumTotalSizeBytes && currentBytes + file.size > configuration.maximumTotalSizeBytes)
    return `${label}: combined file size must not exceed ${sizeLabel(configuration.maximumTotalSizeBytes)}.`
  return ''
}
