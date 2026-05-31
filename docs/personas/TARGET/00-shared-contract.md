# 00 — Shared Contract (canonical schema, wire format, built-ins, prompt spec)

This is the single source of truth that all three codebases implement. The C# DTO lives in
`Pia.Shared` (owned by Pia.Wpf, consumed by the server via submodule). The Swift wire DTO in
Pia.Mac must reproduce the same JSON shape.

---

## 1. Canonical fields

| Field | Type | Sensitive? | Notes |
|-------|------|-----------|-------|
| `Id` | `Guid` | no | Stable identity. Built-ins use the fixed GUIDs in §4. |
| `Name` | `string` | **yes** | Display name, e.g. "Experienced Coder". Max 255. |
| `Tagline` | `string?` | **yes** | One-liner for the picker / future Council cards. Max 280. |
| `SystemPrompt` | `string` | **yes** | The identity/voice block that **replaces** the assistant identity. Max 20000. |
| `Guardrails` | `string?` | **yes** | Optional constraints appended after the identity (e.g. "no regulated advice"). Max 5000. |
| `Archetype` | `string` | no | `assistant` \| `analyst` \| `creative` \| `visionary` \| `explainer` \| `custom`. Drives future Council role. Default `custom`. |
| `Expertise` | `string[]?` | **yes** | Domain tags. Small list (≤ 16). |
| `Emoji` | `string?` | no | Single emoji for the chip. |
| `AccentColor` | `string?` | no | Hex `#RRGGBB` for the chip/attribution. |
| `ToolScope` | `int` | no | `0` = none, `1` = read-only, `2` = full. See §5. |
| `PreferredProviderId` | `Guid?` | no | Soft reference to an `AiProvider`. `null` ⇒ use the mode default. See §6. |
| `ReasoningEffort` | `int?` | no | Optional override of the provider's reasoning effort (maps to the `ReasoningEffort` enum). `null` ⇒ provider default. |
| `SchemaVersion` | `int` | no | Currently `1`. Bump only for breaking field changes. |
| `CreatedAt` | `DateTime` | no | UTC. |
| `UpdatedAt` | `DateTime` | no | UTC. **Conflict key** (last-write-wins). Use `UpdatedAt`, like `SyncTodo` (not `ModifiedAt`). |
| `EncryptedPayload` | `string?` | — | Base64 AES-GCM blob; present only under E2EE. |
| `WrappedDek` | `string?` | — | Base64 wrapped DEK; present only under E2EE. |

`IsBuiltIn` is **not** a wire field. It is a local-only flag derived client-side (an `Id` present in
the built-in catalog ⇒ built-in). Built-ins are never serialized to the wire.

### C# DTO (`Pia.Shared/Models/SyncPersona.cs`)

```csharp
using System.Text.Json.Serialization;

namespace Pia.Shared.Models;

public class SyncPersona
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Tagline { get; set; }
    public string? SystemPrompt { get; set; }
    public string? Guardrails { get; set; }
    public string? Archetype { get; set; }            // "assistant" | "analyst" | ... default "custom"
    public List<string>? Expertise { get; set; }
    public string? Emoji { get; set; }
    public string? AccentColor { get; set; }          // "#RRGGBB"
    public int ToolScope { get; set; }                // 0 none, 1 read-only, 2 full
    public Guid? PreferredProviderId { get; set; }
    public int? ReasoningEffort { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedPayload { get; set; }     // Base64: AES-GCM (nonce‖ciphertext‖tag)
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WrappedDek { get; set; }           // Base64: wrapped DEK
}
```

Follow the exact `[JsonPropertyName]`/`[JsonIgnore]` conventions used in
`Pia.Shared/Models/SyncTemplate.cs` so the wire casing is consistent with the rest of sync.

---

## 2. Local model (both clients)

The local model = canonical fields **plus** `bool IsBuiltIn`. On WPF this is
`Pia.Wpf/Models/Persona.cs`; on Mac it's `PiaKit/Models/Persona.swift`. Built-ins set
`IsBuiltIn = true`; everything from sync sets `IsBuiltIn = false`.

---

## 3. E2EE field split (identical on every platform)

When E2EE is **off**: textual fields travel as plaintext; `EncryptedPayload`/`WrappedDek` are null.

When E2EE is **on**:

- **Encrypted into `EncryptedPayload`** (and nulled on the wire): `Name`, `Tagline`,
  `SystemPrompt`, `Guardrails`, `Expertise`.
