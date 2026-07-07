import { useState } from 'react';
import type { RedactionResponse, RedactedEntity } from '../types';
import DocumentViewer from './DocumentViewer';
import styles from './RedactionResult.module.css';

interface RedactionResultProps {
  result: RedactionResponse;
  onReset: () => void;
}

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
  Quantity: '#166534',
  Age: '#16a34a',
  Default: '#6b7280',
};

function categoryColor(category: string): string {
  return CATEGORY_COLORS[category] ?? CATEGORY_COLORS['Default'];
}

function EntityBadge({ entity }: { entity: RedactedEntity }) {
  const color = categoryColor(entity.category);
  return (
    <span
      className={styles.badge}
      style={{ borderColor: color, color }}
      title={`"${entity.text}" — confidence: ${(entity.confidenceScore * 100).toFixed(0)}%`}
    >
      {entity.category}
      {entity.subCategory ? ` · ${entity.subCategory}` : ''}
    </span>
  );
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export default function RedactionResult({ result, onReset }: RedactionResultProps) {
  const [activeTab, setActiveTab] = useState<'documents' | 'entities'>('documents');

  const originalUrl = `/api/document/${result.jobId}/original`;
  const redactedUrl = `/api/document/${result.jobId}/redacted`;

  const handleDownload = async () => {
    const res = await fetch(redactedUrl);
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    const dot = result.fileName.lastIndexOf('.');
    const baseName = dot > 0 ? result.fileName.slice(0, dot) : result.fileName;
    const ext = dot > 0 ? result.fileName.slice(dot) : '';
    a.download = `${baseName}_redacted${ext}`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const entityCounts = result.redactedEntities.reduce<Record<string, number>>((acc, e) => {
    acc[e.category] = (acc[e.category] ?? 0) + 1;
    return acc;
  }, {});

  return (
    <div className={styles.container}>
      {/* Header */}
      <div className={styles.header}>
        <div>
          <h2 className={styles.title}>Redaction Complete</h2>
          <p className={styles.meta}>
            <strong>{result.fileName}</strong> &nbsp;·&nbsp; {formatBytes(result.fileSizeBytes)}{' '}
            &nbsp;·&nbsp; Job&nbsp;
            <code className={styles.jobId}>{result.jobId}</code>
          </p>
        </div>
        <div className={styles.headerActions}>
          <button onClick={handleDownload} className={styles.downloadBtn}>
            ⬇ Download Redacted Document
          </button>
          <button onClick={onReset} className={styles.resetBtn}>
            Upload Another
          </button>
        </div>
      </div>

      {/* Entity summary */}
      {result.redactedEntities.length > 0 ? (
        <div className={styles.summaryBox}>
          <p className={styles.summaryTitle}>
            <strong>{result.redactedEntities.length}</strong> PII item
            {result.redactedEntities.length !== 1 ? 's' : ''} redacted
          </p>
          <div className={styles.badgeList}>
            {Object.entries(entityCounts).map(([cat, count]) => (
              <span
                key={cat}
                className={styles.summaryBadge}
                style={{ borderColor: categoryColor(cat), color: categoryColor(cat) }}
              >
                {cat}: {count}
              </span>
            ))}
          </div>
        </div>
      ) : (
        <div className={styles.noPii}>
          ✅ No personally identifiable information detected in this document.
        </div>
      )}

      {/* Tabs */}
      <div className={styles.tabs}>
        <button
          className={`${styles.tab} ${activeTab === 'documents' ? styles.activeTab : ''}`}
          onClick={() => setActiveTab('documents')}
        >
          Documents
        </button>
        <button
          className={`${styles.tab} ${activeTab === 'entities' ? styles.activeTab : ''}`}
          onClick={() => setActiveTab('entities')}
        >
          Redacted Entities ({result.redactedEntities.length})
        </button>
      </div>

      {/* Side-by-side documents */}
      {activeTab === 'documents' && (
        <div className={styles.compareGrid}>
          <div className={styles.docColumn}>
            <div className={styles.docHeader}>Original</div>
            <DocumentViewer url={originalUrl} contentType={result.contentType} />
          </div>
          <div className={styles.docColumn}>
            <div className={`${styles.docHeader} ${styles.docHeaderRedacted}`}>Redacted</div>
            <DocumentViewer url={redactedUrl} contentType={result.contentType} />
          </div>
        </div>
      )}

      {/* Entity detail table */}
      {activeTab === 'entities' && (
        result.redactedEntities.length > 0 ? (
          <div className={styles.tableWrapper}>
            <table className={styles.entityTable}>
              <thead>
                <tr>
                  <th>#</th>
                  <th>Original Text</th>
                  <th>Category</th>
                  <th>Sub-category</th>
                  <th>Confidence</th>
                </tr>
              </thead>
              <tbody>
                {result.redactedEntities.map((entity, i) => (
                  <tr key={i}>
                    <td>{i + 1}</td>
                    <td className={styles.entityText}>
                      <EntityBadge entity={entity} />
                      &nbsp;{entity.text}
                    </td>
                    <td>{entity.category}</td>
                    <td>{entity.subCategory ?? '—'}</td>
                    <td>{(entity.confidenceScore * 100).toFixed(0)}%</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <div className={styles.noPii}>No entities to display.</div>
        )
      )}
    </div>
  );
}
