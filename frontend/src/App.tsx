import { useState } from 'react';
import type { UploadStatus } from './types';
import DocumentUpload from './components/DocumentUpload';
import LoadingSpinner from './components/LoadingSpinner';
import RedactionResult from './components/RedactionResult';
import styles from './App.module.css';

export default function App() {
  const [status, setStatus] = useState<UploadStatus>({ kind: 'idle' });

  const handleReset = () => setStatus({ kind: 'idle' });

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
              Upload a clinical record, lab report, or any MMRS document. The system will
              automatically extract the text and strip all personally identifiable information (PII)
              including patient names, provider names, facility identifiers, dates, addresses, and
              contact details — returning a fully de-identified version suitable for committee
              review.
            </p>
          </div>

          {/* Workflow area */}
          {status.kind === 'idle' && (
            <div className={styles.uploadWrapper}>
              <DocumentUpload onStatusChange={setStatus} />
            </div>
          )}

          {status.kind === 'processing' && (
            <LoadingSpinner message="Analyzing and redacting document — this may take a moment…" />
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

          {status.kind === 'success' && (
            <RedactionResult result={status.result} onReset={handleReset} />
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
