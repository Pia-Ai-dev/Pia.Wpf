@echo off
setlocal enabledelayedexpansion
::
:: Run this file to set the env vars consumed by
:: tests/Pia.Wpf.Tests/Integration/Providers/ProviderTestEnvironment.cs.
::
:: Usage:
::   1. Copy this file to a private location (it should NOT be committed once
::      filled in -- the placeholders below stay as the committed template).
::   2. Fill in the secrets.
::   3. In a command prompt at the repo root:
::        call scripts\set-provider-test-env.bat
::        dotnet test -- --explicit only --filter-namespace "Pia.Tests.Integration.Providers"
::
:: These tests are marked Explicit, so a plain "dotnet test" never runs them --
:: --explicit only is what opts in.
::
:: Any provider whose KEY/ENDPOINT is left empty will have its integration
:: tests Skipped (not Failed). Leave blocks empty for providers you don't
:: have credentials for.

:: ---------- OpenAI ----------
set PIA_TEST_OPENAI_KEY=
set PIA_TEST_OPENAI_MODEL=gpt-5-mini

:: ---------- Azure OpenAI ----------
set PIA_TEST_AZURE_ENDPOINT=
set PIA_TEST_AZURE_KEY=
set PIA_TEST_AZURE_DEPLOYMENT=

:: ---------- Mistral ----------
set PIA_TEST_MISTRAL_KEY=
set PIA_TEST_MISTRAL_MODEL=mistral-small-latest

:: ---------- OpenRouter ----------
set PIA_TEST_OPENROUTER_KEY=
set PIA_TEST_OPENROUTER_MODEL=openai/gpt-5-mini

:: ---------- Ollama ----------
:: Gated: tests skip unless PIA_TEST_OLLAMA_ENDPOINT is explicitly set, even
:: though the default localhost endpoint is fine for the handler.
set PIA_TEST_OLLAMA_ENDPOINT=
set PIA_TEST_OLLAMA_MODEL=qwen3:8b

:: ---------- vLLM ----------
:: Gated: endpoint must be set; no public default.
set PIA_TEST_VLLM_ENDPOINT=
set PIA_TEST_VLLM_MODEL=Qwen/Qwen3-8B

:: ---------- Report what is configured ----------
echo Pia provider integration env loaded:
call :check PIA_TEST_OPENAI_KEY
call :check PIA_TEST_AZURE_KEY
call :check PIA_TEST_MISTRAL_KEY
call :check PIA_TEST_OPENROUTER_KEY
call :check PIA_TEST_OLLAMA_ENDPOINT
call :check PIA_TEST_VLLM_ENDPOINT
goto :eof

:check
if defined %~1 (
    if not "!%~1!"=="" (
        echo   [x] %~1
    ) else (
        echo   [ ] %~1  (tests will be skipped^)
    )
) else (
    echo   [ ] %~1  (tests will be skipped^)
)
goto :eof
