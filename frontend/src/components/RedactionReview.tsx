import { useMemo, useState } from 'react';
import type { DetectionResponse, DetectedEntity } from '../types';
import styles from './RedactionReview.module.css';
import { categoryColor } from './categoryColors';
import PdfHighlightPreview from './PdfHighlightPreview';

const DEFAULT_CONFIDENCE = 70;

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

function countTermOccurrences(text: string, terms: string[]): number {
  return terms.reduce((total, term) => {
    let count = 0;
    let searchFrom = 0;
    while (searchFrom <= text.length - term.length) {
      const offset = text.toLocaleLowerCase().indexOf(term.toLocaleLowerCase(), searchFrom);
      if (offset < 0) break;
      searchFrom = offset + term.length;
      const startsWithWordCharacter = /[\p{L}\p{N}]/u.test(text[offset]);
      const endsWithWordCharacter = /[\p{L}\p{N}]/u.test(text[offset + term.length - 1]);
      const validStart = !startsWithWordCharacter || offset === 0 || !/[\p{L}\p{N}]/u.test(text[offset - 1]);
      const end = offset + term.length;
      const validEnd = !endsWithWordCharacter || end === text.length || !/[\p{L}\p{N}]/u.test(text[end]);
      if (validStart && validEnd) count++;
    }
    return total + count;
  }, 0);
}

interface RedactionReviewProps {
  detection: DetectionResponse;
  busy: boolean;
  onApply: (selectedIds: string[], blacklistTerms: string[]) => void;
  onCancel: () => void;
}

