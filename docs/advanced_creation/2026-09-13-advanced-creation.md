# Advanced Creation: an interview overlay for templates, routines and personas

**Status:** Proposed — not started
**Owner:** Marco Altmann
**Written:** 2026-09-13
**Origin:** Owner request, 2026-09-13: an "advanced creation" mode beside the existing
one-textfield creation, where the user still starts from a single prompt but the model
then brainstorms the design with them — asking for sample inputs and whatever else the
use case needs — with the same look and feel for all three subjects. Sibling plan:
[2026-09-13-templates-editor-parity.md](2026-09-13-templates-editor-parity.md).

## Why

All three subjects already have a one-shot assist: describe it in a sentence, press the
Sparkle button, get a filled-in draft.

| Subject | Entry | Service call | Result |
|---|---|---|---|
| Optimize template | `Views/Dialogs/TemplateEditContentDialog.xaml` | `GeneratePromptAsync` | a prompt string |
| Persona | `PersonasView.xaml:319` → `PersonaEditModel.GenerateDraftAsync` (`:190`) | `GeneratePersonaDraftAsync` | `PersonaDraft` |
| Routine | `RoutinesView.xaml:619` → `RoutinesViewModel.GenerateDraftAsync` (`:1078`) | `GenerateRoutineDraftAsync` | `RoutineDraft` |

One shot is the ceiling: the model gets one sentence and has to guess tone, scope,
output shape, and — for templates especially — what the input actually looks like. A
user who cannot write that sentence well gets a bad draft and no way to say why.

Advanced Creation keeps the one-shot path untouched and adds a second door: the same
opening sentence, then a bounded back-and-forth in which the model asks the two or three
things it actually needs, and only then produces the draft. The draft lands in the same
editor, through the same apply path, as the one-shot button.

## Host: extend the overlay, not the first-run wizard

Three candidates were considered.

**`FirstRunWizardWindow`** — rejected. It is a separate `ui:FluentWindow` (750×800,
`ResizeMode="NoResize"`) whose progress dots are hand-written `Ellipse` elements with the
step index baked into a `DataTrigger` per dot (`FirstRunWizardWindow.xaml:48-120`). An
interview has an unknown number of turns, so there is nothing to draw dots for, and a
second top-level window means the editor underneath is not visible while you design
against it.

**`ui:ContentDialog`** — rejected. It is what templates use today and what the parity
plan removes. It is also sized for a form, not a transcript, and its button row belongs
to the dialog rather than to the turn.

**`DialogOverlayHost` + `OverlayDialogPanel`** — chosen. It is already mounted app-wide at
`MainWindow.xaml:196` with `Panel.ZIndex="20"`, above `ui:ContentDialogHost`, so one panel
type serves every entry point; it already does the backdrop, the show/hide animation,
focus save-and-restore and Escape routing; and `PolicyRestartOverlayPanel` is a working
subclass to copy. `Views/Dialogs/Overlay/` is where it goes.

### Four gaps in the overlay that this change has to close first

The panel as it stands cannot host an interview. All four are in
`Resources/Styles/OverlayDialog.xaml` and `Views/Controls/OverlayDialogPanel.cs`:

