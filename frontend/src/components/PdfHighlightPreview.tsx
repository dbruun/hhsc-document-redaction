import { useEffect, useRef, useState } from 'react';
import * as pdfjsLib from 'pdfjs-dist';
import pdfjsWorkerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';
import type { DetectedEntity, DetectionResponse, LayoutWordInfo } from '../types';
import styles from './PdfHighlightPreview.module.css';

pdfjsLib.GlobalWorkerOptions.workerSrc = pdfjsWorkerUrl;

/** Maximum pages to render to avoid hanging the browser on a huge PDF during a demo. */
const MAX_PAGES = 25;

/** A reviewer-added redaction box (a word the models missed), keyed for removal. */
export interface ManualBox {
  id: string;
  page: number;
  x: number;
  y: number;
  width: number;
  height: number;
}

// ─── Single rendered page with entity overlays ────────────────────────────────

interface PageCanvasProps {
  pageProxy: pdfjsLib.PDFPageProxy;
  pageNumber: number;
  renderWidth: number;
  entities: DetectedEntity[];
  words: LayoutWordInfo[];
  manualBoxes: ManualBox[];
  selected: Set<string>;
  onToggle: (id: string) => void;
  onAddManual: (box: { page: number; x: number; y: number; width: number; height: number }) => void;
  onRemoveManual: (id: string) => void;
  categoryColor: (cat: string) => string;
}

function PdfPageCanvas({
  pageProxy,
  pageNumber,
  renderWidth,
  entities,
  words,
  manualBoxes,
  selected,
  onToggle,
  onAddManual,
  onRemoveManual,
  categoryColor,
}: PageCanvasProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const renderTaskRef = useRef<pdfjsLib.RenderTask | null>(null);
  const [canvasSize, setCanvasSize] = useState<{ width: number; height: number } | null>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || renderWidth <= 0) return;

    // Cancel any in-progress render before starting a new one to avoid overlapping tasks.
    if (renderTaskRef.current) {
      renderTaskRef.current.cancel();
      renderTaskRef.current = null;
    }

    // Scale the page to fill the available width provided by the parent.
    const baseViewport = pageProxy.getViewport({ scale: 1 });
    const scale = renderWidth / baseViewport.width;
    const scaledViewport = pageProxy.getViewport({ scale });

    // HiDPI: enlarge the backing store by the device pixel ratio for crisp text, and apply a
    // matching transform so pdfjs draws into the FULL backing store (not just the top-left
    // corner). Without this transform the page fills only 1/dpr of the canvas, which throws the
    // percentage-positioned entity overlays out of alignment on Retina / 2x displays.
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.floor(scaledViewport.width * dpr);
    canvas.height = Math.floor(scaledViewport.height * dpr);
    canvas.style.width = `${scaledViewport.width}px`;
    canvas.style.height = `${scaledViewport.height}px`;

    setCanvasSize({ width: scaledViewport.width, height: scaledViewport.height });

    const renderTask = pageProxy.render({
      canvas,
      viewport: scaledViewport,
      transform: dpr !== 1 ? [dpr, 0, 0, dpr, 0, 0] : undefined,
    });
    renderTaskRef.current = renderTask;

    renderTask.promise.catch((err: unknown) => {
      if ((err as { name?: string })?.name !== 'RenderingCancelledException') {
        console.error(`PDF page ${pageNumber} render error:`, err);
      }
    });

    return () => {
      if (renderTaskRef.current) {
        renderTaskRef.current.cancel();
        renderTaskRef.current = null;
      }
    };
  }, [pageProxy, pageNumber, renderWidth]);

  const pageEntities = entities.filter((e) => e.boxes.some((b) => b.page === pageNumber));
  const pageWords = words.filter((w) => w.page === pageNumber);
  const pageManual = manualBoxes.filter((m) => m.page === pageNumber);

  // Click on empty document area → find the word under the cursor and add it as a manual
  // redaction. Clicks on an existing entity/manual overlay stop propagation, so they never
  // reach here (they toggle / remove instead).
  const handlePageClick = (e: React.MouseEvent<HTMLDivElement>) => {
    if (!canvasSize) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const nx = (e.clientX - rect.left) / rect.width;
    const ny = (e.clientY - rect.top) / rect.height;
    const hit = pageWords.find(
      (w) => nx >= w.x && nx <= w.x + w.width && ny >= w.y && ny <= w.y + w.height,
    );
    if (hit) onAddManual({ page: pageNumber, x: hit.x, y: hit.y, width: hit.width, height: hit.height });
  };

  return (
    <div className={styles.pageWrapper}>
      <div
        className={styles.canvasContainer}
        style={canvasSize ? { width: canvasSize.width, height: canvasSize.height } : {}}
        onClick={handlePageClick}
      >
        <canvas ref={canvasRef} className={styles.canvas} />
        {canvasSize &&
          pageEntities.flatMap((entity) =>
            entity.boxes
              .filter((b) => b.page === pageNumber)
              .map((box, boxIdx) => {
                const isSelected = selected.has(entity.id);
                const color = categoryColor(entity.category);
                return (
                  <button
                    key={`${entity.id}-box${boxIdx}`}
                    className={styles.overlay}
                    style={{
                      left: `${box.x * 100}%`,
                      top: `${box.y * 100}%`,
                      width: `${box.width * 100}%`,
                      height: `${box.height * 100}%`,
                      borderColor: color,
                      backgroundColor: isSelected ? `${color}44` : `${color}11`,
                    }}
                    title={`${entity.category}${entity.subCategory ? ' · ' + entity.subCategory : ''} — ${(entity.confidenceScore * 100).toFixed(0)}%`}
                    onClick={(e) => {
                      e.stopPropagation();
                      onToggle(entity.id);
                    }}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        onToggle(entity.id);
                      }
                    }}
                  />
                );
              }),
          )}
        {canvasSize &&
          pageManual.map((m) => (
            <button
              key={m.id}
              className={styles.manualOverlay}
              style={{
                left: `${m.x * 100}%`,
                top: `${m.y * 100}%`,
                width: `${m.width * 100}%`,
                height: `${m.height * 100}%`,
              }}
              title="Manual redaction — click to remove"
              onClick={(e) => {
                e.stopPropagation();
                onRemoveManual(m.id);
              }}
            />
          ))}
      </div>
    </div>
  );
}