export default function RedactionReview({ detection, busy, onApply, onCancel }: RedactionReviewProps) {
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set(
      detection.entities
        .filter((entity) => entity.confidenceScore * 100 >= DEFAULT_CONFIDENCE)
        .map((entity) => entity.id),
    ),
  );
  const [confidenceThreshold, setConfidenceThreshold] = useState(DEFAULT_CONFIDENCE);
  const [blacklist, setBlacklist] = useState<string[]>([]);
  const [blacklistDraft, setBlacklistDraft] = useState('');

  const applyConfidence = (threshold: number) => {
    setSelected(new Set(
      detection.entities
        .filter((entity) => entity.confidenceScore * 100 >= threshold)
        .map((entity) => entity.id),
    ));
  };

  const toggle = (id: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const selectAll = () => setSelected(new Set(detection.entities.map((entity) => entity.id)));
  const clearAll = () => setSelected(new Set());

  const toggleCategory = (entities: DetectedEntity[]) => {
    const allSelected = entities.every((entity) => selected.has(entity.id));
    setSelected((current) => {
      const next = new Set(current);
      for (const entity of entities) {
        if (allSelected) next.delete(entity.id);
        else next.add(entity.id);
      }
      return next;
    });
  };

  const changeConfidence = (value: number) => {
    setConfidenceThreshold(value);
    applyConfidence(value);
  };

  const addBlacklistTerm = () => {
    const term = blacklistDraft.trim();
    if (!term || blacklist.some((item) => item.toLocaleLowerCase() === term.toLocaleLowerCase())) {
      setBlacklistDraft('');
      return;
    }

    setBlacklist([...blacklist, term]);
    setBlacklistDraft('');
  };

  const removeBlacklistTerm = (term: string) =>
    setBlacklist((current) => current.filter((item) => item !== term));

  const toggleBlacklistTerm = (term: string) => {
    const normalized = term.trim();
    if (!normalized) return;
    setBlacklist((current) => {
      const existing = current.find(
        (item) => item.toLocaleLowerCase() === normalized.toLocaleLowerCase(),
      );
      return existing
        ? current.filter((item) => item !== existing)
        : [...current, normalized];
    });
  };

  const visibleEntities = useMemo(
    () => detection.entities.filter(
      (entity) => entity.confidenceScore * 100 >= confidenceThreshold,
    ),
    [detection.entities, confidenceThreshold],
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

  const selectedCount = selected.size;
  const total = visibleEntities.length;
  const hiddenCount = detection.entities.length - total;
  const blacklistMatchCount = useMemo(
    () => countTermOccurrences(detection.extractedText, blacklist),
    [detection.extractedText, blacklist],
  );

  return (
    <div className={styles.container}>
      <div className={styles.header}>
        <div>
          <h2 className={styles.title}>Review &amp; select what to redact</h2>
          <p className={styles.meta}>
            <strong>{detection.fileName}</strong> &nbsp;·&nbsp; {formatBytes(detection.fileSizeBytes)}{' '}
            &nbsp;·&nbsp; {total} PII item{total !== 1 ? 's' : ''} shown
            {hiddenCount > 0 ? ` (${hiddenCount} below ${confidenceThreshold}% hidden)` : ''}
          </p>
        </div>
        <div className={styles.headerActions}>
          <button className={styles.applyBtn} onClick={() => onApply([...selected], blacklist)} disabled={busy}>
            {busy ? 'Redacting…' : `Redact ${selectedCount} selected${blacklistMatchCount ? ` + ${blacklistMatchCount} matches` : ''}`}
          </button>
          <button className={styles.cancelBtn} onClick={onCancel} disabled={busy}>
            Cancel
          </button>
        </div>
      </div>

      <p className={styles.hint}>
        Items at or above the confidence threshold are shown and selected. Lower the threshold to
        review more results, or raise it to hide likely false positives. Only checked items and
        redact-everywhere matches are removed.
      </p>

      <div className={styles.filters}>
        <label className={styles.confidenceFilter}>
          <span>
            Minimum confidence <strong>{confidenceThreshold}%</strong>
          </span>
          <input
            type="range"
            min="0"
            max="100"
            step="1"
            value={confidenceThreshold}
            onChange={(event) => changeConfidence(Number(event.target.value))}
            disabled={busy}
          />
        </label>

        <div className={styles.blacklistFilter}>
          <label htmlFor="blacklist-term">Redact everywhere</label>
          <div className={styles.blacklistEntry}>
            <input
              id="blacklist-term"
              list="detected-entity-values"
              value={blacklistDraft}
              onChange={(event) => setBlacklistDraft(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') {
                  event.preventDefault();
                  addBlacklistTerm();
                }
              }}
              placeholder="Enter any word or phrase"
              disabled={busy}
            />
            <datalist id="detected-entity-values">
              {[...new Set(detection.entities.map((entity) => entity.text.trim()).filter(Boolean))]
                .map((text) => <option key={text} value={text} />)}
            </datalist>
            <button type="button" onClick={addBlacklistTerm} disabled={busy || !blacklistDraft.trim()}>
              Add
            </button>
          </div>
          {blacklist.length > 0 && (
            <div className={styles.blacklistTerms} aria-label="Words and phrases to redact everywhere">
              {blacklist.map((term) => (
                <span className={styles.blacklistTerm} key={term}>
                  {term}
                  <button
                    type="button"
                    onClick={() => removeBlacklistTerm(term)}
                    disabled={busy}
                    aria-label={`Remove ${term} from redact everywhere`}
                    title={`Remove ${term}`}
                  >
                    ×
                  </button>
                </span>
              ))}
            </div>
          )}
        </div>
      </div>

      <div className={styles.body}>
        {/* Highlighted document preview */}
        <section className={styles.docPane} aria-label="Document preview with highlighted PII">
          <div className={styles.docHeader}>Document preview</div>
          {detection.contentType === 'application/pdf' ? (
            <PdfHighlightPreview
              detection={detection}
              entities={visibleEntities}
              selected={selected}
              blacklist={blacklist}
              onToggle={toggle}
              onToggleBlacklistTerm={toggleBlacklistTerm}
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
              {selectedCount} selected · {detection.entities.length} detected
              {hiddenCount > 0 ? ` · ${hiddenCount} hidden` : ''}
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
                const selectedInCategory = items.filter((item) => selected.has(item.id)).length;
                const allSelected = selectedInCategory === items.length;
                return (
                  <div key={category} className={styles.group}>
                    <div className={styles.groupTitle} style={{ color }}>
                      <span className={styles.categoryName}>
                        <span className={styles.swatch} style={{ backgroundColor: color }} />
                        {category} ({selectedInCategory}/{items.length})
                      </span>
                      <button
                        type="button"
                        className={styles.categoryToggle}
                        onClick={() => toggleCategory(items)}
                        disabled={busy}
                      >
                        {allSelected ? 'Deselect all' : 'Select all'}
                      </button>
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
