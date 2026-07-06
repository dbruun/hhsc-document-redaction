# HHSC / MMRS Document Redaction System

A **React + .NET 10** web application that uses **Microsoft Azure AI Foundry** services to automatically de-identify (redact) sensitive clinical documents before they are reviewed by the Maternal Mortality Review System (MMRS) committee.

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│  React Frontend (Vite + TypeScript)                          │
│  ─ Drag-and-drop document upload                             │
│  ─ Processing status + animated spinner                      │
│  ─ Side-by-side original / redacted text view                │
│  ─ Redacted entity summary table                             │
│  ─ One-click download of de-identified document              │
└────────────────────────┬─────────────────────────────────────┘
                         │ POST /api/document/redact
                         ▼
┌──────────────────────────────────────────────────────────────┐
│  ASP.NET Core 10 Web API                                     │
│                                                              │
│  DocumentController                                          │
│    └─ DocumentRedactionOrchestrator                         │
│         ├─ BlobStorageService    → Azure Blob Storage        │
│         └─ DocumentPiiRedaction  → Azure AI Language         │
│              Service               (native-document PII)     │
└──────────────────────────────────────────────────────────────┘
```

### Redaction pipeline

| Step | Service | What happens |
|------|---------|-------------|
| 1 | Azure Blob Storage | Original document stored in `original/<jobId>.<ext>` (private) |
| 2 | Azure AI Language – native-document PII | Document submitted via SAS URL; Language service extracts text, detects and redacts all PII entities using the character-mask policy, writes `<jobId>.result.json` + redacted binary to the target container |
| 3 | Azure Blob Storage | Redacted plain-text stored in `redacted/<jobId>.txt` |
| 4 | API response | Original text (reconstructed from entity offsets), redacted text, entity list, and blob URLs returned to the client |

### Documents supported

`PDF · DOCX · TXT` — up to **50 MB**.

> The Azure AI Language native-document endpoint currently supports **PDF, DOCX, and TXT** only.
> Image-based document scanning (JPEG, PNG, TIFF, BMP) and HTML are not supported by the
> native-document PII feature.

---

## Prerequisites

| Tool | Version |
|------|---------|
| .NET SDK | 10.0 |
| Node.js | 24 LTS |
| Azure subscription | — |

---

## Azure Resource Setup

### 1. Azure AI Language (via Azure AI Foundry Gateway)

The service is accessed through the pre-configured Azure AI Foundry API gateway:

```
https://derekgatewaytest.azure-api.net/multimodalsearch-resource
```

You only need the **API key** — the endpoint is already set as the default in `appsettings.json`. Set it via User Secrets or the `AZURE_LANGUAGE_KEY` environment variable (see *Configuration* below).

### 2. Azure Blob Storage

1. Create a **Storage Account**.
2. Copy the **Connection String** from *Access Keys*.
3. Optionally create a container named `documents` (the app creates it automatically if absent).
4. Grant the Language resource access to the storage account using either:
   - **Managed Identity** (recommended for production): assign the *Storage Blob Data Contributor*
     role to the Language resource's managed identity.
   - **Shared Access Signatures** (used by default in this app): SAS tokens are generated
     automatically at request time using the storage connection string.

---

## Configuration

### Option A — User Secrets (local development)

```bash
cd backend/DocumentRedaction.API

dotnet user-secrets set "Azure:Language:Key"             "<key>"
dotnet user-secrets set "Azure:Storage:ConnectionString" "<connection-string>"
```

### Option B — Environment variables

```
Azure__Language__Key=...
Azure__Storage__ConnectionString=...
Azure__Storage__ContainerName=documents   # optional, default: documents
```

### Option C — Docker Compose

Copy `.env.example` to `.env` and fill in your values, then see *Running with Docker Compose* below.

---

## Running locally (development)

### Backend

```bash
cd backend/DocumentRedaction.API
dotnet run
# Listening on http://localhost:5000
```

### Frontend

```bash
cd frontend
npm install
npm run dev
# Vite dev server on http://localhost:5173
# /api/* requests are proxied to http://localhost:5000
```

Open **http://localhost:5173** in your browser.

---

## Running with Docker Compose

```bash
cp .env.example .env
# Edit .env with your Azure credentials

docker compose up --build
```

- Frontend: **http://localhost:5173**
- Backend API: **http://localhost:5000**

---

## Project structure

```
hhsc-document-redaction/
├── backend/
│   └── DocumentRedaction.API/
│       ├── Controllers/
│       │   └── DocumentController.cs              # POST /api/document/redact
│       ├── Models/
│       │   └── RedactionModels.cs                 # Request / Response records
│       ├── Services/
│       │   ├── BlobStorageService.cs              # Azure Blob Storage + SAS helpers
│       │   ├── DocumentPiiRedactionService.cs     # Azure AI Language native-document PII
│       │   ├── DocumentRedactionOrchestrator.cs   # Pipeline orchestrator
│       │   └── I*.cs                              # Service interfaces
│       ├── Program.cs
│       ├── appsettings.json
│       └── Dockerfile
├── frontend/
│   ├── src/
│   │   ├── components/
│   │   │   ├── DocumentUpload.tsx                 # Drag-and-drop upload zone
│   │   │   ├── LoadingSpinner.tsx                 # Processing indicator
│   │   │   └── RedactionResult.tsx                # Results view + entity table
│   │   ├── services/
│   │   │   └── api.ts                             # fetch wrapper
│   │   ├── types/
│   │   │   └── index.ts                           # TypeScript interfaces
│   │   └── App.tsx
│   ├── nginx.conf                                 # Production reverse-proxy config
│   ├── vite.config.ts                             # Dev proxy: /api → :5000
│   └── Dockerfile
├── docker-compose.yml
├── .env.example
└── README.md
```

---

## API reference

### `POST /api/document/redact`

Accepts a `multipart/form-data` request with a single `file` field.

**Response `200 OK`**

```json
{
  "jobId": "a1b2c3...",
  "originalBlobUrl": "https://storage.../original/a1b2c3.pdf",
  "redactedBlobUrl": "https://storage.../redacted/a1b2c3.txt",
  "extractedText": "Patient Jane Doe was admitted on 01/15/2024...",
  "redactedText": "Patient ******** was admitted on **********...",
  "redactedEntities": [
    {
      "text": "Jane Doe",
      "category": "Person",
      "subCategory": null,
      "confidenceScore": 0.99,
      "offset": 8,
      "length": 8
    }
  ],
  "fileName": "admission-record.pdf",
  "fileSizeBytes": 245120,
  "processedAt": "2024-01-15T14:32:00Z"
}
```

**Response `400 Bad Request`** — unsupported file type or file too large.

**Response `500 Internal Server Error`** — Azure service error.

---

## Security considerations

- Documents are stored in a **private** Blob Storage container. No public access is configured.
- SAS tokens are generated with minimal required permissions and a 1-hour expiry.
- API keys are never committed to source control — use User Secrets or environment variables.
- The `RequestSizeLimit` attribute limits uploads to 50 MB at the controller level.
- CORS is restricted to the configured `AllowedOrigins` list.
- The frontend validates file type and size client-side before submitting.

---

## Compliance notes

This application is designed to support DSHS abstractors performing HIPAA-mandated de-identification (45 CFR § 164.514) of maternal mortality records before committee review. The Azure AI Language PII detection service identifies the following entity categories (among others):

`Person · PersonType · PhoneNumber · Organization · Address · Email · URL · DateTime · Age · MedicalTreatment · Diagnosis`

Review the redacted output carefully before sharing with committee members. This tool is a **decision-support aid** — final de-identification responsibility rests with the reviewing abstractor.
