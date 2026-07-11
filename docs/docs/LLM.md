# LLM Module

## Design

The LLM module generates natural-language summaries of code **without ever reading source code**. It consumes only `ProjectIR` metadata — class names, method signatures, relationships, and metrics.

```
ProjectIR → PromptBuilder → LLM Provider → Summary Text
                              ↓
                         (Ollama / future backends)
```

If the LLM is unavailable or returns empty output, the `LLMSummarizer` falls back to a metrics-based textual summary.

## Components

### PromptBuilder

**File:** `src/SCIDE.LLM/PromptBuilder.cs`

Produces 5 prompt types:

| Method | Prompt Type | Input |
|---|---|---|
| `BuildMethodPrompt` | method | `MethodInfo` + parent `TypeInfo` |
| `BuildClassPrompt` | class | `TypeInfo` + containing namespace |
| `BuildNamespacePrompt` | namespace | `NamespaceInfo` + `ProjectIR` |
| `BuildProjectPrompt` | project | `ProjectIR` |
| `BuildArchitecturePrompt` | architecture | `ProjectIR` |

Prompts follow a strict format:
1. **Role instruction**: "You are an expert software engineer..."
2. **ProjectIR data**: Structured metadata (class hierarchy, method signatures, relationships)
3. **Constraint**: "Do not invent classes, methods, fields, or relationships that are not explicitly listed."
4. **Fallback instruction**: "If the provided data is insufficient, state 'Unable to determine.' and nothing more."

### OllamaProvider

**File:** `src/SCIDE.LLM/OllamaProvider.cs`

Communicates with a local Ollama server via the HTTP API (`/api/generate`).

- Configurable endpoint, model, temperature, max tokens, top_p
- Synchronous `GenerateAsync` method
- Configurable via `LlmConfig` or `ScideConfig`

### LLMSummarizer

**File:** `src/SCIDE.LLM/LLMSummarizer.cs`

High-level orchestrator that:

1. Selects the right prompt builder method based on `summaryType`
2. Calls `ILLMProvider.GenerateAsync`
3. Caches results using SHA-256 hash of prompt as key
4. Falls back to metrics-based summary on failure or empty output

## Supported LLM Backends

| Backend | Status | Notes |
|---|---|---|
| Ollama | ✅ Implemented | HTTP API, tested with codellama |
| llama.cpp | ⏳ Planned | Server mode compatible with OpenAI API format |

## Configuration

```json
{
  "ollamaEndpoint": "http://localhost:11434",
  "modelName": "codellama",
  "temperature": 0.3,
  "maxTokens": 2048,
  "topP": 0.9,
  "contextSize": 4096,
  "cacheSize": 100
}
```
