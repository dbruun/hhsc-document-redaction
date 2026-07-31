# Plan: Interactive "select-then-redact" with on-document highlights + redacted PDF

Today the app does one-shot automatic redaction: Azure's native-document PII endpoint masks *everything* and never returns the full document text — so it can't support previewing, selecting, or redacting only chosen items. The fix is to split into a **detect** phase and an **apply** phase, unify every input to PDF (so highlights overlay the rendered page and the output is always a redacted PDF), and switch to Azure services that give us text + coordinates + offsets.

## How it works (new pipeline)

1. Convert input to PDF (PDF as-is; DOCX/TXT → PDF).
2. Azure **Document Intelligence** (prebuilt-layout) → full text + per-word bounding boxes + page sizes.
3. Azure **Language text PII** on that text → entities with character offsets + category + confidence.
4. Map each entity's offsets → the words' boxes → normalized rectangles per page.
5. Persist job state (source PDF + `detection.json`) in blob; return everything to the UI.
6. UI renders the PDF with clickable highlight boxes (all pre-selected); user deselects what to keep.
7. Apply: rasterize pages, burn opaque bars over only the selected boxes, flatten to an image-PDF (guarantees the text underneath is gone) → redacted PDF.

## Steps

1. **Provisioning/config** — Add an Azure Document Intelligence resource + `Azure:DocumentIntelligence:Endpoint`; grant the app's identity `Cognitive Services User` on both Language and Document Intelligence.
2. **Backend – PDF conversion** — New `IPdfConversionService` (DOCX/TXT → PDF) via Gotenberg container *(recommended)* or LibreOffice headless.
3. **Backend – detect** *(depends on 2)* — New `DocumentLayoutService` (Document Intelligence), `TextPiiDetectionService` (Language text PII, reusing the existing bearer-token pattern in `DocumentPiiRedactionService.cs`), and `EntityBoxMapper` (offset→box). Persist `detection.json` + source PDF via `IBlobStorageService.cs`.
4. **Backend – apply** *(depends on 3)* — New `IPdfRedactionService`: rasterize (PDFium/PDFtoImage + SkiaSharp), draw bars over selected boxes, emit flattened PDF.
5. **Backend – orchestrator + controller** *(depends on 3,4)* — Rework `DocumentRedactionOrchestrator.cs` to `DetectAsync`/`ApplyAsync`; in `DocumentController.cs` replace `POST /redact` with `POST /detect`, add `POST /{jobId}/apply` and `GET /{jobId}/source-pdf`, keep `GET /{jobId}/redacted`. New models in `RedactionModels.cs`.
6. **Frontend – review UI** *(parallel with 3–5)* — Add `pdfjs-dist`; new `RedactionReview` component (canvas + toggle-able overlay boxes + category-grouped checklist reusing `CATEGORY_COLORS` from `RedactionResult.tsx`); extend `api.ts`/`types` with `detectDocument`/`applyRedactions`; add `reviewing`/`applying` states in `App.tsx` and point `DocumentUpload.tsx` at detect.
7. **Docker/docs/verify** — Add Gotenberg (or LibreOffice) + Document Intelligence env to `docker-compose.yml`, `.env.example`, README.

## Relevant files

- `backend/DocumentRedaction.API/Services/DocumentPiiRedactionService.cs` — reuse Entra bearer-token/polling pattern; repurpose for text PII.
- `backend/DocumentRedaction.API/Services/DocumentRedactionOrchestrator.cs` — split into detect/apply.
- `backend/DocumentRedaction.API/Controllers/DocumentController.cs` — new endpoints.
- `frontend/src/components/RedactionResult.tsx` / `frontend/src/components/DocumentViewer.tsx` — reuse colors; replace viewer with pdf.js overlay.
- `frontend/src/App.tsx` — new state machine.
- `backend/DocumentRedaction.API/Program.cs` — register new services + DI config.
- `backend/DocumentRedaction.API/Dockerfile` / `docker-compose.yml` — conversion tooling + DI config.

## Detailed backend changes (DocumentRedaction.API)

