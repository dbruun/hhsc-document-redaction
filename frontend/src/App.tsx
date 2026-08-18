import { useState } from 'react';
import type { UploadStatus } from './types';
import { applyRedactions } from './services/api';
import DocumentUpload from './components/DocumentUpload';
import LoadingSpinner from './components/LoadingSpinner';
import RedactionReview from './components/RedactionReview';
import styles from './App.module.css';

export default function App() {
  const [status, setStatus] = useState<UploadStatus>({ kind: 'idle' });

  const handleReset = () => setStatus({ kind: 'idle' });

  const handleApply = async (
    selectedIds: string[],
    manualRedactionTerms: string[],
    whitelistedTerms: string[],
  ) => {
    if (status.kind !== 'reviewing') return;
    const detection = status.detection;
    setStatus({ kind: 'applying', detection });
    try {
      const result = await applyRedactions(
        detection.jobId,
        selectedIds,
        manualRedactionTerms,
        whitelistedTerms,
      );
      setStatus({ kind: 'success', result, detection });
    } catch (err) {
      setStatus({
        kind: 'error',
        message: err instanceof Error ? err.message : 'An unexpected error occurred.',
      });
    }
  };

  const handleDownload = async (jobId: string, fileName: string) => {
    const res = await fetch(`/api/document/${jobId}/redacted`);
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    const dot = fileName.lastIndexOf('.');
    const baseName = dot > 0 ? fileName.slice(0, dot) : fileName;
    const ext = dot > 0 ? fileName.slice(dot) : '';
    a.download = `${baseName}_redacted${ext}`;
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <div className={styles.page}>
      {/* Site header */}
      <header className={styles.header}>
        <div className={styles.headerInner}>
          <div className={styles.logo}>
            <span className={styles.logoIcon} aria-hidden="true">🏥</span>
            <span>MMRS Document Redaction</span>
          </div>
          <p className={styles.tagline}>
            Powered by&nbsp;
            <a
              href="https://azure.microsoft.com/en-us/products/ai-foundry"
              target="_blank"
              rel="noreferrer"
              className={styles.link}
            >
              Microsoft Azure AI Foundry
            </a>
          </p>
        </div>
      </header>

      {/* Main content */}
      <main className={styles.main}>
        <div className={styles.card}>
          {/* Title & description */}
          <div className={styles.intro}>
            <h1 className={styles.title}>Maternal Mortality Review — Document De-identification</h1>
            <p className={styles.description}>
              Upload a clinical record, lab report, or any MMRS document. The system detects all
              personally identifiable information (PII) and shows it highlighted so you can review
              and choose exactly what to remove — then redacts only your selections, keeping the
              original file format.
            </p>
          </div>

          {/* Workflow area */}
          {status.kind === 'idle' && (
            <div className={styles.uploadWrapper}>
              <DocumentUpload onStatusChange={setStatus} />
            </div>
          )}

          {status.kind === 'processing' && (
            <LoadingSpinner message="Analyzing document and detecting PII — this may take a moment…" />
          )}

          {(status.kind === 'reviewing' || status.kind === 'applying') && (
            <RedactionReview
              detection={status.detection}
              busy={status.kind === 'applying'}
              onApply={handleApply}
              onCancel={handleReset}
            />
          )}

          {status.kind === 'success' && (
            <div className={styles.successBox}>
              <p className={styles.successTitle}>✅ Redaction complete</p>
              <p className={styles.successMessage}>
                <strong>{status.result.redactedCount}</strong> item
                {status.result.redactedCount !== 1 ? 's' : ''} redacted in{' '}
                <strong>{status.result.fileName}</strong>. The de-identified document keeps its
                original format.
              </p>
              <div className={styles.successActions}>
                <button
                  className={styles.downloadBtn}
                  onClick={() => handleDownload(status.result.jobId, status.result.fileName)}
                >
                  ⬇ Download redacted document
                </button>
                <button onClick={handleReset} className={styles.retryBtn}>
                  Redact another
                </button>
              </div>
            </div>
          )}

          {status.kind === 'error' && (
            <div className={styles.errorBox} role="alert">
              <p className={styles.errorTitle}>⚠ Redaction failed</p>
              <p className={styles.errorMessage}>{status.message}</p>
              <button onClick={handleReset} className={styles.retryBtn}>
                Try Again
              </button>
            </div>
          )}
        </div>

        {/* Compliance notice */}
        <p className={styles.notice}>
          This tool uses Azure AI Language (PII detection) and Azure AI Document Intelligence
          (text extraction) via Microsoft Azure AI Foundry. All documents are stored in Azure Blob
          Storage with private access. No document content is retained beyond the configured
          retention policy.
        </p>
      </main>

      <footer className={styles.footer}>
        <p>HHSC / DSHS — Maternal Mortality Review System &copy; {new Date().getFullYear()}</p>
      </footer>
    </div>
  );
}
