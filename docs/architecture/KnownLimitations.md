# Known Limitations

## C++ Parser
- Regex-based; does not handle all C++ syntax correctly (templates, advanced SFINAE, preprocessor conditionals)
- No call graph extraction for C++
- No XML doc comment extraction
- ClangSharp native interop requires `libclang.dll` at runtime and is not currently enabled

## General
- Large projects (>1000 files) may cause slow analysis; no incremental parsing yet
- Only C# and C++ source files are supported
- The WinForms GUI only runs on Windows
- LLM summaries depend on the quality and capability of the local model
- No support for .NET Core/5+ project file (.csproj) scanning — only raw source directories
- No support for preprocessor directives, conditional compilation, or platform-specific code

## LLM
- Only Ollama HTTP API is implemented; llama.cpp is planned
- No streaming display in the current WinForms GUI
- The LLM may produce inaccurate summaries if the model is small or untrained on the code patterns
- Rate limiting: no throttling if multiple concurrent summaries are requested

## Testing
- No integration tests that verify against real C++ codebases
- No performance tests or benchmarks
- Test coverage is limited to core components
