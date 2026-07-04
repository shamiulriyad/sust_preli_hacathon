# QueueStorm Investigator — bKash SUST CSE Carnival 2026 Preliminary

AI/API copilot for support agents that investigates a customer complaint against
their recent transaction history and returns a structured, routed, safety-checked
JSON decision.

## Tech Stack

- **.NET 8** (ASP.NET Core Minimal API)
- No database — fully stateless, request-scoped reasoning
- No external NuGet packages — built-in `System.Text.Json` and `HttpClient` only
- Optional: Anthropic Claude API (`claude-haiku-4-5-20251001`) for LLM-backed reasoning

## Architecture

```
POST /analyze-ticket
        │
        ▼
ReasoningOrchestrator
        │
        ├─► AnthropicReasoningService (LLM)  ── 8s timeout ──► success? ──► SchemaValidator
        │         │ fails / times out / no API key
        │         ▼
        └─► RuleBasedReasoningService (deterministic fallback) ──► SchemaValidator
                                                                          │
                                                                          ▼
                                                              SafetyFilterService (always runs)
                                                                          │
                                                                          ▼
                                                                   JSON response
```

The service runs in **two modes** depending on whether `ANTHROPIC_API_KEY` is set:

- **With API key**: LLM is the primary reasoning engine; rule-based engine is the
  automatic fallback on timeout, API failure, or malformed LLM output.
- **Without API key**: runs fully on the deterministic rule-based engine. No
  external calls are made at all. This is a complete, self-sufficient
  implementation — an LLM is not required for the service to function.

In both modes, every response passes through `SchemaValidator` (normalizes/corrects
enum values, refuses to ship missing required fields) and `SafetyFilterService`
(regex-scans `customer_reply` / `recommended_next_action` and wholesale replaces
them with a known-safe template if any Section 8 violation is detected).

## MODELS

| Model | Where it runs | Why chosen |
|---|---|---|
| `claude-haiku-4-5-20251001` (Anthropic API) | Anthropic's cloud, called via HTTPS from the API | Fast and cheap, sufficient quality for structured classification/extraction tasks; low latency keeps us well under the 30s timeout and close to the 5s p95 target. Used only when `ANTHROPIC_API_KEY` is configured. |
| Rule-based engine (no ML model) | In-process, no network call | Deterministic fallback and fully standalone mode. Zero latency, zero external dependency, zero hallucination risk. Used automatically when the LLM is unavailable, slow, or returns invalid output, or when no API key is configured at all. |

No other models are used. No fine-tuning was performed.

## Evidence Reasoning Approach

Both engines implement the same contract:

1. Parse the complaint for amount, approximate time, and counterparty signals
   (rule-based: regex/keyword extraction in English + Bangla + Banglish; LLM:
   natural-language understanding).
2. Cross-check those signals against every entry in `transaction_history`.
3. Set `relevant_transaction_id` to the best-matching transaction's ID, or `null`
   if nothing in the history matches — **never invented**.
4. Set `evidence_verdict` by comparing the matched transaction's actual `status`
   against what the complaint claims happened (`consistent` / `inconsistent` /
   `insufficient_data`). Ties or genuine ambiguity always resolve to
   `insufficient_data` rather than a confident guess.
5. Derive `case_type`, `severity`, `department`, and `human_review_required` from
   the verdict and case type using the taxonomy in the problem statement.

The LLM system prompt explicitly instructs the model to perform this same
cross-check against the supplied `transaction_history` rather than trusting the
complaint text at face value, and explicitly instructs it to treat the complaint
field as untrusted input — any embedded instructions inside the complaint are to
be analyzed, never obeyed (prompt-injection resistance).

## Safety Logic

Enforced in two layers:

1. **Prompt-level** (LLM path only): system prompt explicitly forbids requesting
   credentials, confirming refunds/reversals, or referring customers to third
   parties, and instructs the model to ignore any instructions embedded in the
   complaint text.
