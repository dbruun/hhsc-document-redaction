import { useEffect, useRef, useState } from 'react';
import * as pdfjsLib from 'pdfjs-dist';
import pdfjsWorkerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';
import type { DetectedEntity, DetectionResponse, LayoutWordInfo } from '../types';
import styles from './PdfHighlightPreview.module.css';

pdfjsLib.GlobalWorkerOptions.workerSrc = pdfjsWorkerUrl;

const DEFAULT_ZOOM = 150;
const MIN_ZOOM = 75;
const MAX_ZOOM = 250;
const ZOOM_STEP = 25;

// ─── Single rendered page with entity overlays ────────────────────────────────

interface PageCanvasProps {
  pageProxy: pdfjsLib.PDFPageProxy;
  pageNumber: number;
  entities: DetectedEntity[];
  selected: Set<string>;
  onToggle: (id: string) => void;
  categoryColor: (cat: string) => string;
  zoomPercent: number;
  words: LayoutWordInfo[];
  extractedText: string;
  blacklist: string[];
  onToggleBlacklistTerm: (term: string) => void;
  shouldRender: boolean;
}

function PdfPageCanvas({
  pageProxy,
  pageNumber,
  entities,
  selected,
  onToggle,
  categoryColor,
  zoomPercent,
  words,
  extractedText,
  blacklist,
  onToggleBlacklistTerm,
  shouldRender,
}: PageCanvasProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const wrapperRef = useRef<HTMLDivElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const renderTaskRef = useRef<pdfjsLib.RenderTask | null>(null);
  const [canvasSize, setCanvasSize] = useState<{ width: number; height: number } | null>(null);
  const [availableWidth, setAvailableWidth] = useState(0);

  useEffect(() => {
    const wrapper = wrapperRef.current;
    if (!wrapper) return;

    const updateWidth = () => setAvailableWidth(wrapper.clientWidth);
    updateWidth();
    const resizeObserver = new ResizeObserver(updateWidth);
    resizeObserver.observe(wrapper);
    return () => resizeObserver.disconnect();
  }, []);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || availableWidth === 0) return;

    // Cancel any in-progress render before starting a new one to avoid overlapping tasks.
    if (renderTaskRef.current) {
      renderTaskRef.current.cancel();
      renderTaskRef.current = null;
    }

    const viewport = pageProxy.getViewport({ scale: 1 });
    const scale = (availableWidth / viewport.width) * (zoomPercent / 100);
    const scaledViewport = pageProxy.getViewport({ scale });

    setCanvasSize({ width: scaledViewport.width, height: scaledViewport.height });

    if (!shouldRender) {
      canvas.width = 1;
      canvas.height = 1;
      canvas.style.width = '1px';
      canvas.style.height = '1px';
      return;
    }

    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.floor(scaledViewport.width * dpr);
    canvas.height = Math.floor(scaledViewport.height * dpr);
    canvas.style.width = `${scaledViewport.width}px`;
    canvas.style.height = `${scaledViewport.height}px`;

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
  }, [pageProxy, pageNumber, availableWidth, shouldRender, zoomPercent]);

  const pageEntities = entities.filter((e) => e.boxes.some((b) => b.page === pageNumber));
  const pageWords = words.filter((word) => word.box.page === pageNumber);

  return (
    <div ref={wrapperRef} className={styles.pageWrapper} data-page-number={pageNumber}>
      <div ref={containerRef} className={styles.canvasContainer} style={canvasSize ? { width: canvasSize.width, height: canvasSize.height } : {}}>
        <canvas ref={canvasRef} className={styles.canvas} />
        {canvasSize && shouldRender && pageWords.map((word) => {
          const term = extractedText.slice(word.offset, word.offset + word.length).trim();
          if (!term) return null;
          const isBlacklisted = blacklist.some(
            (item) => item.toLocaleLowerCase() === term.toLocaleLowerCase(),
          );
          return (
            <button
              key={`word-${word.offset}-${word.length}`}
              type="button"
              className={`${styles.wordOverlay} ${isBlacklisted ? styles.wordOverlaySelected : ''}`}
              style={{
                left: `${word.box.x * 100}%`,
                top: `${word.box.y * 100}%`,
                width: `${word.box.width * 100}%`,
                height: `${word.box.height * 100}%`,
              }}
              aria-pressed={isBlacklisted}
              aria-label={`${isBlacklisted ? 'Remove' : 'Redact'} ${term} everywhere`}
              title={`${isBlacklisted ? 'Remove' : 'Redact'} “${term}” everywhere`}
              onClick={() => onToggleBlacklistTerm(term)}
            />
          );
        })}
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
  entities: DetectedEntity[];
  selected: Set<string>;
  onToggle: (id: string) => void;
  categoryColor: (cat: string) => string;
  blacklist: string[];
  onToggleBlacklistTerm: (term: string) => void;
}

