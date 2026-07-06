import { useCallback, useRef, useState } from 'react';
import type { UploadStatus } from '../types';
import { redactDocument } from '../services/api';
import styles from './DocumentUpload.module.css';

const ACCEPTED_TYPES = [
  'application/pdf',
  'image/jpeg',
  'image/png',
  'image/tiff',
  'image/bmp',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  'application/vnd.openxmlformats-officedocument.presentationml.presentation',
  'text/html',
];

const ACCEPT_STRING = ACCEPTED_TYPES.join(',');
const MAX_SIZE_MB = 50;

interface DocumentUploadProps {
  onStatusChange: (status: UploadStatus) => void;
}

export default function DocumentUpload({ onStatusChange }: DocumentUploadProps) {
  const [isDragging, setIsDragging] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  const processFile = useCallback(
    async (file: File) => {
      if (!ACCEPTED_TYPES.includes(file.type)) {
        onStatusChange({
          kind: 'error',
          message: `Unsupported file type "${file.type}". Please upload a PDF, DOCX, image, or HTML file.`,
        });
        return;
      }
      if (file.size > MAX_SIZE_MB * 1024 * 1024) {
        onStatusChange({
          kind: 'error',
          message: `File exceeds the ${MAX_SIZE_MB} MB size limit.`,
        });
        return;
      }

      onStatusChange({ kind: 'processing' });

      try {
        const result = await redactDocument(file);
        onStatusChange({ kind: 'success', result });
      } catch (err) {
        onStatusChange({
          kind: 'error',
          message: err instanceof Error ? err.message : 'An unexpected error occurred.',
        });
      }
    },
    [onStatusChange],
  );

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) processFile(file);
    // Reset input so the same file can be re-uploaded
    e.target.value = '';
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(false);
    const file = e.dataTransfer.files[0];
    if (file) processFile(file);
  };

  const handleDragOver = (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(true);
  };

  const handleDragLeave = () => setIsDragging(false);

  return (
    <div
      className={`${styles.dropZone} ${isDragging ? styles.dragging : ''}`}
      onDrop={handleDrop}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onClick={() => inputRef.current?.click()}
      role="button"
      tabIndex={0}
      aria-label="Upload document for redaction"
      onKeyDown={(e) => e.key === 'Enter' && inputRef.current?.click()}
    >
      <input
        ref={inputRef}
        type="file"
        accept={ACCEPT_STRING}
        onChange={handleFileChange}
        className={styles.hiddenInput}
        aria-hidden="true"
      />

      <div className={styles.icon} aria-hidden="true">
        📄
      </div>
      <p className={styles.primaryText}>
        Drag &amp; drop your document here, or <span className={styles.link}>browse files</span>
      </p>
      <p className={styles.secondaryText}>
        Supports PDF, DOCX, XLSX, PPTX, JPEG, PNG, TIFF, BMP, HTML &mdash; up to {MAX_SIZE_MB} MB
      </p>
    </div>
  );
}
