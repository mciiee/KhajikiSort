# FIRE Routing Engine (KhajikiSort)

Automatic after-hours ticket processing and assignment service for multilingual client requests (RU/KZ/ENG), with routing logic and web dashboard.

## Features

- Reads real datasets from CSV:
  - `tickets.csv`
  - `managers.csv`
  - `business_units.csv`
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
  - `routing_results.csv`
  - Web UI dashboard
  - JSON API endpoints

## Tech Stack

- .NET 10
- C#
- ASP.NET Core (minimal API + static UI)

## Quick Start

From project root:

```bash
dotnet run
```

If port `5000` is busy:

```bash
ASPNETCORE_URLS=http://localhost:5001 dotnet run
```

If your environment restricts CLI home writes:

```bash
DOTNET_CLI_HOME=/tmp DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 dotnet run
```

## UI and API

- UI: `http://localhost:5000`
- Health: `http://localhost:5000/api/health`
- Dashboard data: `http://localhost:5000/api/dashboard`

## Routing Logic (Business Rules)

1. Determine primary office from ticket address (settlement/region mapping).
2. Apply hard filters to managers in that office:
   - `VIP` or `Priority` segment -> manager must have `VIP` skill.
   - `DataChange` request -> manager must be `Главный специалист`.
   - `KZ`/`ENG` language -> manager must have corresponding language skill.
3. Pick two least-loaded eligible managers and assign by round-robin.
4. If no eligible manager in origin city -> route to nearest city with eligible candidates.

## Project Structure

- `Program.cs` - app startup, data processing, API exposure
- `Data/` - CSV parsing/loading and results writer
- `Nlp/` - dictionaries, language detection, request/tone extraction, attachments analyzer
- `Routing/` - assignment logic and nearest-city fallback
- `Models/` - domain + metadata models
- `wwwroot/index.html` - dashboard UI

## Notes

- Dictionaries are heuristic and can be expanded for domain phrases.
- Current NLP is rule-based (no external LLM call).
- Dataset files are ignored by default in `.gitignore`.
