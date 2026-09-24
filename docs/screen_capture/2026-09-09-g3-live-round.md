# G3 — the live Pia Cloud round

**Status.** **G3 is ANSWERED: yes.** Closed 2026-09-09 on the second run, once the local Pia Cloud
deployment gained an image-capable endpoint. The first run, below, never reached the question — its
request died at the server's model router — and is kept because the distinction is the finding.
**Owner.** Marco Altmann
**Written.** 2026-09-09
**Origin.** The G3 gate of [2026-09-07-screen-vision-checklist.md](2026-09-07-screen-vision-checklist.md).

## What G3 asks

Does Pia Cloud accept a `ChatRole.User` message carrying a `DataContent` interleaved between a tool
result and the next round, and does the model actually attend to it?

## What happened

A Debug build of `feature/screen_capture` was launched against the real profile. Notepad held one
nonce sentence chosen to survive the PII tokenizer (no digits, no date shapes). The prompt was
"Look at the Notepad window on my screen and tell me the exact sentence it contains."

The model drove the feature exactly as designed, unprompted about how:

1. **Round 1** — called `screen_list_targets()` with no arguments. It returned 1 monitor and
   9 windows.
2. **Round 2** — called `screen_capture(target: "window", match: "notepad")`. `match` resolved to
   exactly one target out of nine windows and the handler proposed rather than captured.
3. The interactive approval card appeared. "Allow once" was pressed, the capture ran
   (`Captured Window notepad 1533x716 in 22 ms`), and the handler returned its text marker:
   *"Captured window notepad at 1533x716. The picture is attached as the next message; read it from
   there."*
4. **The drain fired in the right place** — `Round 2: 1 tool image(s) appended for the next request`,
   logged after the round's last tool result and before `Round 2 complete`.
5. **Round 3 carried it** — `Round 3: request carries 1 image message(s)`.
6. `POST /api/ai/chat` returned **200** and opened the SSE stream, then the stream carried an error
   frame:

```json
{"error":{"message":"No endpoints found that support image input","code":404,
  "metadata":{"routing_funnel":[{"step":"Initial Endpoints","endpoint_count":29}],
  "failed_routing_step":"Filter by Image Support"}}}
```

## Why this does not answer G3

The router rejected the request while **choosing a model**, at the step named
`Filter by Image Support`: it started with 29 candidate endpoints and none of them accept image
input. The message *sequence* was therefore never judged. A tool-result-then-user-image ordering
that the provider dislikes would fail at validation with a shape complaint, not at endpoint
selection with a capability complaint.

So the three outcomes G3 could have had — provider rejects the shape, model cannot see, model sees
and ignores — resolve to the second, and for an environmental reason: the provider in this profile
points at `https://localhost:8081`, the local development Pia Cloud server, whose endpoint
catalogue has no vision-capable model in it.

**G3 needs one more run against a deployment that has a vision endpoint.** Nothing in the client
needs to change first.

## What this run did prove

All of it live, from the Debug log and the profile on disk rather than from the reply on screen:

- The `screen` plugin is offered (`pluginId=10000000-0000-0000-0000-00000000000b`) and both tools
  reach their handler through `PluginService`.
- `match` resolution picked one window out of nine and refused nothing it should have kept.
- The gate produced an interactive card, and "Allow once" is what executed the capture.
- The drain lands after the round's tool results, not between them.
- **D3 safeguard 3 holds.** One metadata-only line was appended, title hashed:

```json
{"timestamp":"2026-09-09T06:36:28.9395627+00:00","surface":"interactive",
 "taskId":"fd452a4d-2bf2-4279-81f1-9c8490a7a370","granter":null,"targetKind":"window",
 "processName":"notepad","width":1533,"height":716,"titleHash":"e5628126ac16edbf"}
```

- **The bytes do not reach SQLite.** `history.db` and `history.db-wal` both hold zero occurrences of
  the base64 JPEG prefix `/9j/` after the turn. The workflow had only proven this by reading code.
- The composer button renders enabled from the German resx (`Bildschirm aufnehmen`) with Pia Cloud
  as the Assistant default, which is B2 and B4 on a real window.

**D2a was not exercised.** The placeholder swap runs once the following response is back, and no
response came back, so `consumed and replaced by placeholders` never logged. It stays unproven live.

## Three findings worth acting on

1. **D2's gate is necessary but not sufficient, and this is not limited to screen capture.** The
   design inherits the existing image gate: refuse unless the provider is `AiProviderType.PiaCloud`.
   This run shows a provider that *is* Pia Cloud and still cannot take an image, because whether an
   image can be sent depends on the **routed endpoint**, not the provider type. The pre-existing
   paste and drop paths have the same exposure — the failure would look identical there — so this
   is not a regression introduced by this branch, but the screen-capture button is a new and much
   more inviting way to reach it. A capture that is refused *after* the pixels are taken is exactly
   what D2 says must not happen.
2. **The error reaches the user as raw server JSON.** The chat bubble reads
   `Error: {"error":{"message":"No endpoints found that support image input","code":404,...}}`.
   That is the pre-existing error path, not this feature's doing, but a user who presses the new
   button on a text-only endpoint is who will read it.
3. **The action card's German title reads "Aufnehmen Bildschirm".** The button next to it correctly
   says "Bildschirm aufnehmen". The card composes verb and noun in the wrong order for German.

## To close G3

Point the Assistant provider at a Pia Cloud deployment with a vision endpoint, repeat the run above,
and confirm from the Debug log — not from the reply on screen — that:

- the round after the drain returns a response rather than an error frame,
- that response quotes the nonce sentence (this is the "attended to" half, and the only part that
  the log alone cannot tell you),
- `consumed and replaced by placeholders` logs afterwards, and
- the next round reports `request carries 0 image message(s)`.

## The run that closed it

Second run, 2026-09-09, same procedure, after the owner enabled image support on the local Pia Cloud
deployment. Nonce: *The copper walrus rehearses quiet arithmetic before dawn.* — different from the
first run's, and chosen again to survive the PII tokenizer.

The model drove it the same way unprompted: `screen_list_targets()`, then
`screen_capture(target: "window", match: "notepad")`, resolved to one target out of the open windows,
proposed rather than captured, and the interactive card was approved with "Allow once".

**Both halves of the gate now hold.**

*Accepted* — the transport chain, from the Debug log:

| step | line |
|---|---|
| the drain, after the round's last tool result | `Round 2: 1 tool image(s) appended for the next request` |
| the next request carried it | `Round 3: request carries 1 image message(s)` |
| the provider took it | `POST /api/ai/chat -> 200` in 8557 ms, **no error frame** |
| D2a swapped it once consumed | `Round 3: 1 tool image(s) consumed and replaced by placeholders` |
| the swap survives the turn | the next turn reports `carries 0` on rounds 1 and 2 |

*Attended to* — the only part no log can show. The reply quoted the sentence word for word:

> Der Notepad-Inhalt lautet, Wort für Wort:
> „The copper walrus rehearses quiet arithmetic before dawn."

Nothing but the injected picture carried that sentence, so the model read it.

Two things re-confirmed in the same run: `history.db` and its WAL still hold **zero** occurrences of
the base64 JPEG prefix `/9j/` after a *successful* round, not just a failed one; and the audit line
landed with the title hashed (`titleHash: 0f2eafba92f2a112`, `surface: interactive`).

**What this does not close.** The endpoint-capability gap stands: image support is a property of the
routed endpoint, so on a text-only endpoint the capture is still taken before the send is refused.
That is why the first run failed, and it remains in the checklist's not-yet-planned list. The refusal
is at least legible now — the server's sentence rather than its JSON envelope.
