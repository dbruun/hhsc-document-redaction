import type { RedactionResponse } from '../types';

const API_BASE = '/api';

export async function redactDocument(file: File): Promise<RedactionResponse> {
  const formData = new FormData();
  formData.append('file', file);

  const response = await fetch(`${API_BASE}/document/redact`, {
    method: 'POST',
    body: formData,
  });

  if (!response.ok) {
    let message = `Request failed with status ${response.status}`;
    try {
      const err = (await response.json()) as { error?: string; details?: string };
      if (err.error) {
        message = err.details ? `${err.error} — ${err.details}` : err.error;
      }
    } catch {
      // ignore JSON parse error; keep the default message
    }
    throw new Error(message);
  }

  return response.json() as Promise<RedactionResponse>;
}
