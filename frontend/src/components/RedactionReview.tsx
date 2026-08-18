import { useMemo, useState } from 'react';
import type { DetectionResponse, DetectedEntity, DetectedBox } from '../types';
import styles from './RedactionReview.module.css';
import { categoryColor } from './categoryColors';
import PdfHighlightPreview, { type ManualBox } from './PdfHighlightPreview';

interface Segment {
  text: string;
  entity?: DetectedEntity;
}

/** Splits the extracted text into plain and entity segments.
 *
 * Overlap policy: entities are sorted longest-first within the same offset so that outer
 * (longer) spans win the visible highlight. A partially-overlapping entity is clipped to the
 * portion of its span that has not already been covered. A fully-nested entity (its entire
 * span already consumed) is skipped from the text pane but still appears in the side checklist
 * and is still redacted — the checklist is the source of truth.
 */
function buildSegments(text: string, entities: DetectedEntity[]): Segment[] {
  // Sort: ascending offset, then descending length (longest-first wins on ties).
  const ordered = [...entities].sort((a, b) =>
    a.offset !== b.offset ? a.offset - b.offset : b.length - a.length,
  );
  const segments: Segment[] = [];
  let cursor = 0;

  for (const entity of ordered) {
    const entityEnd = entity.offset + entity.length;
    // Clip the visible start to wherever we are (cursor).
    const visibleStart = Math.max(entity.offset, cursor);
    // If the entity is entirely consumed, skip its text pane appearance.
    if (visibleStart >= entityEnd) continue;
    // Plain text before this entity's visible portion.
    if (visibleStart > cursor) segments.push({ text: text.slice(cursor, visibleStart) });
    segments.push({ text: text.slice(visibleStart, entityEnd), entity });
    cursor = entityEnd;
  }
  if (cursor < text.length) segments.push({ text: text.slice(cursor) });

  return segments;
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

interface RedactionReviewProps {
  detection: DetectionResponse;
  busy: boolean;
  onApply: (selectedIds: string[], manualBoxes: DetectedBox[]) => void;
  onCancel: () => void;
}

/** Detections below this confidence (percent) are hidden by default to cut false positives. */
const DEFAULT_MIN_CONFIDENCE = 75;

export default function RedactionReview({ detection, busy, onApply, onCancel }: RedactionReviewProps) {
  // Confidence filter (percent). Detections below the threshold are hidden entirely from the
  // preview, checklist, and counts — a quick way to suppress low-confidence false positives.
  const [minConfidence, setMinConfidence] = useState(DEFAULT_MIN_CONFIDENCE);

  // Entities at or above the current threshold. Everything downstream (preview, checklist,
  // selection, counts) works off this filtered set.
  const visibleEntities = useMemo(
    () => detection.entities.filter((e) => e.confidenceScore * 100 >= minConfidence),
    [detection.entities, minConfidence],
  );

  const [selected, setSelected] = useState<Set<string>>(
    () =>
      new Set(
        detection.entities
          .filter((e) => e.confidenceScore * 100 >= DEFAULT_MIN_CONFIDENCE)
          .map((e) => e.id),
      ),
  );
  const [manualBoxes, setManualBoxes] = useState<ManualBox[]>([]);

  const toggle = (id: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const selectAll = () => setSelected(new Set(visibleEntities.map((e) => e.id)));
  const clearAll = () => setSelected(new Set());

  // Select or deselect every visible instance in a category at once (e.g. clear all DateTime).
  const toggleCategory = (ids: string[], select: boolean) =>
    setSelected((prev) => {
      const next = new Set(prev);
      for (const id of ids) {
        if (select) next.add(id);
        else next.delete(id);
      }
      return next;
    });

  // Move the confidence threshold: hide detections below it and (re)select everything now
  // visible so the reviewer immediately sees exactly what will be redacted.
  const applyConfidenceFilter = (pct: number) => {
    setMinConfidence(pct);
    setSelected(new Set(detection.entities.filter((e) => e.confidenceScore * 100 >= pct).map((e) => e.id)));
  };

  const addManual = (box: { page: number; x: number; y: number; width: number; height: number }) =>
    setManualBoxes((prev) => {
      // Ignore a duplicate click on the same word.
      if (prev.some((m) => m.page === box.page && m.x === box.x && m.y === box.y)) return prev;
      const id = `m-${box.page}-${Math.round(box.x * 10000)}-${Math.round(box.y * 10000)}`;
      return [...prev, { id, ...box }];
    });

  const removeManual = (id: string) => setManualBoxes((prev) => prev.filter((m) => m.id !== id));

  // Categories the reviewer has expanded in the checklist. Empty by default (all collapsed).
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const toggleCollapsed = (category: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(category)) next.delete(category);
      else next.add(category);
      return next;
    });

  const handleApply = () =>
    onApply(
      [...selected],
      manualBoxes.map(({ page, x, y, width, height }) => ({ page, x, y, width, height })),
    );

  const segments = useMemo(
    () => buildSegments(detection.extractedText, visibleEntities),
    [detection.extractedText, visibleEntities],
  );

  const grouped = useMemo(() => {
    const map = new Map<string, DetectedEntity[]>();
    for (const e of visibleEntities) {
      const list = map.get(e.category) ?? [];
      list.push(e);
      map.set(e.category, list);
    }
    return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [visibleEntities]);

  // A view of the detection limited to entities above the threshold, so the PDF preview only
  // draws boxes for what's currently visible.
  const filteredDetection = useMemo(
    () => ({ ...detection, entities: visibleEntities }),
    [detection, visibleEntities],
  );

  const selectedCount = selected.size;
  const total = visibleEntities.length;
  const hiddenCount = detection.entities.length - visibleEntities.length;
  const isPdf = detection.contentType === 'application/pdf';
  const redactTotal = selectedCount + manualBoxes.length;

  return (
    <div className={styles.container}>
      <div className={styles.header}>
        <div>
          <h2 className={styles.title}>Review &amp; select what to redact</h2>
          <p className={styles.meta}>
            <strong>{detection.fileName}</strong> &nbsp;·&nbsp; {formatBytes(detection.fileSizeBytes)}{' '}
            &nbsp;·&nbsp; {total} PII item{total !== 1 ? 's' : ''} shown
            {hiddenCount > 0 ? ` (${hiddenCount} below ${minConfidence}% hidden)` : ''}
          </p>
        </div>
        <div className={styles.headerActions}>
          <button className={styles.applyBtn} onClick={handleApply} disabled={busy}>
            {busy ? 'Redacting…' : `Redact ${redactTotal} item${redactTotal !== 1 ? 's' : ''}`}
          </button>
          <button className={styles.cancelBtn} onClick={onCancel} disabled={busy}>
            Cancel
          </button>
        </div>
      </div>

      <p className={styles.hint}>
        Showing detections at or above the confidence threshold, all selected by default. Uncheck
        anything that should be kept — for example, keep the patient while redacting the nurse and
        doctor. Only the checked items are removed.
        {isPdf ? ' Missed something? Click any word in the preview to add a manual redaction (click it again to remove).' : ''}
      </p>

      {(detection.entities.length > 0 || manualBoxes.length > 0) && (
        <div className={styles.controls}>
          {detection.entities.length > 0 && (
            <label className={styles.confidence}>
              Min. confidence: <strong>{minConfidence}%</strong>
              <input
                type="range"
                min={0}
                max={100}
                step={5}
                value={minConfidence}
                onChange={(e) => applyConfidenceFilter(Number(e.target.value))}
                disabled={busy}
                className={styles.slider}
              />
              <span className={styles.controlHint}>Lower to show more; raise to hide low-confidence false positives</span>
            </label>
          )}
          {manualBoxes.length > 0 && (
            <span className={styles.manualCount}>
              {manualBoxes.length} manual redaction{manualBoxes.length !== 1 ? 's' : ''} added
            </span>
          )}
        </div>
      )}

      <div className={styles.body}>
        {/* Highlighted document preview */}
        <section className={styles.docPane} aria-label="Document preview with highlighted PII">
          <div className={styles.docHeader}>Document preview</div>
          {detection.contentType === 'application/pdf' ? (
            <PdfHighlightPreview
              detection={filteredDetection}
              selected={selected}
              manualBoxes={manualBoxes}
              onToggle={toggle}
              onAddManual={addManual}
              onRemoveManual={removeManual}
              categoryColor={categoryColor}
            />
          ) : (
            <pre className={styles.docText}>
              {segments.map((seg, i) => {
                if (!seg.entity) return <span key={i}>{seg.text}</span>;
                const color = categoryColor(seg.entity.category);
                const isSelected = selected.has(seg.entity.id);
                return (
                  <mark
                    key={i}
                    className={`${styles.mark} ${isSelected ? styles.markOn : styles.markOff}`}
                    style={{
                      color,
                      borderColor: color,
                      backgroundColor: isSelected ? `${color}22` : 'transparent',
                    }}
                    title={`${seg.entity.category}${seg.entity.subCategory ? ' · ' + seg.entity.subCategory : ''} — ${(
                      seg.entity.confidenceScore * 100
                    ).toFixed(0)}%`}
                    onClick={() => toggle(seg.entity!.id)}
                  >
                    {seg.text}
                  </mark>
                );
              })}
            </pre>
          )}
        </section>

        {/* Grouped instance checklist */}
        <aside className={styles.listPane} aria-label="Detected PII instances">
          <div className={styles.listHeader}>
            <span>
              {selectedCount} of {total} selected
            </span>
            <div className={styles.listActions}>
              <button className={styles.linkBtn} onClick={selectAll} disabled={busy}>
                All
              </button>
              <button className={styles.linkBtn} onClick={clearAll} disabled={busy}>
                None
              </button>
            </div>
          </div>

          {total === 0 ? (
            <div className={styles.empty}>
              {hiddenCount > 0
                ? `No detections at or above ${minConfidence}% — lower the confidence threshold to show ${hiddenCount} more.`
                : '✅ No PII detected in this document.'}
            </div>
          ) : (
            <div className={styles.groups}>
              {grouped.map(([category, items]) => {
                const color = categoryColor(category);
                const ids = items.map((e) => e.id);
                const selectedInGroup = ids.filter((id) => selected.has(id)).length;
                const allSelected = selectedInGroup === ids.length;
                const isCollapsed = !expanded.has(category);
                return (
                  <div key={category} className={styles.group}>
                    <div className={styles.groupTitle} style={{ color }}>
                      <button
                        className={styles.collapseBtn}
                        onClick={() => toggleCollapsed(category)}
                        aria-expanded={!isCollapsed}
                        title={isCollapsed ? `Expand ${category}` : `Collapse ${category}`}
                      >
                        <span className={styles.caret} aria-hidden="true">{isCollapsed ? '▸' : '▾'}</span>
                        <span className={styles.swatch} style={{ backgroundColor: color }} />
                        <span className={styles.groupName}>
                          {category} ({selectedInGroup}/{items.length})
                        </span>
                      </button>
                      <button
                        className={styles.groupToggle}
                        onClick={() => toggleCategory(ids, !allSelected)}
                        disabled={busy}
                        title={allSelected ? `Deselect all ${category}` : `Select all ${category}`}
                      >
                        {allSelected ? 'Deselect all' : 'Select all'}
                      </button>
                    </div>
                    {!isCollapsed && (
                      <ul className={styles.instanceList}>
                        {items.map((e) => (
                          <li key={e.id}>
                            <label className={styles.instance}>
                              <input
                                type="checkbox"
                                checked={selected.has(e.id)}
                                onChange={() => toggle(e.id)}
                                disabled={busy}
                              />
                              <span className={styles.instanceText}>{e.text || '(blank)'}</span>
                              {e.subCategory ? (
                                <span className={styles.subCategory}>{e.subCategory}</span>
                              ) : null}
                              <span className={styles.confidence}>
                                {(e.confidenceScore * 100).toFixed(0)}%
                              </span>
                            </label>
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </aside>
      </div>
    </div>
  );
}
