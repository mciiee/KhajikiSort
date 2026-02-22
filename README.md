# FIRE Routing Engine (KhajikiSort)

Automatic after-hours ticket processing and assignment service for multilingual client requests (RU/KZ/ENG), with routing logic and web dashboard.

## Features

- Reads real datasets from CSV:
  - `datasets/tickets.csv`
  - `datasets/managers.csv`
  - `datasets/business_units.csv`
- NLP metadata extraction per ticket:
  - `RequestType`: `Complaint`, `DataChange`, `Consultation`, `Claim`, `AppFailure`, `FraudulentActivity`, `Spam`
  - `Tone`: `Positive`, `Neutral`, `Negative`
  - `Language`: `RU`, `KZ`, `ENG` (supports Cyrillic + Latin variants)
  - `Priority` (1..10), `Summary`, `Recommendation`
- Attachment analysis:
  - detects image attachments
  - adds attachment context into NLP pass
- Routing engine:
  - office selection by geo logic
  - hard filters (`VIP/Priority`, `DataChange -> Chief Specialist`, language skills)
  - round-robin among 2 least-loaded candidates
  - fallback to nearest city with eligible manager if origin city has no candidates
- Outputs:
  - `datasets/routing_results.csv`
  - Web UI dashboard
  - JSON API endpoints

## Tech Stack

- .NET 10
- C#
- ASP.NET Core (minimal API + static UI)

## Quick Start

From project root:

```bash
docker compose up -d
cd backend
dotnet run
```

If port `5000` is busy:

```bash
cd backend
ASPNETCORE_URLS=http://localhost:5001 dotnet run
```

If your environment restricts CLI home writes:

```bash
cd backend
DOTNET_CLI_HOME=/tmp DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 dotnet run
```

For Gemma AI analysis:

```bash
export FIRE_GEMMA_API_KEY="your_api_key_here"
export FIRE_GEMMA_MODEL="gemma-3-4b-it"
```

## UI and API

- UI: `http://localhost:5000`
- Health: `http://localhost:5000/api/health`
- Dashboard data: `http://localhost:5000/api/dashboard`
- Adminer (DB UI): `http://localhost:8080`

## PostgreSQL Export

On startup, backend exports:

- source datasets (`tickets`, `managers`, `business_units`)
- routing output (`routed_tickets`)
- dictionary terms (`ru/kz/eng/signals`)

Connection string env var:

```bash
FIRE_PG_CONN="Host=localhost;Port=5432;Database=firedb;Username=fire;Password=fire"
```

Default connection is the same as Docker Compose credentials above.

Main schema/tables:

- `fire.source_tickets`
- `fire.source_managers`
- `fire.business_units`
- `fire.routed_tickets`
- `fire.dictionary_terms`

## Routing Logic (Business Rules)

1. Determine primary office from ticket address (settlement/region mapping).
2. Apply hard filters to managers in that office:
   - `VIP` or `Priority` segment -> manager must have `VIP` skill.
   - `DataChange` request -> manager must be `Главный специалист`.
   - `KZ`/`ENG` language -> manager must have corresponding language skill.
3. Pick two least-loaded eligible managers and assign by round-robin.
4. If no eligible manager in origin city -> route to nearest city with eligible candidates.

## Project Structure

- `backend/Program.cs` - app startup, data processing, API exposure
- `backend/Data/` - CSV parsing/loading and results writer
- `backend/Nlp/` - dictionaries, language detection, request/tone extraction, attachments analyzer
- `backend/Routing/` - assignment logic and nearest-city fallback
- `backend/Models/` - domain + metadata models
- `dictionaries/` - external NLP dictionaries (`ru.json`, `kz.json`, `eng.json`, `signals.json`)
- `frontend/index.html` - dashboard UI
- `datasets/` - input CSV files + generated `routing_results.csv`
- `datasets/attachments/` - attachment files directory

## Notes

- Dictionaries are heuristic and can be expanded for domain phrases.
- Current NLP is rule-based (no external LLM call).
- If `FIRE_GEMMA_API_KEY` is set, Gemma is used for AI analysis (request type, tone, priority, summary, recommendation, image analysis) with rule-based fallback on API failure.
- Dataset files are ignored by default in `.gitignore`.