// ─── Main component ────────────────────────────────────────────────────────────

interface Props {
  detection: DetectionResponse;
  selected: Set<string>;
  manualBoxes: ManualBox[];
  onToggle: (id: string) => void;
  onAddManual: (box: { page: number; x: number; y: number; width: number; height: number }) => void;
  onRemoveManual: (id: string) => void;
  categoryColor: (cat: string) => string;
}

export default function PdfHighlightPreview({
  detection,
  selected,
  manualBoxes,
  onToggle,
  onAddManual,
  onRemoveManual,
  categoryColor,
}: Props) {
  const [pages, setPages] = useState<pdfjsLib.PDFPageProxy[]>([]);
  const [totalPages, setTotalPages] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [renderWidth, setRenderWidth] = useState(0);
  const containerRef = useRef<HTMLDivElement>(null);
  const abortRef = useRef<AbortController | null>(null);

  // Measure the available width from the scrolling container (a stable full-width element) and
  // keep it in sync on resize. Every page renders to this width, so the preview fills the pane
  // instead of collapsing to the canvas's default 300px intrinsic size.
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;

    const measure = () => {
      const styleWidth = el.clientWidth; // excludes vertical scrollbar
      const cs = window.getComputedStyle(el);
      const padding = parseFloat(cs.paddingLeft) + parseFloat(cs.paddingRight);
      const available = Math.max(0, styleWidth - padding);
      if (available > 0) setRenderWidth(available);
    };

    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, [loading]);

  useEffect(() => {
    abortRef.current?.abort();
    const abort = new AbortController();
    abortRef.current = abort;
    setLoading(true);
    setError(null);
    setPages([]);

    async function loadPdf() {
      try {
        const resp = await fetch(`/api/document/${detection.jobId}/original`, { signal: abort.signal });
        if (!resp.ok) throw new Error(`Could not load PDF (${resp.status})`);
        const buf = await resp.arrayBuffer();
        if (abort.signal.aborted) return;

        const loadingTask = pdfjsLib.getDocument({ data: buf });
        const doc = await loadingTask.promise;
        if (abort.signal.aborted) {
          await doc.cleanup();
          return;
        }

        setTotalPages(doc.numPages);
        const pageCount = Math.min(doc.numPages, MAX_PAGES);
        const pageProxies: pdfjsLib.PDFPageProxy[] = [];
        for (let i = 1; i <= pageCount; i++) {
          pageProxies.push(await doc.getPage(i));
          if (abort.signal.aborted) {
            await doc.cleanup();
            return;
          }
        }

        setPages(pageProxies);
        setLoading(false);
      } catch (err: unknown) {
        if ((err as { name?: string })?.name === 'AbortError') return;
        setError(err instanceof Error ? err.message : String(err));
        setLoading(false);
      }
    }

    void loadPdf();
    return () => {
      abort.abort();
    };
  }, [detection.jobId]);

  if (loading) return <div className={styles.status}>Loading PDF…</div>;
  if (error) return <div className={styles.status}>⚠ Could not render PDF: {error}</div>;

  return (
    <div className={styles.pdfContainer} ref={containerRef}>
      {totalPages > MAX_PAGES && (
        <div className={styles.truncationNote}>
          Showing first {MAX_PAGES} of {totalPages} pages.
        </div>
      )}
      {pages.map((pageProxy, i) => (
        <PdfPageCanvas
          key={i}
          pageProxy={pageProxy}
          pageNumber={i + 1}
          renderWidth={renderWidth}
          entities={detection.entities}
          words={detection.words ?? []}
          manualBoxes={manualBoxes}
          selected={selected}
          onToggle={onToggle}
          onAddManual={onAddManual}
          onRemoveManual={onRemoveManual}
          categoryColor={categoryColor}
        />
      ))}
    </div>
  );
}
