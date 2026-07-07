import { useEffect, useRef, useState } from 'react';
import mammoth from 'mammoth/mammoth.browser';
import styles from './DocumentViewer.module.css';

interface DocumentViewerProps {
  /** Same-origin API URL that streams the document bytes. */
  url: string;
  /** MIME type of the document (drives the renderer choice). */
  contentType: string;
}

type ViewState =
  | { kind: 'loading' }
  | { kind: 'pdf' }
  | { kind: 'html'; html: string }
  | { kind: 'text'; text: string }
  | { kind: 'error'; message: string };

const DOCX_TYPE =
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document';

export default function DocumentViewer({ url, contentType }: DocumentViewerProps) {
  const [state, setState] = useState<ViewState>({ kind: 'loading' });
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let cancelled = false;

    async function render() {
      try {
        if (contentType === 'application/pdf') {
          if (!cancelled) setState({ kind: 'pdf' });
          return;
        }

        if (contentType === DOCX_TYPE) {
          const res = await fetch(url);
          if (!res.ok) throw new Error(`Failed to load document (${res.status})`);
          const arrayBuffer = await res.arrayBuffer();
          const result = await mammoth.convertToHtml({ arrayBuffer });
          if (!cancelled) setState({ kind: 'html', html: result.value });
          return;
        }

        // Fallback: treat as plain text (TXT and anything else).
        const res = await fetch(url);
        if (!res.ok) throw new Error(`Failed to load document (${res.status})`);
        const text = await res.text();
        if (!cancelled) setState({ kind: 'text', text });
      } catch (err) {
        if (!cancelled)
          setState({ kind: 'error', message: err instanceof Error ? err.message : 'Failed to load document.' });
      }
    }

    setState({ kind: 'loading' });
    render();
    return () => {
      cancelled = true;
    };
  }, [url, contentType]);

  if (state.kind === 'loading') {
    return <div className={styles.status}>Loading document…</div>;
  }

  if (state.kind === 'error') {
    return <div className={styles.error}>⚠ {state.message}</div>;
  }

  if (state.kind === 'pdf') {
    return <iframe className={styles.pdfFrame} src={url} title="Document" />;
  }

  if (state.kind === 'html') {
    return (
      <div
        ref={containerRef}
        className={styles.docHtml}
        // Content is produced by mammoth from a trusted, server-provided document.
        dangerouslySetInnerHTML={{ __html: state.html }}
      />
    );
  }

  return <pre className={styles.docText}>{state.text}</pre>;
}
