# Resume intake and parser layout lessons

Verified with a mixed executive-CV intake (PDF, DOCX and legacy DOC). Keep this knowledge anonymized: never store candidate names, contacts or complete private resumes as reusable examples.

## Identity in columns and decorated headings

Text extraction may interleave a left contact column, a split name in a right column, summary prose and employment history. A filename can help reconnect tokens only when the tokens are evidenced in the document header in order. Do not supply a missing surname from an email or filename alone. One-letter middle initials, military ranks, doctorate prefixes and retired/degree suffixes must not cause an occupational heading to become the candidate name.

`Technology Leader`, `Tech and Product Leader`, `Contact Information`, `Contact Me At`, `Curriculum Vitae` (including spelling variants), skills and competency headings are not person names. Company/product vocabulary and sentence fragments are not identity. Preserve unknown facts for recruiter review.

## Current location versus employer and narrative

Prefer candidate contact/location/address evidence. Stop at PDF column separators and the next contact or language label. Employment responsibilities, a company or R&D centre, a competency heading and the target job's location do not establish the candidate's current/residential location. A location is never copied from another resume or a retrieved JD.

## Independent columns and current employment

A PDF can have a Skills sidebar beside a Professional Experience timeline, followed by full-width pages. Separate columns only when repeated gutters and independent section headings establish that layout; preserve row associations in ordinary aligned tables. Keep wrapped bullet tails in their original section. A sidebar skill such as `Artificial intelligence design` must never become an employer. Reconnect token-only wrapped email fragments only inside the same detected column, never across unrelated contact rows.

An ongoing `Current`, `Present`, `Currently`, `Still working`, `Onwards` or equivalent marker outranks a newer historical start date. With no ongoing role, use the latest end date. Support date-first co-titles, `Employer (dates) - Role`, and `Employer - Role` followed by a date row. Multiple co-titles are not separate employers. Reporting-manager names/titles and team-size metadata after the date do not replace the candidate's role. Do not borrow a responsibility bullet, summary aspiration, email or arbitrary following line as company/title evidence. An explicit overall career duration must not be replaced by an incomplete set of dated roles. Recognize `Skillset` headings and common round/font bullets without letting narrative sections leak into the skill list.

## Education versus language-model terminology

Use Education/Executive Development boundaries. `LLM & SLM Design & development`, language-model frameworks and models trained on legal corpora are technical evidence, not an LL.M. law degree. Keep explicit law degrees valid. Preserve skill bullets that wrap onto a lower-case continuation such as `background`, and stop skill collection at the next independent section.

## Admin and published-link parity

Bulk/admin preview, final attachment processing, published candidate forms and stored-resume processing all call the shared `ResumeParsingService`. Maintain extraction changes in that shared core; do not create a client-specific or UI-specific parser. Public-form prefill consumes the canonical job/degree facts too: it must not reintroduce a separate first-line guess when a shared fact is unknown. The published form's optional AI enhancement remains controlled by the posting's existing settings, while the local core facts use the same layout/date rules. Test shared entry points and public prefill with the same synthetic source and verify a representative real PDF deliberately. A persisted recruiter edit is not silently overwritten by a parser upgrade.

## Draft review and numeric defaults

Before final upload a recruiter can remove any parsed draft. Remove the same file from the upload payload and preserve other edited drafts using file identity, including when two files have the same filename. Removing a draft does not delete an existing candidate or application.

An explicit **Fill missing CTC / notice with 0** action applies zero to absent current CTC, expected CTC and notice period only. Existing numbers and explicit zeros remain unchanged. Zero is a supplied value, not blank, in review, transport, save and display. These manual defaults must never be labelled as source evidence or used to inflate an ATS score.

## Decoder availability and text encoding

A PDF page renderer may be installed while the OCR recognizer is absent. Native Windows OCR must remain a fallback when rendering succeeds but recognition fails too. Decode PDF text-command output explicitly as UTF-8 so bullets, smart punctuation and zero-width format characters do not become visible garbage or bogus company/skill values. A legacy DOC without its decoder remains a review item. A cleaned filename name suggestion must be labelled as a suggestion, separate from parsed source facts; discard file numbering and rank/degree suffixes.

## Learning and verification boundary

The local parser uses deterministic layout validation. Quinn's resume prompt includes the same reusable layout guidance through `ResumeParsingPolicy`. FrevoPilot retrieves this document as support knowledge. This is a parser/prompt/RAG update; it does not change model weights, approved job requirements, existing candidate facts or saved scores. Reparse a source deliberately before importing or updating it. Keep regression cases synthetic and test readable and unavailable-decoder paths separately.

## Shared job policy for upload paths

Published careers links and internal candidate forms use the same public-session upload endpoint and `ParseResumeDraftAsync`. Job single/bulk intake and the pipeline's Add resume action use `RecruitmentResumeIntake` and the same intake API. Pipeline/batch candidate editing and candidate document replacement use `UploadResumeAsync`; job-bound profile controls pass the exact `ApplicationId`, validated against the candidate and actor's client before attachment storage. A generic profile replacement retains latest-active-application selection. The selected posting's parsing/AI switches and client memory apply, including a globally owned candidate applying to a particular client. An absent posting does not automatically opt into AI. Intake's explicitly selected scope must not inherit another pre-existing application's settings.