1. **No height cap and no scrolling.** The template body is a bare `StackPanel` inside a
   `Border` with `MaxWidth="{TemplateBinding MaxPanelWidth}"` and `MinWidth="320"` — no
   `MaxHeight`, no `ScrollViewer`. A six-turn transcript grows past the window. Add a
   `MaxPanelHeight` dependency property (default matching today's implicit behaviour) and
   wrap the `ContentPresenter` in a `ScrollViewer`.
2. **The templated buttons close the dialog.** `OnApplyTemplate` wires
   `PART_PrimaryButton` to `RaiseResultChosen`, which completes the `TaskCompletionSource`
   in `DialogOverlayHost.ShowAsync` and tears the panel down. Per-turn *Send* / *Skip*
   must therefore be ordinary buttons inside the content. Reserve the templated Primary
   for "Use this draft", enabled only once the model reports `done`, and Close for cancel.
3. **The host is single-slot.** `DialogContentPresenter.Content = panel` holds exactly one
   panel. Calling `IDialogService.ShowMessage` mid-interview would replace the panel and
   orphan its `TaskCompletionSource`. Confirmations and errors must render *inside* the
   panel — never through `IDialogService`.
4. **Escape discards silently.** `OnEscapePressed` is `virtual` and raises `Close`.
   Override it: once at least one answer has been given, the first Escape shows an inline
   "discard this design?" confirmation rather than closing.

A fifth is not a gap but a trap `PolicyRestartOverlayPanel` already documents: the app-level
style is implicit and keys on the exact type, so a subclass that does not set
`Style="{StaticResource {x:Type controls:OverlayDialogPanel}}"` renders untemplated.

## Protocol: a structured envelope, not free chat

Free-form chat would give three different-looking conversations and an unparseable
result. Each turn the model returns exactly one JSON object:

```json
{
  "state": "ask",
  "summary": "A weekly competitor digest, formal tone, for a German-speaking team.",
  "questions": [
    { "id": "sample", "kind": "sample", "label": "Paste a mail you'd want rewritten",
      "help": "One real example teaches more than a description.", "optional": true },
    { "id": "tone", "kind": "choice", "label": "How formal?",
      "options": ["Formal (Sie)", "Neutral", "Casual (du)"], "optional": false }
  ]
}
```

and, when it has enough:

```json
{ "state": "done", "summary": "…", "draft": { …subject-specific keys… } }
```

- `kind` ∈ `text` · `longtext` · `choice` · `multichoice` · `sample`. One
  `DataTemplateSelector` over `kind` renders every question in every mode — that is what
  makes the look and feel identical rather than merely similar.
- At most three questions per turn, so the panel stays a conversation and not a form.
- The app replies with a user message carrying `{"answers": {"tone": "…"}, "skipped": ["sample"]}`.
- **Hard cap of six ask-turns.** On the sixth the app sends "produce the final draft now".
  Without it a model that never converges bills the user indefinitely.
- `draft` uses the *exact* key list the existing one-shot prompts already specify, so the
  terminal turn reuses `ParsePersonaDraft` / `ParseRoutineDraft`
  (`TextOptimizationService.cs:260,296`) unchanged.

## Mode descriptor

One record supplies everything that differs between the three subjects, so the panel, the
engine and the templates stay subject-agnostic:

```csharp
public sealed record AdvancedCreationMode(
    string SubjectPreamble,     // what is being designed, lifted from the one-shot prompt
    string DraftKeysBlock,      // the "- key: meaning" list the one-shot prompt already has
    string? ExtraContext,       // routines: the offered tool list; null otherwise
    string TitleKey,
    string SubtitleKey);
```

Three instances live next to the service. `ExtraContext` is where routines pass
`RoutinesViewModel.OfferableDraftTools()` (`:1160`), so the model still picks from tools
this device actually has.

## Reuse that is not optional

The interview result must flow through the **existing** apply paths, not around them:

- **Routines.** `AcceptableDraftTools()` (`:1185`) drops any tool the local catalog does
  not offer, and `ApplyWebSearchGuard()` (`:1214`) appends the guard clause when
  `NeedsWebSearch`. Skipping either lets an invented tool name reach a stored grant, or
  ships a routine whose provider cannot search and answers from memory instead.
- **Personas.** The draft goes through `PersonaEditModel` so the same field-level
  handling applies.
- **Templates.** There is no `TemplateDraft` record today —
  `GeneratePromptAsync` returns a bare string. Add one (`Name`, `Description`,
  `StyleDescription`, `Prompt`) and a `ParseTemplateDraft`, mirroring the other two, so
  all three modes terminate the same way.
- **Empty-stream retry.** `GenerateRoutineDraftAsync` retries once because "the stream
  comes back empty often enough to look like a dead button: an upstream error frame is
  dropped rather than thrown". The same applies per interview turn.
- **Streaming path.** Keep `_aiClientService.GetChatCompletionWithToolsAsync` with the
  "You produce only the requested output" system message. Pia Cloud's `/api/ai/chat` only
  returns the expected shape on the streaming path.

## Service surface

A new `IAdvancedCreationService` rather than more methods on `ITextOptimizationService`,
which is already carrying four unrelated generators:

```csharp
Task<AdvancedCreationTurn> StartAsync(AdvancedCreationMode mode, string opening,
    Guid? providerId = null, CancellationToken ct = default);

Task<AdvancedCreationTurn> AnswerAsync(AdvancedCreationSession session,
    IReadOnlyDictionary<string, string> answers, IReadOnlyList<string> skipped,
    CancellationToken ct = default);
```

`AdvancedCreationSession` holds the running `ChatMessage` list and the turn count.
`providerId` comes from whatever the calling editor has selected, matching the one-shot
buttons.

## Entry points

A secondary button beside each existing Sparkle button — *Advanced…* — never replacing
it. The user asked for it "aside of the existing one textfield creation".

- Routines: `RoutinesView.xaml:619`, next to `Routines_Draft_Generate`
- Personas: `PersonasView.xaml:319`, next to `PersonaEdit_GenerateDraft`
- Templates: the new editor pane from the parity plan, next to the generate-prompt button

All three call one `IAdvancedCreationLauncher.LaunchAsync(mode)`, which resolves the
overlay host from `IDialogOverlayService`, shows the panel, and returns the raw draft JSON
for the caller to apply through its own path.

## Privacy

Every opening sentence, every question label, every answer and every draft payload is
user content. Log with `SensitiveDebug` / `SensitiveTrace` from `Pia.Logging`; at
non-sensitive level log only turn index, question count, `state`, and whether the cap was
hit. No provider URL without `SafeUrl.Format`.

## Testing

`dotnet test` is not the gate — the built exe is
(`tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe`).

- Envelope parser tests mirroring `tests/Pia.Wpf.Tests/Services/RoutineDraftFailureTests.cs`:
  empty stream, non-JSON prose, JSON in a code fence, unknown `kind`, `state:"done"` with
  no `draft`, `state:"ask"` with no questions, more than three questions.
- Turn-cap test: a model that always answers `ask` terminates with a draft request.
- Routine tool filtering through the interview path, asserting an invented tool name is
  dropped — the `AcceptableDraftTools` case, re-asserted here because it is the one that
  can reach a stored grant.
- `ViewAutomationIdTests` row for the panel. It can take one: `Take` builds the type with
  `Activator.CreateInstance` and walks the logical tree plus declared `DataTemplate`s, so
  the panel's *content* — including the question `ItemsControl` templates — is surveyed.
  What it does **not** see is the `ControlTemplate` in `OverlayDialog.xaml`, which is
  never applied — but its three buttons need no id of their own. They carry no
  `AutomationProperties.AutomationId`, so UIA falls back to the template `x:Name` and they
  are already reachable as `PART_PrimaryButton` / `PART_SecondaryButton` /
  `PART_CloseButton`, which the playbook documents and `PolicyRestartOverlayPanelTests`
  asserts. Adding an explicit id would shadow those and break both.
- A `tests/ui-scripts/` recording is optional and outside the gate.

## Open questions

Carried as decision gates in
[2026-09-13-advanced-creation-checklist.md](2026-09-13-advanced-creation-checklist.md).