- NEW `PdfConversionService`: DOCX/TXT → PDF (LibreOffice `soffice --headless --convert-to pdf` OR Gotenberg HTTP). Interface `IPdfConversionService`.
- NEW `DocumentLayoutService`: Azure Document Intelligence prebuilt-layout → text + word polygons + page dims. Config `Azure:DocumentIntelligence:Endpoint`. Entra role: Cognitive Services User.
- NEW `TextPiiDetectionService`: Language text PII on DI content → entities w/ offsets. (Reuse bearer-token pattern from `DocumentPiiRedactionService`; scope `cognitiveservices.azure.com/.default`.)
- NEW `EntityBoxMapper`: entity span → normalized page boxes via DI word spans.
- NEW `PdfRedactionService`: rasterize+burn+flatten → redacted PDF. Interface `IPdfRedactionService`.
- Job state persistence in blob: `detection.json` read/write helpers on `IBlobStorageService`.
- Rework `DocumentRedactionOrchestrator` + `IDocumentRedactionOrchestrator`: `DetectAsync(file)` and `ApplyAsync(jobId, selectedIds)`. Keep OpenOriginal/OpenRedacted; add OpenSourcePdf.
- Controller `DocumentController`:
  - POST /api/document/detect  (replaces /redact) → DetectionResponse
  - POST /api/document/{jobId}/apply { selectedEntityIds[] } → ApplyResponse (redacted url)
  - GET /api/document/{jobId}/source-pdf  (for pdf.js)
  - GET /api/document/{jobId}/redacted (keep)
- Models: `DetectionResponse { jobId, fileName, fileSizeBytes, pageCount, pages[]{width,height}, entities[] }`, `DetectedEntity { id, category, subCategory, text, confidence, offset, length, boxes[]{page,x,y,w,h} }`, `ApplyRequest { selectedEntityIds[] }`, `ApplyResponse { jobId, redactedUrl, redactedCount }`.
- Program.cs: register new services + DI credential (already shared DefaultAzureCredential), add DI endpoint config, add HttpClients.

## Dependencies to add

- Backend: `Azure.AI.DocumentIntelligence` (or REST), `PDFtoImage` (PDFium) + `SkiaSharp` (raster+draw+emit PDF). DOCX/TXT→PDF via LibreOffice in Docker OR Gotenberg service in docker-compose.
- Frontend: `pdfjs-dist`.

## Detailed frontend changes

- `api.ts`: `detectDocument(file)` → DetectionResponse; `applyRedactions(jobId, ids)` → ApplyResponse.
- `types`: DetectionResponse, DetectedEntity, ApplyResponse; UploadStatus gains 'reviewing' and 'applying'.
- NEW `RedactionReview` component: pdf.js renders source-pdf pages to canvas; overlay abs-positioned toggle boxes from normalized coords (category colors, all selected by default); side list grouped by category w/ checkboxes synced to boxes; "Apply redactions" → applyRedactions → success.
- `App.tsx` state machine: idle → processing(detect) → reviewing → applying → success(redacted PDF via existing RedactionResult download, or a simple done screen). Update intro copy (no longer "automatically strip all PII").
- `DocumentUpload.onStatusChange` calls detect instead of redact.
- Reuse CATEGORY_COLORS from RedactionResult.

## Docker/compose

- Backend image: add LibreOffice OR add Gotenberg service to docker-compose (recommended) and call it over HTTP.
- Add Document Intelligence env vars to docker-compose + .env.example + README.

## Verification

1. `dotnet build` clean.
2. Detect a TXT/DOCX/PDF → highlight boxes line up over the rendered pages.
3. Deselect a subset → apply → redacted PDF masks only selected spans; copy-paste under a bar yields nothing (text truly removed).
4. No-PII doc → review shows none; apply produces a clean flattened PDF.
5. Roles confirmed: existing Storage roles + `Cognitive Services User` on Language and Document Intelligence.
6. az login tenant d64bea8b-... still valid.

## Decisions

- Redacted **PDF for all inputs**; highlights **overlaid on the rendered doc**; **all pre-selected**; character-mask style rendered as an **opaque bar** (true asterisk reflow isn't possible on a rasterized PDF).
- Blob-based job state (no database), consistent with current design.

## Further considerations

1. DOCX/TXT→PDF tooling: **Gotenberg container** (clean, recommended) vs **LibreOffice in the image** (heavier, fewer moving parts). Which do you prefer?
2. "Character mask" on a rasterized PDF: **opaque bar** (recommended, guarantees removal) vs **bar stamped with asterisks** for style.
3. Scanned/image PDFs: Document Intelligence will OCR them and burn still works — OK to rely on that?
