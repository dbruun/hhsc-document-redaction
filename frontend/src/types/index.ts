export interface RedactedEntity {
  text: string;
  category: string;
  subCategory: string | null;
  confidenceScore: number;
  offset: number;
  length: number;
}

export interface RedactionResponse {
  jobId: string;
  originalBlobUrl: string;
  redactedBlobUrl: string;
  extractedText: string;
  redactedText: string;
  redactedEntities: RedactedEntity[];
  fileName: string;
  fileSizeBytes: number;
  processedAt: string;
}

export interface ErrorResponse {
  error: string;
  details?: string;
}

export type UploadStatus =
  | { kind: 'idle' }
  | { kind: 'uploading'; progress: number }
  | { kind: 'processing' }
  | { kind: 'success'; result: RedactionResponse }
  | { kind: 'error'; message: string };
