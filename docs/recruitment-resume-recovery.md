# Missing recruitment resume originals

An application/resume record and successful parsing do not prove that its original
file is still present in attachment storage. Attachment access audit entries record
authorization before the storage read; they are not evidence of successful delivery.

## Saved text fallback

Authorized attachment preview/download now tries the original file first, including
the existing development-to-production read path. If that returns file-not-found,
a candidate resume can return the text already saved in `recruitment_candidate_resumes`.
The fallback is bound to the exact active attachment, candidate and client. Other
attachment types and resumes without saved text retain the existing missing-file
response. No candidate/application/file metadata is rewritten and no model is called.

The response is explicitly `text/plain`, labelled **Recovered resume text - original
file unavailable**, with a `.recovered.txt` download name. This preserves available
resume content; it does not restore PDF formatting, images, signatures or the original
document. Existing originals continue to use their original bytes/type/name. The API
must be updated for the fallback to be available through the application.

## Recovering original files

Before changing storage mounts or redeploying the API, preserve the current API
container's `/app/App_Data`, especially `attachments`. A new mount can hide existing
container files, and replacement/pruning can remove the only remaining copy.

1. In Coolify's HRMS API **Persistent Storage**, inspect the destination path and
   source volume/bind mount. The configured production attachment root is normally
   `/app/App_Data/attachments`; verify actual environment overrides too.
2. Inspect current and stopped API containers, detached Docker volumes, old bind
   paths and available backups. Do not prune them during recovery.
3. Locate files by the exact `entity_attachments.storage_key`, including the original
   `form_submission` path for public applications. Promotion to a candidate changes
   ownership metadata, not the storage key or the location of the physical file.
4. Compare recovered bytes with `file_size_bytes` and `sha256_hash`. Preserve originals
   and backup evidence before copying verified files into the durable attachment root.
5. Verify preview and download for older and recent uploads. Configure a persistent
   volume/bind mount for future writes, populate it with the preserved files before
   switching the API to it, and back it up independently of database backups.

The Dockerfile creating an attachment directory does not establish a persistent
Coolify volume. Database backups and saved extracted text do not contain original
PDF bytes. Missing files alone do not prove which deployment or storage change caused
their disappearance.
