# Plan: POC polish — fix the demo-facing rough edges

## Context for the agent

This repo is the **HHSC / MMRS Document Redaction** proof-of-concept: a React (Vite/TS)
frontend + ASP.NET Core (.NET 10) API that detects PII with Azure AI Language, gets PDF
word coordinates from Azure AI Document Intelligence, and redacts only reviewer-selected
instances (TXT string-mask, DOCX OpenXML edit, PDF rasterize-and-burn).

**This is a POC, not production.** Do **NOT** add authentication, retention/cleanup jobs,
rate limiting, secret rotation, multi-tenancy, or broad test suites. Those are intentionally
out of scope. The goal of this work is only to fix things that are **misleading, visibly
broken in a demo, or a fresh-clone landmine**. Five tasks, ordered by value. Each task is
independent — commit them separately so any one can be reverted without touching the others.

**Environment / build notes (already verified in this repo):**
- Backend builds with: `cd backend/DocumentRedaction.API; dotnet build`
- Backend tests: `cd backend/DocumentRedaction.Tests; dotnet test`
- Frontend: `cd frontend; npm install; npm run build` (must stay type-clean; `tsc` runs in build)
- Do a clean `dotnet build` and `npm run build` before AND after each task. Fix all warnings
  you introduce. Do not leave the tree in a non-building state between commits.
- Known gotcha: editor buffers may lag disk. After edits, verify on disk before building.

---

## Task 1 — Fix PDF over-redaction (box unioning blacks out too much) ⭐ highest value, lowest risk

**Problem.** `MapBoxes` in
[backend/DocumentRedaction.API/Services/DocumentRedactionOrchestrator.cs](backend/DocumentRedaction.API/Services/DocumentRedactionOrchestrator.cs)
(method around the "Maps an entity span to one bounding box per page" comment) unions **all**
overlapping words per page into a **single** rectangle. When an entity spans two lines or two
columns, the union becomes a giant box that blacks out unrelated content between the words.
This is visible on screen and in the burned PDF output.

**Fix.** Produce **one box per text line** the entity occupies, instead of one union per page.

Implementation in `MapBoxes`:
1. Keep the existing "collect words whose span overlaps `[offset, offset+length)`" loop.
2. Instead of unioning all matched words per page, **group matched words into line buckets**:
   two words are on the same line if they share the same `Box.Page` AND their vertical extents
   overlap (e.g. the center-Y of one falls within the `[Y, Y+Height]` of the other, or use a
   tolerance of `min(height)*0.5`). Union only within a line bucket.
3. Emit one `DetectedBox` per (page, line) bucket. Order by page, then by Y, then X.

Keep the normalized 0..1 coordinate convention unchanged (the burn code in
`PdfDocumentProcessor.PaintBars` multiplies by bitmap width/height and adds a 2px pad — leave
it as-is). TXT/DOCX still return `Array.Empty<DetectedBox>()` (no word geometry).

**Acceptance criteria.**
- An entity whose words all sit on one line → exactly one box (same as before).
- A multi-line / multi-column entity → one tight box per line, no page-spanning bar.
- Existing test `DetectAsync_maps_pdf_word_boxes_into_a_union_box_per_entity` in
  [backend/DocumentRedaction.Tests/DocumentRedactionOrchestratorTests.cs](backend/DocumentRedaction.Tests/DocumentRedactionOrchestratorTests.cs)
  currently asserts a single union box for `"Dr Smith"` (two words on the same line) — that
  case MUST still pass (one line = one box). If the test's two words are on the same line it
  needs no change; verify and keep it green.
- **Add** a new test: two words on different lines (different Y, non-overlapping vertical
  extents) for one entity → asserts **two** boxes, and that neither box's height spans the gap
  between the lines.

**Files:** `DocumentRedactionOrchestrator.cs` (logic), `DocumentRedactionOrchestratorTests.cs`
(new test). Commit message: `fix(pdf): emit per-line boxes to stop over-redaction`.

