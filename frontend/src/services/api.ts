import type { DetectionResponse, ApplyResponse } from '../types';

const API_BASE = '/api';

async function readError(response: Response): Promise<string> {
  let message = `Request failed with status ${response.status}`;
  try {
    const err = (await response.json()) as { error?: string; details?: string };
    if (err.error) {
      message = err.details ? `${err.error} — ${err.details}` : err.error;
    }
  } catch {
    // ignore JSON parse error; keep the default message
  }
  return message;
}

/**
 * DETECT phase: upload a document and get back every detected PII instance for review.
 * Nothing is redacted yet.
 */
export async function detectDocument(file: File): Promise<DetectionResponse> {
  const formData = new FormData();
  formData.append('file', file);

  const response = await fetch(`${API_BASE}/document/detect`, {
    method: 'POST',
    body: formData,
  });

  if (!response.ok) throw new Error(await readError(response));
  return response.json() as Promise<DetectionResponse>;
}

/**
 * APPLY phase: redact only the selected instances. Returns the redacted document location.
 */
export async function applyRedactions(
  jobId: string,
  selectedEntityIds: string[],
  manualRedactionTerms: string[] = [],
  whitelistedTerms: string[] = [],
): Promise<ApplyResponse> {
  const response = await fetch(`${API_BASE}/document/${jobId}/apply`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ selectedEntityIds, manualRedactionTerms, whitelistedTerms }),
  });

  if (!response.ok) throw new Error(await readError(response));
  return response.json() as Promise<ApplyResponse>;
}