- **Stay plaintext** (non-sensitive structural/config): `Archetype`, `Emoji`, `AccentColor`,
  `ToolScope`, `PreferredProviderId`, `ReasoningEffort`, `SchemaVersion`, `CreatedAt`, `UpdatedAt`.

Rationale: the server never interprets persona fields, so the only question is what metadata is
visible server-side. Encrypting the text protects the user's authored content while keeping the
small structural fields available for non-E2EE-aware tooling/debug endpoints. The E2EE
service key/id convention mirrors templates: key `"persona"`, id `persona.Id.ToString()`.

> The payload is a JSON object of the encrypted fields, e.g.
> `{"Name":...,"Tagline":...,"SystemPrompt":...,"Guardrails":...,"Expertise":[...]}`,
> then AES-GCM-encrypted. Keep the inner property names identical across platforms.

---

## 4. Built-in persona catalog (FIXED GUIDs — copy verbatim into WPF & Mac)

Namespace prefix `0000000A-0000-0000-0000-...` distinguishes personas from templates
(`00000001-...`). These GUIDs **must be byte-identical** in `BuiltInPersonas` on both clients,
because a synced active-persona selection references them.

| GUID | Name | Archetype | ToolScope | Emoji | Accent |
|------|------|-----------|-----------|-------|--------|
| `0000000A-0000-0000-0000-000000000001` | Pia · Personal | assistant | full | 🟣 | `#7C4DFF` |
| `0000000A-0000-0000-0000-000000000002` | Pia · Business | assistant | full | 🔵 | `#2962FF` |
| `0000000A-0000-0000-0000-000000000003` | Experienced Coder | analyst | full | 💻 | `#00C853` |
| `0000000A-0000-0000-0000-000000000004` | Marketing Writer | creative | full | ✍️ | `#FF4081` |
| `0000000A-0000-0000-0000-000000000005` | Financial Expert | analyst | full | 📈 | `#00BFA5` |
| `0000000A-0000-0000-0000-000000000006` | Worldwide Company CEO | visionary | full | 🌐 | `#FFAB00` |
| `0000000A-0000-0000-0000-000000000007` | Explain It Simply | explainer | **none** | 🧒 | `#FF6D00` |

> `ToolScope` for the non-Pia built-ins is set to `full` in v1 except *Explain It Simply* (`none`).
> `read-only` is reserved in the enum but not enforced in v1 (see §5).

### Built-in prompts (strawman — redline freely; these are the `SystemPrompt` values)

**Pia · Personal** (default for `UserOperatingMode.Personal`; seeded from the current identity line):
> You are Pia, the user's warm and upbeat personal assistant. You're friendly, encouraging, and a
> little informal — like a sharp, dependable friend. Keep answers concise, accurate, and friendly,
> celebrate small wins, and gently help the user stay organised. When something is unclear, ask one
> quick question rather than guessing.

**Pia · Business** (default for `UserOperatingMode.Business`):
> You are Pia, the user's professional executive assistant. You're crisp, proactive, and
> outcome-oriented. Lead with the answer, then the supporting detail. Prefer structured, skimmable
> responses — short paragraphs, bullets, clear next steps. Keep a polished, business-appropriate
> tone and respect the user's time.

**Experienced Coder:**
> You are a senior software engineer with 15+ years across backend, frontend, and systems. You give
> precise, idiomatic, production-minded answers. Show code when it helps; call out edge cases,
> trade-offs, and failure modes; and name the assumptions you're making. Prefer clarity over
> cleverness and flag security and performance concerns proactively. If a request is ambiguous,
> state the most likely interpretation and proceed.

**Marketing Writer:**
> You are a seasoned marketing copywriter and brand-voice expert. You craft punchy, persuasive copy
> — hooks, headlines, taglines, CTAs — and match the requested tone and audience. You think benefit
> over feature, clarity over jargon, and emotional resonance. Offer a few distinct options when
> useful and briefly note why each works.

**Financial Expert** (note the `Guardrails` field below):
> You are a knowledgeable financial analyst. You are measured, numerate, and risk-aware. You explain
> financial concepts clearly, state your assumptions explicitly, and quantify when possible. You
> weigh downside and uncertainty, not just upside.
>
> `Guardrails`: You provide general educational information only — never personalised investment,
> tax, or legal advice — and you remind the user to consult a licensed professional before making
> decisions.

**Worldwide Company CEO:**
> You are the CEO of a large global company. You think in strategy, leverage, and prioritisation.
> You're decisive and direct; you frame problems in terms of goals, trade-offs, risk, and ROI, and
> you separate the vital few from the trivial many. You give clear recommendations with the
> reasoning behind them and are comfortable making a call under uncertainty.