---

## Task 2 — Make the reviewer preview match the actual document (PDF) ⭐ biggest gap vs. the pitch

**Problem.** The selling point is "review PII highlighted **on the document**," but
[frontend/src/components/RedactionReview.tsx](frontend/src/components/RedactionReview.tsx)
only renders Document Intelligence's **flattened extracted text** in a `<pre>`. For a
multi-column/table clinical PDF the preview looks scrambled versus the real page. The backend
already computes per-word `boxes` (see `DetectedEntity.boxes`) and exposes the source file at
`GET /api/document/{jobId}/original`, but the UI never uses either.

**Fix.** For **PDF** jobs, render the real pages with `pdfjs-dist` and overlay the entity
boxes as clickable, toggle-able highlights. For **TXT/DOCX** (no boxes, no page geometry),
keep the existing text-highlight pane unchanged.

Steps:
1. `cd frontend; npm install pdfjs-dist`. Pin the version in `package.json`. Configure the
   worker via the Vite-friendly `import 'pdfjs-dist/build/pdf.worker.min.mjs?url'` pattern (or
   `pdfjs-dist/legacy` if the modern build fights Vite — whichever type-checks cleanly).
2. Decide format from `detection.contentType === 'application/pdf'`.
3. New sub-component `PdfHighlightPreview.tsx` (co-located under `frontend/src/components/`):
   - Props: `detection: DetectionResponse`, `selected: Set<string>`, `onToggle: (id) => void`,
     `categoryColor: (cat: string) => string` (reuse the existing `CATEGORY_COLORS` map — lift
     it into a tiny shared module or export it from `RedactionReview.tsx` rather than
     duplicating).
   - Fetch the PDF bytes from `/api/document/${detection.jobId}/original` (same-origin; the
     Vite dev proxy already forwards `/api`). Load with `pdfjs.getDocument`.
   - For each page: render to a `<canvas>` at a sensible scale (e.g. fit container width).
     Wrap each canvas in a `position: relative` container.
   - Overlay one abs-positioned `<div>` per `box` in every entity where `box.page === pageNumber`.
     Position/size = normalized coords × rendered canvas CSS width/height
     (`left = box.x*width`, `top = box.y*height`, etc.). Border + translucent fill in the
     entity's category color; solid/opaque-ish when `selected.has(entity.id)`, faint outline
     when not. `onClick` → `onToggle(entity.id)`. Add `title` with category + confidence like
     the existing `<mark>` does. Make overlays `cursor: pointer`.
   - Handle multi-page: render all pages stacked vertically. Guard against re-render races
     (cancel the previous `renderTask` / use an AbortController on the fetch; pdf.js render
     tasks must not overlap on the same canvas).
4. In `RedactionReview.tsx`, swap the left `docPane` content: if PDF → `<PdfHighlightPreview .../>`;
   else → the current `<pre>`/`segments` rendering. The right-hand grouped checklist stays for
   **all** formats and remains the source of truth; selection state (`selected` set) is shared,
   so clicking a box or a checkbox stays in sync.
5. Keep everything keyboard-reachable enough for a demo (overlays as `<button>` or with
   `role="button"` + `tabIndex={0}` + Enter handler). Don't over-engineer a11y beyond that.

**Performance/safety for the POC:** cap rendered pages if `pageCount` is large (e.g. render
first 25 pages and show a "showing first 25 of N pages" note) so a huge PDF can't hang the
browser during a demo. Don't build virtualization.

**Acceptance criteria.**
- Upload a multi-column/table PDF → preview shows the **actual rendered page** with colored
  boxes sitting on the real PII, not scrambled text.
- Clicking a box toggles its checkbox in the right list and vice-versa.
- TXT and DOCX still show the existing text-highlight pane (no regression, no pdf.js load).
- `npm run build` is type-clean; no console errors on load.

