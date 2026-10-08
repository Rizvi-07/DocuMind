# DocuMind — Document Q&A with .NET and RAG

DocuMind is an in-progress full-stack application for asking questions about uploaded documents. It combines an ASP.NET Core backend, a Next.js frontend, PostgreSQL with pgvector, and planned Gemini integration through retrieval-augmented generation (RAG).

**The product rule is document first:** a user must upload a document and wait for successful processing before starting a conversation. Answers will use relevant passages from the selected documents and include source citations. If the selected documents do not contain enough information, the application should say so rather than provide an unsupported answer. General-purpose chat is outside the intended scope.

**Current stage:** authentication and the database foundation are implemented and tested. Document upload, ingestion, Gemini requests, retrieval, citations, and the application UI are the next milestones. This repository is not yet a working end-to-end document assistant.

## Project purpose

The goal is to make lengthy documents easier to explore while building practical experience with secure web APIs, relational and vector data, reliable background processing, and external AI services.

The project takes functional inspiration from a Laravel document-assistant reference application. This repository implements the .NET version with controller-based APIs, services for application logic, EF Core persistence, a separate worker, and a Next.js frontend. Features in the reference application are not automatically features of this implementation.

## Technology stack

- **Backend:** C# and ASP.NET Core 10, controller-based APIs, dependency injection, and Entity Framework Core 10.
- **Authentication:** ASP.NET Core Identity, email confirmation, cookie sessions, and CSRF protection.
- **Database:** PostgreSQL 17 with pgvector, managed locally through Docker Compose.
- **Background processing:** a .NET Worker Service with persisted job state and lease/retry rules in the data model.
- **Frontend:** Next.js 16, React 19, TypeScript, and Tailwind CSS 4.
- **AI integration, planned:** Gemini for embeddings and document-grounded answers. The current configuration and schema select `gemini/gemini-embedding-2` with 1,536 dimensions; provider HTTP clients are not implemented yet.
- **Testing:** xUnit v3 and Moq, Node's built-in test runner, and separate real HTTP/PostgreSQL flow checkers.

## Implemented foundation

### Accounts and browser security

- Registration with input validation, Identity password validation/hashing, and unique email requirements.
- Email confirmation and resend flows with URL-safe tokens, expiration handling, and generic responses that avoid disclosing registered addresses.
- Confirmed-email login, failed-password lockout counting, logout, and a protected current-user endpoint returning safe account fields.
- HttpOnly authentication cookies, HTTPS cookie requirements outside Development, CSRF protection for state-changing browser requests, and authentication rate limits.
- Development-only email previews in an ignored local folder. Production startup intentionally fails until a real email sender is registered.

### Persistence and processing design

- User-owned documents and conversations, document chunks, processing jobs, and ordered messages.
- EF Core configuration classes with foreign keys, constraints, indexes, and safe document failure information.
- A fixed embedding-model identity, `vector(1536)` storage, and an HNSW cosine index. Equal vector dimensions alone are not treated as proof that different models are compatible.
- Job retry/backoff, lease renewal, interrupted-worker recovery decisions, and optimistic concurrency using PostgreSQL `xmin`.
- Inspected migrations and documented checks that preserve existing Identity data.

These job rules and vector queries are tested foundations. The worker currently logs a heartbeat; it does not yet extract documents or generate embeddings. Database relationships also do not replace authorization: future document and retrieval services must scope queries to the authenticated user.

### Frontend and local development

- Next.js and Tailwind scaffolding, a same-origin development API proxy, and a cookie/CSRF-aware request helper.
- Development configuration loaded from ignored `deployment/.env`, with a safe example file and server-only Gemini settings.
- Database readiness and Development OpenAPI endpoints.

The frontend still displays starter content. Account forms, document screens, and chat screens are not implemented.

## Planned document Q&A workflow

1. **Upload:** an authenticated user uploads a supported document to private storage.
2. **Process:** the worker extracts text, records source metadata, and creates overlapping chunks. PDF OCR will be added for scanned documents.
3. **Embed:** Gemini generates a vector for each chunk using the configured model and output dimensions.
4. **Select:** the user selects at least one of their successfully processed documents before creating a conversation.
5. **Retrieve:** the backend embeds the question and searches only the selected, user-owned documents for relevant passages.
6. **Answer:** Gemini receives the question and retrieved context, and the application returns a document-grounded answer with citations or an insufficient-information response.
7. **Persist:** the conversation, messages, and citation references are saved for later review.

The current conversation schema has an optional single-document relationship. Enforcing required document selection and supporting multiple selected documents are explicit upcoming changes. Stored message citations also need to be added before the chat feature is complete.

## Architecture and repository layout

Controllers handle HTTP behavior; account logic already lives in Services. Document processing and RAG orchestration will follow the same boundary.

```text
backend/
  DocuMind.slnx
  src/
    DocuMind.Api/             # Controllers, HTTP contracts, authentication configuration
    DocuMind.Services/        # Account services, email delivery, configuration; future RAG logic
    DocuMind.Data/            # Identity, entities, EF configurations, migrations, vector mapping
    DocuMind.Worker/          # Hosted worker scaffold; future ingestion job execution
  tests/
    DocuMind.UnitTests/       # Isolated backend behavior tests
    DocuMind.Auth.FlowChecks/ # Real HTTP and PostgreSQL account/session checks
    DocuMind.Data.FlowChecks/ # Real PostgreSQL constraints, vectors, and concurrency checks
frontend/                    # Next.js application, request helper, and frontend tests
deployment/                  # PostgreSQL Compose configuration and local environment template
docs/                        # Verified progress, design decisions, and testing guides
```