export default function PdfHighlightPreview({
  detection,
  entities,
  selected,
  blacklist,
  onToggle,
  onToggleBlacklistTerm,
  categoryColor,
}: Props) {
  const [pages, setPages] = useState<pdfjsLib.PDFPageProxy[]>([]);
  const [pagesToRender, setPagesToRender] = useState<Set<number>>(new Set([1, 2, 3]));
  const [zoomPercent, setZoomPercent] = useState(DEFAULT_ZOOM);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const abortRef = useRef<AbortController | null>(null);

  const updatePageWindow = (container: HTMLDivElement) => {
    const containerBounds = container.getBoundingClientRect();
    const renderMargin = 1200;
    const next = new Set<number>();
    for (const element of container.querySelectorAll<HTMLElement>('[data-page-number]')) {
      const pageBounds = element.getBoundingClientRect();
      if (
        pageBounds.bottom >= containerBounds.top - renderMargin
        && pageBounds.top <= containerBounds.bottom + renderMargin
      ) {
        next.add(Number(element.dataset.pageNumber));
      }
    }
    setPagesToRender((current) => {
      if (current.size === next.size && [...current].every((page) => next.has(page))) return current;
      return next;
    });
  };

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

        const pageProxies: pdfjsLib.PDFPageProxy[] = [];
        for (let i = 1; i <= doc.numPages; i++) {
          pageProxies.push(await doc.getPage(i));
          if (abort.signal.aborted) {
            await doc.cleanup();
            return;
          }
        }

        setPages(pageProxies);
        setPagesToRender(new Set([1, 2, 3].filter((page) => page <= doc.numPages)));
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
    <div className={styles.pdfContainer} onScroll={(event) => updatePageWindow(event.currentTarget)}>
      <div className={styles.pdfToolbar}>
        <span>{pages.length} page{pages.length === 1 ? '' : 's'}</span>
        <div className={styles.zoomControls} aria-label="PDF zoom controls">
          <button
            type="button"
            onClick={() => setZoomPercent((zoom) => Math.max(MIN_ZOOM, zoom - ZOOM_STEP))}
            disabled={zoomPercent === MIN_ZOOM}
            aria-label="Zoom out"
            title="Zoom out"
          >
            −
          </button>
          <button
            type="button"
            className={styles.zoomValue}
            onClick={() => setZoomPercent(DEFAULT_ZOOM)}
            title="Reset zoom"
          >
            {zoomPercent}%
          </button>
          <button
            type="button"
            onClick={() => setZoomPercent((zoom) => Math.min(MAX_ZOOM, zoom + ZOOM_STEP))}
            disabled={zoomPercent === MAX_ZOOM}
            aria-label="Zoom in"
            title="Zoom in"
          >
            +
          </button>
        </div>
      </div>
      {pages.map((pageProxy, i) => (
        <PdfPageCanvas
          key={i}
          pageProxy={pageProxy}
          pageNumber={i + 1}
          entities={entities}
          selected={selected}
          onToggle={onToggle}
          categoryColor={categoryColor}
          zoomPercent={zoomPercent}
          words={detection.words ?? []}
          extractedText={detection.extractedText}
          blacklist={blacklist}
          onToggleBlacklistTerm={onToggleBlacklistTerm}
          shouldRender={pagesToRender.has(i + 1)}
        />
      ))}
    </div>
  );
}