2. **Code-level guardrail** (`SafetyFilterService`, runs on every response from
   either engine): regex-scans `customer_reply` and `recommended_next_action` for:
   - Credential requests (PIN / OTP / password / CVV / card number)
   - Unauthorized confirmation language ("we will refund you", "we have refunded", etc.)
   - Third-party referral language
   
   If any pattern matches, the entire `customer_reply` and `recommended_next_action`
   are replaced with a pre-written safe template, severity is escalated, and
   `human_review_required` is forced to `true`. This guarantees a Section 8
   violation cannot ship even if the LLM generates unsafe text.

The rule-based engine's customer-facing text is built entirely from a fixed
template library (see `BuildTextFields` in `RuleBasedReasoningService.cs`) — no
free text generation — so it is safe by construction.

## API Contract

- `GET /health` → `{"status":"ok"}`, instant, no dependencies, ready within 60s of start.
- `POST /analyze-ticket` → see request/response schema in the problem statement.
  - `400` on malformed JSON / missing `ticket_id`
  - `422` on semantically invalid input (e.g. empty `complaint`)
  - `500` on unexpected internal error (no stack traces/secrets ever included)
  - The service never crashes the process on bad input.

## Setup & Run

### Local (.NET SDK 8 required)

```bash
cd QueueStormInvestigator
cp .env.example .env   # fill in ANTHROPIC_API_KEY if you want LLM mode; leave blank for rule-based-only
export $(cat .env | xargs)   # or set env vars however your shell prefers
dotnet restore
dotnet run
```

Service listens on the port from `$PORT`, defaulting to `8080` if unset.

### Docker

```bash
docker build -t queuestorm-investigator .
docker run -p 8080:8080 -e PORT=8080 -e ANTHROPIC_API_KEY=your_key_here queuestorm-investigator
```

Omit `-e ANTHROPIC_API_KEY` to run in rule-based-only mode.

### Render

1. New Web Service → connect this repo → Render auto-detects the `Dockerfile`.
2. Set environment variable `ANTHROPIC_API_KEY` in the Render dashboard
   (Environment tab) — never commit it to the repo.
3. Render injects `PORT` automatically; `Program.cs` binds to it directly.

## Validation Against Public Sample Pack

The rule-based engine's decision logic (case_type, evidence_verdict,
relevant_transaction_id) was checked against all 10 cases in
`SUST_Preli_Sample_Cases.json` and matches the expected output on all three
required-equivalence fields for every case, including the harder reasoning
cases:

- **Established-recipient pattern** (SAMPLE-02): correctly flags `inconsistent`
  when the matched transaction's counterparty has multiple prior completed
  transfers, contradicting a "wrong number" claim.
- **Ambiguous multi-match** (SAMPLE-08): correctly returns `null` /
  `insufficient_data` and asks a clarifying question instead of guessing when
  multiple transactions equally match on amount alone.
- **Bangla input/output** (SAMPLE-07): detects Bangla script and responds in
  Bangla, matching the spec's requirement that the reply language follow the
  input language.
- **Pending-status evidence** (SAMPLE-07, SAMPLE-09): treats a `pending`
  transaction status as evidence *supporting* a "money not received" complaint
  for cash-in and merchant settlement cases.
- **Duplicate-pair detection** (SAMPLE-10): identifies two same-amount,
  same-counterparty transactions within a short time window and correctly
  points `relevant_transaction_id` at the second (suspected duplicate), not
  the first.

This is a local sanity check only — the hidden judge set is broader and
includes scenarios not in the public pack, as the problem statement states.

## Known Limitations

- Rule-based keyword coverage for Bangla/Banglish phrasing is broad but not
  exhaustive — complaints using phrasing outside the keyword lists may fall back
  to `case_type: other` / `evidence_verdict: insufficient_data` rather than the
  precise category. This is a deliberate safe-default, not a crash.
- Amount extraction via regex can mismatch if multiple numbers appear in a
  complaint (e.g. a phone number adjacent to an amount) — transaction matching
  uses a multi-signal score (amount + time + counterparty) to reduce false matches,
  but is not infallible.
- The LLM path depends on Anthropic API availability and the team's own API key;
  if neither is available, the service automatically and transparently degrades
  to the rule-based engine with no manual intervention required.
- No persistent storage or learning across requests — each ticket is evaluated
  independently, as specified.