In Development, the browser uses `http://localhost:3000`. Next.js forwards relative `/api/*` requests to the backend at port 5185, keeping browser cookies and CSRF requests on one application origin. Docker Compose currently runs PostgreSQL only; the API, worker, and frontend run on the host.

## Engineering focus

The implemented work demonstrates Identity-based account flows, privacy-conscious HTTP responses, separation of HTTP and application logic, relational ownership constraints, vector compatibility checks, concurrency-aware job state, and behavior-focused testing.

The remaining work will connect these foundations into a document-processing and retrieval system. Production readiness, retrieval quality, and live Gemini behavior have not yet been established.

## Development roadmap

1. **Private document upload:** add file validation, MIME/size metadata, private storage, owner-scoped listing/deletion, and processing-job creation. Start with PDF support.
2. **Ingestion worker:** implement safe job claiming, text extraction, chunking, progress reporting, retries, and interruption recovery using the existing job rules.
3. **Embeddings and retrieval:** add Gemini clients, validate vector dimensions, and implement searches scoped to document ownership and selection.
4. **Document-bound conversations:** require at least one ready document, support multiple selected documents, generate grounded answers, and persist citations.
5. **Next.js application UI:** build account forms, upload/list/detail views, processing-status updates, document selection, and chat history.
6. **More formats and OCR:** add scanned PDF, DOCX, XLSX, and CSV processing with suitable page, sheet, and row metadata.
7. **Operational completion:** add password reset, production email delivery, usage controls, administration, deployment configuration, and CI.

Each milestone should include ownership/security checks, failure-path tests, and an updated development-progress record. The next implementation milestone is private document upload; no free-form chat endpoint is planned.

## Run the current development foundation

Prerequisites: .NET 10 SDK, Node.js 22, npm, and Docker Desktop with Compose. Run the following from the repository root in PowerShell.

```powershell
# Create local settings only when absent; preserve an existing password and database volume.
if (-not (Test-Path deployment/.env)) {
    Copy-Item deployment/.env.example deployment/.env
}
# Edit deployment/.env locally before continuing. Keep credentials out of Git.

# Start the persistent PostgreSQL service and build the backend.
docker compose --env-file deployment/.env -f deployment/compose.yaml up -d postgres
dotnet build backend/DocuMind.slnx

# Start the API in Development so it loads the ignored local configuration.
dotnet run --project backend/src/DocuMind.Api --no-build --launch-profile http
```

For a new database, review and apply the migrations before exercising account endpoints. Startup does not apply them automatically. EF tooling requires a privately supplied `ConnectionStrings__DocuMind` process variable and does not load the local dotenv file. See the [data-model guide](docs/data-model.md) for migration behavior and preservation checks, and the [environment guide](docs/environment-configuration.md) for configuration precedence and launch details. Changing an environment-file password does not change the password already stored in a PostgreSQL volume.

In a second terminal, start the frontend:

```powershell
# Install the locked frontend dependencies, then start the same-origin development proxy.
Set-Location frontend
npm ci
npm run dev -- --port 3000
```

Open `http://localhost:3000` to see the current starter frontend. The API readiness endpoint is `http://localhost:5185/api/v1/health/ready`, and the same-origin route is `http://localhost:3000/api/v1/health/ready`. Development OpenAPI is available at `http://localhost:5185/openapi/v1.json`.

Use the [registration guide](docs/registration-testing.md) and [cookie-authentication guide](docs/cookie-authentication-testing.md) to exercise the implemented account APIs. There is no document-upload or chat screen to test yet.

## Tests and verification

```powershell
# From the repository root: build and run database-free backend unit tests.
dotnet build backend/DocuMind.slnx
dotnet test backend/DocuMind.slnx --no-build --no-restore

# Frontend helpers use mocked requests; lint and build provide separate source/type checks.
Push-Location frontend
npm test
npm run lint
npm run build
Pop-Location
```

The latest recorded verification on **2026-10-08** includes 146 backend unit cases, 22 frontend helper cases, 62 HTTP account-flow assertions, and 44 PostgreSQL data-flow assertions. These counts describe the implemented foundation, not an end-to-end document Q&A test.

The Auth and Data flow checkers run separately from `dotnet test`. They require a privately supplied `DOCUMIND_TEST_CONNECTION` and permission to create disposable databases. Consult the guides before running them; they are not intended for a production database.

Live Gemini requests, document ingestion, answer quality, browser UI automation, and production deployment remain unverified. Local configuration and test results do not establish that those future features work.

## Further documentation

- [Verified development progress](docs/development-progress.md) — detailed status, check results, and limitations.
- [Data model and ownership](docs/data-model.md) — relationships, embedding constraints, migrations, and job recovery.
- [Local environment configuration](docs/environment-configuration.md) — ignored settings, precedence, and Gemini configuration.
- [Registration and confirmation testing](docs/registration-testing.md).
- [Cookie authentication and CSRF testing](docs/cookie-authentication-testing.md).
- [Unit testing](docs/unit-testing.md) — test scope and integration-check boundaries.

Credentials, local email previews, build output, and dependency folders are excluded from tracked source. A working production email provider, HTTPS deployment, persistent Data Protection keys, and production proxy configuration remain deployment prerequisites.