`ResumeParsingJobPolicy` resolves the switches; `ParseForJobAsync` runs the shared local parser/memory, then AI review whenever the job enables AI. Parsing off blocks both parsing and AI. AI off retains local extraction. Filled local fields never skip the model: an apparently complete local value can still be wrong. The same rule applies to public/background stream parsing. Both concrete AI consumers and the parser's suggestion interface share the same provider instance and quotas. Verified email recovery uses the same job policy after attachment acceptance and preserves its existing resume/application guard and pipeline state.

Temporary single/bulk preview and final intake resolve the same server-side selected-job policy and validated client/position/posting context. Preview runs `ParseForJobAsync`, including AI review before recruiter review when the job enables it. A request cannot enable parsing or AI against the posting's settings, and a posting from another client/position is rejected before parsing. A no-posting Talent Pool preview remains local. Candidate, application and attachment storage records remain unsaved until Final upload, while AI provider usage may be recorded. Final intake preserves reviewed fields and performs its AI review only after attachment acceptance. A configured available model is still required; disabled/unavailable/quota-limited/truncated model output is explicitly unverified rather than silently marked AI-reviewed. No separate parser implementation is needed for a page or upload source.

AI review can correct nonempty company, designation and qualification values. Corrections require Completed status, exact field metadata at least 0.80, a value present in the original source, and a literal evidence block. Employment evidence must establish an ongoing/current role or the latest ended role; a historical employer or skill phrase cannot replace the current company. Qualification evidence must describe a completed degree rather than technical LLM terminology or a pursuing degree. Source-backed AI skills supplement existing skills. Unverified/conflicting/omitted proposals remain review issues. Recruiter-reviewed draft facts are preserved; exact verified memory is reapplied after AI merging. These checks improve suggestions without claiming perfect accuracy or automatically training model weights.

## Verified recruiter feedback and API memory

Qwen/Quinn parsing has supervised feedback memory in the HRMS API, without Paperclip or automatic weight training. A successful authenticated recruiter edit to an existing profile, or a successfully stored reviewed bulk draft, can retain a changed current-company, designation, location, qualification or skill value only when it is explicitly supported by the same resume text. Parser/AI output, public applicant input, unchanged saves, force-upload guesses, unknown placeholders, CTC and notice-period zero defaults cannot promote themselves into this memory.

Exact-source corrections are keyed by client and source fingerprint, updated in place and revalidated against the incoming text. A new resume can reuse a verified field label (for example `Current designation:` or `Technical strengths:`); it never receives another candidate's employer, degree, skills, identity or contacts. Model prompts receive a small set of matching field-label hints, not stored candidate values or full resumes. Admin preview, final attachment parsing, published forms and stored-stream processing share the memory through `ResumeParsingService`. Fresh human review takes precedence over old memory.

Memory is encrypted with a separate purpose under the existing portable integration protector at `App_Data/Recruitment/resume-learning.enc`. Preserve this private API directory across restarts/deploys. It holds at most 512 reviewed source records, 180 days, 1 MiB plaintext / 2 MiB encrypted; unreadable storage/keys cannot fail recruitment saves or parsing. Set `Recruitment:ResumeLearningEnabled=false` to disable it. Authorised recruitment managers can inspect scoped counts with `GET /api/recruitment/resume-learning?clientId=...`; settings managers can clear that client with DELETE. Candidate deletion, archive or consent revocation removes that candidate's retained corrections. No schema migration is required.

This is HRMS API memory; the model gateway stays stateless. The file belongs to one API deployment/process and must not be concurrently shared as a multi-instance database. Disabling reuse does not prevent authorised inspection or clearing. A wrapped end date is not an ongoing role, and an explicit Current/Present Profile caption outranks an older ended job. Structured education/skills sidebars can accompany a dated main column without an Experience heading; keep the contact header early enough for identity/location extraction. Start-only/ambiguous employment dates remain explicit review items.

## Qualifications, photographs and long CPU-model input

Photographs do not establish professional facts. Read the written PDF/DOCX text and the existing OCR path; unreadable text and an unavailable legacy-DOC decoder remain explicit review cases. Independent columns may put education on either side of professional experience. Inline `SKILLS | ...` and `EDUCATION | ...` headings must retain their content. A degree-like token inside an institution name (such as MS) cannot hide a later explicit Ph.D. on the same row. Rank completed qualifications; keep pursuing/expected degrees as ongoing education. ABOUT ME, MS Teams/Office, master-data and domain/regulatory LLM tooling are not degrees.

An employer can precede or follow a date row, and a date-first role can be on the same line. Do not borrow an adjacent historical job. A company description containing numbers can be skipped only if its immediately following line is an actual role header. Keep a role specialization after a dash when an explicit employer has its own line. Strip reporting and team-size metadata. A technical tool literally named in professional summary/experience is source evidence even without a Skills heading; preserve negation and do not infer additional tools from the target role.

The current small Qwen gateway has a bounded request/context budget. Long resumes use selected whole, verbatim lines from across the source, including current employment, later education and skills, rather than silently clipping the first page. The packet is labelled as excerpts; grounding still uses the complete original text. Omitted evidence stays unknown. The existing provider admission, output limits, opt-in parsing settings and fallback behavior remain in force. Field-level review issues are returned for missing evidence and a full skill-list limit; they are not an accuracy guarantee.
