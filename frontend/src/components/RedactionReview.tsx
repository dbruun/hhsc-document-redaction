import { useMemo, useState } from 'react';
import type { DetectionResponse, DetectedEntity } from '../types';
import styles from './RedactionReview.module.css';

const CATEGORY_COLORS: Record<string, string> = {
  Person: '#dc2626',
  PersonType: '#ea580c',
  PhoneNumber: '#d97706',
  Organization: '#7c3aed',
  Address: '#0369a1',
  Email: '#0891b2',
  URL: '#0891b2',
  IPAddress: '#0891b2',
  DateTime: '#15803d',
  Date: '#15803d',
  Quantity: '#166534',
  Age: '#16a34a',
  USSocialSecurityNumber: '#be123c',
  Default: '#6b7280',
};

function categoryColor(category: string): string {
  return CATEGORY_COLORS[category] ?? CATEGORY_COLORS['Default'];
}

interface Segment {
  text: string;
  entity?: DetectedEntity;
}

/** Splits the extracted text into plain and entity segments (skipping overlaps). */
function buildSegments(text: string, entities: DetectedEntity[]): Segment[] {
  const ordered = [...entities].sort((a, b) => a.offset - b.offset);
  const segments: Segment[] = [];
  let cursor = 0;

  for (const entity of ordered) {
    if (entity.offset < cursor) continue; // overlapping — already covered
    if (entity.offset > cursor) segments.push({ text: text.slice(cursor, entity.offset) });
    const end = entity.offset + entity.length;
    segments.push({ text: text.slice(entity.offset, end), entity });
    cursor = end;
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
  onApply: (selectedIds: string[]) => void;
  onCancel: () => void;
}

export default function RedactionReview({ detection, busy, onApply, onCancel }: RedactionReviewProps) {
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set(detection.entities.map((e) => e.id)),
  );

  const toggle = (id: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const selectAll = () => setSelected(new Set(detection.entities.map((e) => e.id)));
  const clearAll = () => setSelected(new Set());

  const segments = useMemo(
    () => buildSegments(detection.extractedText, detection.entities),
    [detection.extractedText, detection.entities],
  );

  const grouped = useMemo(() => {
    const map = new Map<string, DetectedEntity[]>();
    for (const e of detection.entities) {
      const list = map.get(e.category) ?? [];
      list.push(e);
      map.set(e.category, list);
    }
    return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [detection.entities]);

  const selectedCount = selected.size;
  const total = detection.entities.length;

  return (
    <div className={styles.container}>
      <div className={styles.header}>
        <div>
          <h2 className={styles.title}>Review &amp; select what to redact</h2>
          <p className={styles.meta}>
            <strong>{detection.fileName}</strong> &nbsp;·&nbsp; {formatBytes(detection.fileSizeBytes)}{' '}
            &nbsp;·&nbsp; {total} PII item{total !== 1 ? 's' : ''} detected
          </p>
        </div>
        <div className={styles.headerActions}>
          <button className={styles.applyBtn} onClick={() => onApply([...selected])} disabled={busy}>
            {busy ? 'Redacting…' : `Redact ${selectedCount} selected`}
          </button>
          <button className={styles.cancelBtn} onClick={onCancel} disabled={busy}>
            Cancel
          </button>
        </div>
      </div>

      <p className={styles.hint}>
        Everything detected is selected by default. Uncheck anything that should be kept — for
        example, keep the patient while redacting the nurse and doctor. Only the checked items are
        removed.
      </p>

      <div className={styles.body}>
        {/* Highlighted document preview */}
        <section className={styles.docPane} aria-label="Document preview with highlighted PII">
          <div className={styles.docHeader}>Document preview</div>
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
            <div className={styles.empty}>✅ No PII detected in this document.</div>
          ) : (
            <div className={styles.groups}>
              {grouped.map(([category, items]) => {
                const color = categoryColor(category);
                return (
                  <div key={category} className={styles.group}>
                    <div className={styles.groupTitle} style={{ color }}>
                      <span className={styles.swatch} style={{ backgroundColor: color }} />
                      {category} ({items.length})
                    </div>
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
