# Plan — Per-message suggestion chips

**Goal:** after every finished assistant turn, render 2–4 follow-up
suggestion pills below the toolbar. Clicking a pill fills the composer
with that text and sends it.

**Surface (already built):**
- `AssistantMessage.Suggestions` (`ObservableCollection<string>`) + `HasSuggestions`.
- `PiaSuggestionChips.ItemsSource` + `ItemClickCommand` DPs.
- `PiaAssistantMessage.SuggestionCommand` DP (forwarded to chips).

**Why:** discoverability + reduced typing. First-time users learn the
surface area; returning users skip the next obvious step.

---

## Step 1. LLM follow-ups (better quality, adds cost)

After streaming completes, fire a small completion against the **same**
provider with:

> "Given the user's last message and your reply, propose 3 short
> follow-up questions the user is most likely to ask next. Reply as a
> JSON array of strings. No prose."

Cache the result on the message; do not regenerate on reload.

Skip entirely if `provider.SupportsStreaming == false` to avoid double
network round trips on slow paths.

---

## Step 2 — Wire the producer into the turn lifecycle

`AssistantViewModel.ExecuteSendMessage` orchestrates the streaming.
Hook **after** the final token is yielded and `IsStreaming = false`:

```csharp
var picks = _suggestionService.Suggest(currentMessage); // or await for LLM
foreach (var s in picks) currentMessage.Suggestions.Add(s);
```

`HasSuggestions` already fires `PropertyChanged` because the collection
raises `CollectionChanged` and `AssistantMessage` re-emits it.

---

## Step 3 — Wire the click command

`AssistantView.xaml` does not currently set `SuggestionCommand` on
`PiaAssistantMessage`. Add a `UseFollowupCommand` to `AssistantViewModel`:

```csharp
public IRelayCommand<string> UseFollowupCommand { get; }
// ctor
UseFollowupCommand = new RelayCommand<string>(s =>
{
    if (string.IsNullOrWhiteSpace(s)) return;
    InputText = s;
    SendMessageCommand.Execute(null);
});
```

In `AssistantView.xaml` where `PiaAssistantMessage` is instantiated:

```xml
SuggestionCommand="{Binding DataContext.UseFollowupCommand,
                    RelativeSource={RelativeSource AncestorType=UserControl}}"
```
---

## Step 4 — Make optional in settings

In the assistant view settings this can be turned off. Default is on

---

## Acceptance

- Ask Pia anything. After the turn ends, 2–4 pills appear under the toolbar.
- Click a pill: composer briefly shows the text, then sends; a new
  assistant turn starts.
- Cancel a turn mid-stream: no pills appear.
- Re-open the conversation later: pills are restored if persisted; if
  not persisted, that's fine (out of scope here).

## Risks / notes

- For LLM path, beware of streaming cancellation: if the user cancels
  the main turn, also cancel the follow-up call.
- Persist `Suggestions` only if you persist chat history. If history is
  ephemeral today, leave it ephemeral; don't add storage just for chips.
- Logging: suggestion strings are user-adjacent content. Log via
  `SensitiveDebug`, never at Information level.
