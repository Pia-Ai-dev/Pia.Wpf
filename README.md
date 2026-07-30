<p align="center">
  <img src="src/Pia.Wpf/Resources/Icons/pia-logo_blau_RGB.svg" width="260" alt="Pia" />
</p>

<p align="center"><strong>Personal Intelligent Assistant</strong></p>

<p align="center">
  An AI-powered Windows desktop assistant that optimizes your text<br/>
  and chats with you &mdash; only one hotkey away.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet" alt=".NET 10" />
  <img src="https://img.shields.io/badge/WPF--UI-4.2.0-blue" alt="WPF-UI" />
  <img src="https://img.shields.io/badge/platform-Windows-0078D4?logo=windows" alt="Windows" />
</p>

---

## What is Pia?

Pia sits in your system tray and activates instantly with a global hotkey. Select text in any app, press **Ctrl+Alt+O**, and Pia optimizes it on the spot. Need more? Switch to Assistant mode for a full AI chat.

### Two Modes -  only one hotkey away

| Mode | What it does | Default Hotkey |
|------|-------------|----------------|
| **Optimize** | Transform text using templates &mdash; make an email professional, rewrite for clarity, adjust tone | `Ctrl+Alt+O` |
| **Assistant** | Chat with an AI that remembers your preferences, manages todos and reminders, and stores knowledge in a personal memory | `Ctrl+Alt+P` |

### Key Features

- **Multiple AI providers** &mdash; Pia Cloud, OpenAI, Azure OpenAI, OpenRouter, Mistral, Ollama, or any OpenAI-compatible API
- **Speech-to-text** &mdash; Dictate instead of typing, powered by local Whisper transcription (no data leaves your machine)
- **Text-to-speech** &mdash; Listen to responses with offline Piper TTS &mdash; multiple downloadable voice models, no cloud required
- **Voice mode** &mdash; Hands-free voice conversation overlay: speak your request, hear the answer
- **Smart memory** &mdash; Pia learns your preferences and context over time, with semantic search powered by embeddings
- **Todo &amp; task management** &mdash; Create, prioritize (Low/Medium/High), and track tasks with optional due dates and notes &mdash; plus a drag-and-drop Kanban board view
- **Reminders** &mdash; Set one-time or recurring reminders (Daily, Weekly, Monthly, Yearly) with natural language &mdash; Pia notifies you via Windows toast notifications
- **MCP plugins** &mdash; Extend Assistant mode with Model Context Protocol tools from third-party servers
- **Templates** &mdash; Built-in templates (Business Email, Community Article, Message to Friend) plus custom ones you create
- **Auto-type** &mdash; Optimized text can be typed directly into the previously focused window, or copied to clipboard
- **Cloud sync with optional E2EE** &mdash; Sync settings, templates, and memory across devices, optionally end-to-end encrypted with a printable recovery code
- **Enterprise policy settings** &mdash; Layered preset policy files for managed deployments
- **Multi-language** &mdash; Interface available in English, German, and French
- **Dark theme with Mica** &mdash; Modern Windows 11 look and feel via WPF-UI

### Assistant Tools

In Assistant mode, Pia has access to built-in tools that it uses automatically during conversation:

| Tool Group | Capabilities |
|------------|-------------|
| **Memory** | Store and recall personal facts, contacts, preferences, and notes &mdash; with semantic search via embeddings |
| **Todos** | Create tasks with priorities (Low/Medium/High), due dates, and notes &mdash; query, update, complete, or delete them |
| **Reminders** | Schedule one-time or recurring reminders (Daily, Weekly, Monthly, Yearly) with natural language parsing &mdash; background notifications keep you on track |

All tool actions require your confirmation before executing, so you stay in control.

---

## Quick Start

### Prerequisites

- Windows 10 (1809) or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build & Run

```bash
git clone https://github.com/Pia-Ai-dev/Pia.Wpf.git
cd Pia.Wpf
dotnet build
dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj
```

### First Launch

On first run, a setup wizard walks you through everything:

1. **Welcome** &mdash; Meet Pia and see what it can do
2. **Account Setup** &mdash; Optionally sign in and set up a recovery code for E2EE sync
3. **Provider Setup** &mdash; Configure an AI provider (shown only when needed)
4. **Modes Overview** &mdash; Learn about Optimize and Assistant modes
5. **Your Profile** &mdash; Optionally tell Pia your name and preferred tone (Personal or Business)
6. **Ready** &mdash; Quick-start tips so you can be productive immediately

