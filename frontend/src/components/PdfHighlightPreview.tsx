import { useEffect, useRef, useState } from 'react';
import * as pdfjsLib from 'pdfjs-dist';
import pdfjsWorkerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';
import type { DetectedEntity, DetectionResponse } from '../types';
import styles from './PdfHighlightPreview.module.css';

pdfjsLib.GlobalWorkerOptions.workerSrc = pdfjsWorkerUrl;

/** Maximum pages to render to avoid hanging the browser on a huge PDF during a demo. */
const MAX_PAGES = 25;

// ─── Single rendered page with entity overlays ────────────────────────────────

interface PageCanvasProps {
  pageProxy: pdfjsLib.PDFPageProxy;
  pageNumber: number;
  entities: DetectedEntity[];
  selected: Set<string>;
  onToggle: (id: string) => void;
  categoryColor: (cat: string) => string;
}

function PdfPageCanvas({ pageProxy, pageNumber, entities, selected, onToggle, categoryColor }: PageCanvasProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const renderTaskRef = useRef<pdfjsLib.RenderTask | null>(null);
  const [canvasSize, setCanvasSize] = useState<{ width: number; height: number } | null>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    // Cancel any in-progress render before starting a new one to avoid overlapping tasks.
    if (renderTaskRef.current) {
      renderTaskRef.current.cancel();
      renderTaskRef.current = null;
    }

    const containerWidth = containerRef.current?.clientWidth ?? canvas.parentElement?.clientWidth ?? 800;
    const viewport = pageProxy.getViewport({ scale: 1 });
    const scale = containerWidth / viewport.width;
    const scaledViewport = pageProxy.getViewport({ scale });

    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.floor(scaledViewport.width * dpr);
    canvas.height = Math.floor(scaledViewport.height * dpr);
    canvas.style.width = `${scaledViewport.width}px`;
    canvas.style.height = `${scaledViewport.height}px`;

    setCanvasSize({ width: scaledViewport.width, height: scaledViewport.height });

    // pdfjs-dist v4+ render API: pass the canvas element directly.
    // The scale transform for HiDPI is applied via canvas CSS vs backing store sizes.
    const renderTask = pageProxy.render({ canvas, viewport: scaledViewport });
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
  }, [pageProxy, pageNumber]);

  const pageEntities = entities.filter((e) => e.boxes.some((b) => b.page === pageNumber));

  return (
    <div className={styles.pageWrapper}>
      <div ref={containerRef} className={styles.canvasContainer} style={canvasSize ? { width: canvasSize.width, height: canvasSize.height } : {}}>
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
                    onClick={() => onToggle(entity.id)}
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
      </div>
    </div>
  );
}

// ─── Main component ────────────────────────────────────────────────────────────

interface Props {
  detection: DetectionResponse;
  selected: Set<string>;
  onToggle: (id: string) => void;
  categoryColor: (cat: string) => string;
}

export default function PdfHighlightPreview({ detection, selected, onToggle, categoryColor }: Props) {
  const [pages, setPages] = useState<pdfjsLib.PDFPageProxy[]>([]);
  const [totalPages, setTotalPages] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const abortRef = useRef<AbortController | null>(null);

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
    <div className={styles.pdfContainer}>
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
          entities={detection.entities}
          selected={selected}
          onToggle={onToggle}
          categoryColor={categoryColor}
        />
      ))}
    </div>
  );
}