**Files:** `frontend/src/components/RedactionReview.tsx`, new
`frontend/src/components/PdfHighlightPreview.tsx` (+ `.module.css`), `frontend/package.json`.
If you extract `CATEGORY_COLORS`, add `frontend/src/components/categoryColors.ts`.
Commit message: `feat(review): render real PDF pages with overlay highlights`.

> If `pdfjs-dist` integration with Vite proves genuinely blocked after a reasonable attempt,
> STOP on this task, leave it uncommitted/reverted, and note the blocker in the summary — do
> **not** brute-force it or ship a half-rendering preview. Tasks 1, 3, 4, 5 are independent and
> still valuable.

---

## Task 3 — Fix preview vs. checklist disagreement on overlapping entities

**Problem.** `buildSegments` in
[frontend/src/components/RedactionReview.tsx](frontend/src/components/RedactionReview.tsx)
does `if (entity.offset < cursor) continue;`, silently dropping any entity that overlaps an
earlier one. That entity then has no highlight/click target in the text pane, yet still appears
in the side checklist and still gets redacted — the two panes disagree.

**Fix (text pane only; applies to TXT/DOCX and to any PDF fallback text view):**
1. Sort entities by `offset` asc, then by `length` **desc** (longest first wins the visible span).
2. When an entity overlaps already-consumed text, **clip** its visible segment to the portion
   after `cursor` instead of dropping it entirely; if nothing is left uncovered, still keep it
   associated so a fully-nested entity remains represented. Simplest robust approach for the
   POC: render the outer (longest) entity as the highlight, and for a fully-covered inner
   entity, ensure it's not lost from selection sync — but since the checklist is the source of
   truth and already lists it, the minimum acceptable fix is: **no visible entity silently
   disappears from the text pane unless it is entirely contained within another highlighted
   entity.** Add a short code comment explaining the overlap policy you chose.

Keep it simple — this is a small correctness fix, not a rewrite. Do not change offsets or the
backend.

**Acceptance criteria.**
- Construct/verify a case with two overlapping spans (e.g. "Jane Doe" as Person and "Doe" as a
  nested Person): the text pane no longer drops a partially-overlapping entity; a fully-nested
  entity is at minimum still selectable via the checklist and does not throw.
- No React key warnings; `npm run build` clean.

**Files:** `frontend/src/components/RedactionReview.tsx`.
Commit message: `fix(review): stop dropping overlapping entities from the text preview`.

---

## Task 4 — Remove the fresh-clone PDF demo landmine

**Problem.** [backend/DocumentRedaction.API/appsettings.json](backend/DocumentRedaction.API/appsettings.json)
ships `Azure:DocumentIntelligence:Endpoint` **empty**. The first PDF upload then throws
`Azure:DocumentIntelligence:Endpoint is required for PDF redaction` from
`DocumentLayoutService`'s constructor — the headline format dies at demo time, and the failure
is a raw 500. TXT/DOCX work without it.

**Fix (do all three):**
1. **Fail fast, clearly, at startup** rather than on first PDF. In
   [backend/DocumentRedaction.API/Program.cs](backend/DocumentRedaction.API/Program.cs), after
   config is available, if `Azure:DocumentIntelligence:Endpoint` is empty, log a prominent
   **warning** at startup: `"Azure:DocumentIntelligence:Endpoint is not set — PDF redaction
   will be unavailable; TXT and DOCX still work."` Do NOT throw (keeps TXT/DOCX demos working).