All fields are optional &mdash; you can skip the entire wizard and configure everything later in Settings.

---

## Getting Started in 60 Seconds

1. **Configure a provider** &mdash; Open Settings and add your OpenAI API key (or connect Azure, Ollama, etc.)
2. **Press `Ctrl+Alt+P`** &mdash; Pia appears, ready for input
3. **Type or dictate** your text, pick a template, and hit Optimize
4. **Result auto-types** into your previous window, or copies to clipboard

That's it. Pia stays in your system tray, ready whenever you need it.

---

## AI Providers

Pia works with multiple AI backends &mdash; pick what suits you:

| Provider | Setup |
|----------|-------|
| **Pia Cloud** | Built-in first-party provider &mdash; no configuration required |
| **OpenAI** | Paste your API key in Settings (custom endpoint/model optional) |
| **Azure OpenAI** | Enter endpoint, deployment name, and API key |
| **OpenRouter** | API key (defaults to `https://openrouter.ai/api/v1`) |
| **Mistral** | API key (defaults to `https://api.mistral.ai/v1`) |
| **Ollama** | Point to your local Ollama instance &mdash; no API key needed, fully offline |
| **OpenAI-compatible** | Any OpenAI-compatible endpoint (LM Studio, vLLM, and similar) |

The provider edit dialog can auto-fetch available models from any configured endpoint. API keys are encrypted locally using Windows DPAPI &mdash; they never leave your machine unencrypted.

---

## Project Structure

```
Pia.Wpf.slnx
├── src/
│   ├── Pia.Wpf/             # WPF desktop client
│   │   ├── Behaviors/       # XAML attached behaviors (autocomplete, drag/drop, kanban)
│   │   ├── Controls/        # Custom WPF controls
│   │   ├── Converters/      # Value converters
│   │   ├── Helpers/         # Utility helpers
│   │   ├── Infrastructure/  # DPAPI, SQLite, P/Invoke
│   │   ├── Localization/    # i18n resources
│   │   ├── Models/          # Data models and enums
│   │   ├── Navigation/      # View navigation
│   │   ├── Services/        # Application services (incl. Plugins/ for MCP)
│   │   ├── ViewModels/      # MVVM ViewModels
│   │   └── Views/           # XAML views and dialogs
│   └── Pia.Shared/          # Shared DTOs for sync
└── tests/
    └── Pia.Wpf.Tests/       # Unit tests
```

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| UI Framework | WPF + [WPF-UI 4.2.0](https://github.com/lepoco/wpfui) (Fluent Design) |
| Runtime | .NET 10.0 |
| MVVM | CommunityToolkit.Mvvm |
| AI Integration | Azure.AI.OpenAI, Microsoft.Extensions.AI |
| MCP | ModelContextProtocol |
| Speech-to-Text | Whisper.NET (local, offline) |
| Text-to-Speech | PiperSharp (local, offline) |
| Audio | NAudio |
| Database | Microsoft.Data.Sqlite |
| Encryption | Windows DPAPI (API keys), E2EE for sync |
| Markdown | MdXaml (NuGet) |
| Installer | Velopack |

---

## Data Storage

| Location | Contents |
|----------|----------|
| `%AppData%/Pia/` | settings.json, templates.json, providers.json |
| `%LocalAppData%/Pia/` | history.db (SQLite), Whisper models |

All data stays local. Cloud sync is opt-in and requires setting up the companion server; end-to-end encryption can be enabled on top, protected by a printable recovery code.

---

## Building

```bash
# Debug build
dotnet build

# Release build
dotnet build -c Release

# Run
dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj

# Clean
dotnet clean
```

---

## Contributing

1. Fork the repo and create a feature branch from `main`:
   ```bash
   git checkout main
   git checkout -b feature/your-feature-name
   ```
2. Make your changes following the code style defined in `.editorconfig`
3. Submit a pull request targeting `main`

### Branch Workflow

| Branch | Purpose |
|--------|---------|
| `main` | Production releases |
| `feature/*` | Feature development |

### Code Style Highlights

- 4-space indent (C#), 2-space indent (XAML)
- `var` for apparent types, expression-bodied members preferred
- PascalCase for public members, `_camelCase` for private fields
- All business logic in ViewModels, never in code-behind
