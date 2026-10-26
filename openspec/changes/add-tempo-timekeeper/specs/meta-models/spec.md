# meta-models Specification

## ADDED Requirements

### Requirement: Single model abstraction
The system SHALL expose one `IMetaModels` interface for every model call (`ChatAsync` with tool definitions, `ChatJsonAsync<T>`, `DescribeImageAsync<T>`, `TranscribeAsync`), with two implementations: `MetaApiModels` (Muse Spark and Muse Voice Transcribe over the Meta Model API) and `BasetenModels` (Muse Spark Open `meta-models/muse-spark-open-1026`, transcription delegated to the Meta API).

#### Scenario: Primary selected from configuration
- **WHEN** the application starts with `Models:Primary` set to `MetaApi` or `Baseten`
- **THEN** the matching implementation is the primary and the other is registered as fallback

#### Scenario: Missing keys
- **WHEN** the primary's API key is missing
- **THEN** startup fails with a clear configuration error naming the key

### Requirement: Meta models only
The system SHALL send every model request to a Meta model and SHALL NOT reference any other model provider SDK.

#### Scenario: No foreign provider packages
- **WHEN** the solution's package references and source are inspected
- **THEN** no Anthropic, OpenAI, Google Gemini, Mistral or Llama-branded client packages or endpoints are present

### Requirement: Automatic fallback
The system SHALL retry a failed chat or vision call once on the fallback implementation when the primary returns 429 or a 5xx, and SHALL record which provider served each call.

#### Scenario: Primary rate-limited
- **WHEN** the primary returns 429
- **THEN** the same request is sent to the fallback and the result is returned with `Provider = Fallback`

#### Scenario: Both fail
- **WHEN** both implementations fail
- **THEN** a `ModelUnavailableException` is thrown and the caller's deterministic fallback path (template nudge, no-op agent run) is used

### Requirement: Structured output
`ChatJsonAsync<T>` and `DescribeImageAsync<T>` SHALL return a typed object parsed from JSON constrained by a schema, with one retry on parse failure.

#### Scenario: Invalid JSON twice
- **WHEN** both attempts fail to parse
- **THEN** a `ModelOutputException` is thrown and no partial result is used

### Requirement: Tool calling
`ChatAsync` SHALL accept a list of tool definitions (name, description, JSON schema) and return either an assistant message or one or more tool calls with parsed arguments.

#### Scenario: Tool call returned
- **WHEN** the model responds with a tool call
- **THEN** the result exposes the tool name and arguments as a typed `ToolCall` and no assistant text is treated as final

### Requirement: Transcription
`TranscribeAsync` SHALL accept audio up to 15 seconds as WAV (mono integer PCM, 16 or 24 kHz — the only encoding the ASR API accepts) and return the transcript text. The API returns no detected-language field, so `Language` is `"unknown"`; intent classification downstream is language-agnostic. *(Amended 26 Oct 2026 from webm/opus+detected-language after verifying the real API; see design.md D2 API facts.)*

#### Scenario: Dutch utterance
- **WHEN** the audio is Dutch speech
- **THEN** the transcript is returned in Dutch (e.g. "ik ga eerst even naar de Albert Heijn") with `Language = "unknown"`

### Requirement: Call accounting
The system SHALL log every model call with provider, model, purpose, token counts where available, and latency, so the README can state usage honestly.

#### Scenario: Usage summary
- **WHEN** `GET /admin/model-usage` is called
- **THEN** counts per provider and purpose are returned
