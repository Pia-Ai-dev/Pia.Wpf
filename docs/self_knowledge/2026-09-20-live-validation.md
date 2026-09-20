# Self-knowledge tools — live validation

**Status:** Three driving questions verified; the pack-disabled contrast arm is outstanding.
**Owner:** Marco Altmann
**Written:** 2026-09-20
**Origin:** S10 of [`2026-09-20-self-knowledge-checklist.md`](2026-09-20-self-knowledge-checklist.md)

Run against the **real production profile** (no `PIA_*` overrides) on a Debug build, provider
Pia Cloud against the local Docker server at `https://localhost:8081`. Evidence throughout is the
Debug-only `AiClientService` tool args/result lines in `%LOCALAPPDATA%\Pia\Logs\pia-2026-09-20.log`,
not the reply text.

## The pack is enabled on a profile that predates it

`PluginService initialized with 11 built-in plugins`, and the turn's own tool list carries both
names:

```
GetAllTools: returning 55 tools from 12 active handlers: [... pia_help, pia_settings ...]
```

No `skipped — IsActive=…, UserEnabled=…` warning for `…00C`, and no sync warning naming it. So an
existing profile picks up the pack from `defaultEnabled` with nothing persisted to override it, and
the server's preference push does not disable it. Fresh-profile behaviour was not tested.

## The three questions

| Question | Tool call | Result |
|---|---|---|
| "Can I perform agentic tasks?" | `pia_help {"query":"agent mode agentic tasks background assignments"}` | 5 guide hits, 1827 chars |
| "Welche Stimme ist bei mir eingestellt, und wie ändere ich die Sprache…?" | `pia_settings {"area":"speech"}` + `{"area":"language"}` | 755 + 572 chars |
| "Kannst du die Art ändern, wie du mir antwortest?" | `pia_help` + `pia_settings {"area":"personas"}` | 2173 + 630 chars |

No `web_search` in any of the three turns.

Two answers worth quoting, because they are the behaviour the feature exists for. The voice question
came back with the install's actual state and a path in the user's own language — "Aktuell ist bei
dir die Stimme Thorsten (Deutsch) eingestellt, unter [Einstellungen > Allgemein > Sprache >
Sprachausgabe > Stimmauswahl]", plus the observation that speech output is currently off. And it
closed the TTS-language question the way the design intends: "Einen separaten Schalter für die
Antwortsprache gibt es nicht." The agentic-tasks answer named agent mode, the "Im Hintergrund
ausführen" button and background assignments — corpus content, not invention.

German questions were answered in German off the English corpus, so the query translation the tool
description asks for is working.

## Not covered

- **The pack-disabled arm.** Turning the pack off writes a plugin preference to the production
  profile and pushes it to the server, and whether the server rejects a preference naming a plugin
  id it does not know is itself unverified. Left alone deliberately.
- **A direct (non-Pia-Cloud) provider.** Every persona on this profile resolves to Pia Cloud, and
  repointing one is a persisted, synced settings change. The tools are local and provider-agnostic,
  so this arm tests the model's willingness to call them, not the feature.