2. **Friendlier runtime error.** In the `Detect` path, when a PDF is uploaded but the endpoint
   is missing, surface a clean `400`-style message ("PDF support requires Azure Document
   Intelligence to be configured — see README") instead of a raw 500. Easiest: have
   `DocumentLayoutService` throw a specific exception type (e.g. reuse `ArgumentException` /
   a small `ConfigurationMissingException`) that the controller already maps to `BadRequest`,
   OR check in the orchestrator before calling the PDF processor. Keep it minimal.
3. **README call-out.** In [README.md](README.md) near the Document Intelligence setup section,
   add a short note: PDF redaction requires `Azure:DocumentIntelligence:Endpoint`; without it,
   only TXT and DOCX work.

**Acceptance criteria.**
- Start the API with an empty DocIntel endpoint → clear startup warning, app still runs,
  TXT/DOCX detect works.
- Upload a PDF with no endpoint configured → clean, human-readable error in the UI (not a raw
  stack trace / 500).
- README documents the requirement.

**Files:** `Program.cs`, `DocumentLayoutService.cs` and/or `DocumentRedactionOrchestrator.cs`,
`README.md`. Commit message: `fix(config): fail clearly when DocIntel endpoint is unset`.

> Note: `appsettings.json` also commits environment-specific endpoints + a real TenantId.
> Leave them for this POC (do not refactor to user-secrets) unless trivially safe — just make
> sure the empty DocIntel value no longer produces an ugly crash.

---

## Task 5 — Document the DOCX extraction scope (headers/footers)

**Problem.** `BuildText` in
[backend/DocumentRedaction.API/Services/DocxDocumentProcessor.cs](backend/DocumentRedaction.API/Services/DocxDocumentProcessor.cs)
only walks `<Paragraph>` descendants of the document **body**. PII in headers, footers, text
boxes, comments, and footnotes/endnotes is neither detected nor redacted. For a POC this is
acceptable **only if it's stated** — the risk is a stakeholder assuming "it caught everything."

**Fix (required):** Add a one-line caveat to [README.md](README.md) under the "Documents
supported" / DOCX area: *"DOCX redaction covers body text only in this POC; headers, footers,
text boxes, comments, and footnotes are not yet scanned."*

**Optional stretch (only if Tasks 1–4 are done, building green, and time remains):** extend
`BuildText` to also concatenate header/footer parts (`doc.MainDocumentPart.HeaderParts` /
`FooterParts`) into the same deterministic text stream so their PII is detected and masked.
This must preserve the invariant that extraction and redaction build **byte-for-byte identical
text** (offsets must line up on both passes) — if you can't guarantee that, do the README note
only and leave extraction as-is.

**Acceptance criteria.**
- README states the DOCX body-only limitation.
- If the stretch is attempted: a DOCX with a name in the header gets that name masked, and all
  existing DOCX tests in
  [backend/DocumentRedaction.Tests/DocxDocumentProcessorTests.cs](backend/DocumentRedaction.Tests/DocxDocumentProcessorTests.cs)
  still pass. If any existing test breaks, revert the stretch and keep the README note.

**Files:** `README.md` (required), `DocxDocumentProcessor.cs` (+ tests) if stretch attempted.
Commit message: `docs(docx): note body-only extraction scope` (or `feat(docx): scan headers/footers`).

---

## Out of scope — do NOT do these (POC)

- Authentication / authorization / `[Authorize]` / ownership checks on jobs.
- Blob retention, TTL, or cleanup jobs; the UI's "no content retained" copy can stay.
- Removing the dead SAS methods in `BlobStorageService`.
- Hardening error-detail leakage, hardcoded `"en"` language, CORS tightening.
- Docker Compose credential wiring, `version:` key, broad new test suites.
- Moving committed endpoints/TenantId out of `appsettings.json`.

If you spot something else genuinely broken (throws, won't build, wrong output), note it in the
final summary rather than fixing it — keep this change set to the five tasks above.

---

## Final deliverable / summary to report back

When done, report:
1. Which tasks were completed, skipped, or blocked (and why for any skip/block).
2. `dotnet build`, `dotnet test`, and `npm run build` results (all should be green).
3. The list of commits made (one per task).
4. Any new dependency added (`pdfjs-dist` version) and anything a reviewer must configure to
   demo (e.g. the DocIntel endpoint for PDF).
5. Anything you deliberately left alone that a human should look at later.