**Explain It Simply** (the bidirectional "6-year-old"; `ToolScope = none`):
> You are "Explain It Simply", a friendly, curious explaining partner who uses plain, everyday
> language a young child could follow. You work in two directions:
> - **When the user asks you to explain something:** break it into very simple words, short
>   sentences, and concrete everyday analogies. Avoid jargon; if you must use a special word,
>   immediately explain it simply.
> - **When the user is explaining something to you:** become the curious learner. Ask one or two
>   short "why?" / "what do you mean?" questions, then reflect back what you understood in your own
>   simple words ("So you mean…?"). Tell the user clearly when it finally makes sense. Stay
>   encouraging and never make the user feel silly.
>
> Detect which direction you're in from the user's message and switch automatically.

---

## 5. ToolScope semantics

| Value | Meaning | v1 enforcement |
|-------|---------|----------------|
| `0` none | No tools at all. | **Enforced.** Build the prompt via the no-tools path and pass **no** tools to the model. |
| `1` read-only | Only read/query tools (no writes). | **Reserved.** Requires per-tool read/write metadata; v1 treats it as `full` with a note (fast-follow). |
| `2` full | All enabled tools. | **Enforced.** Current behaviour. |

Implementation note for `read-only` (fast-follow): the plugin layer already distinguishes read vs
write at routing time (write tool-calls become a pending confirmation action). To enforce
`read-only` we add a read/write flag to the advertised tool list and filter writes out for scoped
personas. Until then, no built-in ships as `read-only`.

---

## 6. Provider / reasoning override (soft reference)

`PreferredProviderId` is a **soft** reference — it may point at a provider that doesn't exist on
this device (deleted, or never synced, e.g. the always-local PiaCloud provider). Resolution order
when starting an Assistant turn:

1. If `persona.PreferredProviderId` is set **and** that provider exists and is usable → use it.
2. Otherwise fall back to the existing mode default (`GetProviderForMode(Assistant)`).

This reuses the same dangling-reference tolerance the providers already have (`RepairModeDefaults`
on WPF). If `persona.ReasoningEffort` is set, apply it as an **effective** override for that turn
without mutating the stored provider (construct a shallow copy of the provider with the effort
overridden, then build chat options from the copy).

---

## 7. Active-persona selection (synced setting)

Selection is a per-mode setting, stored alongside the existing per-mode provider defaults:

- WPF: `AppSettings.ModePersonaDefaults : Dictionary<WindowMode, Guid>` + helpers
  `GetPersonaForMode` / `SetPersonaForMode`; synced via `SyncSettings.ModePersonaDefaults`
  (`Dictionary<int, Guid>`), mirroring `ModeProviderDefaults`.
- Mac: `AppSettings.modePersonaDefaults : [String: UUID]` + `WireSyncSettings.modePersonaDefaults`,
  mirroring `modeProviderDefaults`.

**Default resolution** (when no entry exists for Assistant mode): map `UserOperatingMode` →
- `Personal` → `0000000A-…-000000000001` (Pia · Personal)
- `Business` → `0000000A-…-000000000002` (Pia · Business)

The first-run wizard writes this default on completion. There is always a resolvable active persona.

---

## 8. Prompt composition spec (both clients must match)

`BuildSystemPrompt` changes from "hardcode identity" to "inject the active persona's identity, keep
the substrate". Given a resolved, non-null `activePersona`:

```
## Identity
{activePersona.SystemPrompt}                 ← replaces the old "You are Pia…" line
{activePersona.Guardrails, if any}           ← appended as its own short paragraph
The current date and time is {now:yyyy-MM-dd HH:mm} ({now:dddd}).   ← substrate (unchanged)

## Language … (unchanged)

IF activePersona.ToolScope == full (2) AND provider supports tools:
    ## Plugins …            (unchanged)
    ## Tool selection …     (unchanged decision tree)
ELSE (ToolScope == none, or provider has no tool support):
    (omit Plugins + Tool-selection; use the existing no-tools prompt path)

## Principles … (unchanged)
## Privacy / tokenization … (unchanged, if enabled)
## Web search … (unchanged, if active)
```

Key invariants:

- The persona controls **only** the identity/voice (+ optional guardrails). All functional sections
  remain owned by the substrate so tool-calling, privacy, and web-search keep working regardless of
  persona.
- `ToolScope == none` ⇒ no tools are attached to the model call **and** the no-tools prompt is used.
- Resolve the `Persona` object once per turn in the caller and pass it in; keep `BuildSystemPrompt`
  synchronous (don't make it async just to fetch the persona).
