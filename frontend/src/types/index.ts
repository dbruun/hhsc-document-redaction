export interface DetectedBox {
  page: number;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface PageInfo {
  page: number;
  width: number;
  height: number;
}

export interface LayoutWordInfo {
  offset: number;
  length: number;
  box: DetectedBox;
}

export interface DetectedEntity {
  id: string;
  text: string;
  category: string;
  subCategory: string | null;
  confidenceScore: number;
  offset: number;
  length: number;
  boxes: DetectedBox[];
}

export interface DetectionResponse {
  jobId: string;
  fileName: string;
  fileSizeBytes: number;
  contentType: string;
  extractedText: string;
  pageCount: number;
  pages: PageInfo[];
  entities: DetectedEntity[];
  words: LayoutWordInfo[];
  processedAt: string;
}

export interface ApplyResponse {
  jobId: string;
  redactedUrl: string;
  redactedCount: number;
  fileName: string;
  processedAt: string;
}

export interface ErrorResponse {
  error: string;
  details?: string;
}

export type UploadStatus =
  | { kind: 'idle' }
  | { kind: 'processing' }
  | { kind: 'reviewing'; detection: DetectionResponse }
  | { kind: 'applying'; detection: DetectionResponse }
  | { kind: 'success'; result: ApplyResponse; detection: DetectionResponse }
  | { kind: 'error'; message: string };

