# UI how-to questions for Pia

Companion to `2026-08-16-first-run-user-questions.md`. That doc was derived by reading Settings
labels without acting; this one comes from **driving the flows** in Pia v1.3.0.0 on 2026-08-16 —
sending chat messages, creating a persona, creating an Optimize template, and writing vault data.

Questions are deliberately left **unanswered**. This list feeds a documentation-completeness check
in a later session: if the answers were inline, the reviewer couldn't tell whether the *docs*
actually cover them. Each entry names the UI surface the behaviour lives on, nothing more.

Bias of this pass: entry-point questions ("where is the Add button") are cheap and mostly already
covered. The valuable questions are the ones that only appear *mid-flow* — what a field means, what
is required, what happens on save, and where the thing shows up afterwards.

---

## Chat composer (Assistant view)

The composer row is: clear, record, attach, join-meeting, live-transcription, persona picker,
Chat/Agent toggle, send.

- What is the difference between the **Chat** and **Agent** toggle, and what changes when I switch?
- Can I switch a conversation from Chat to Agent halfway through, or do I have to start over?
- What does the persona dropdown in the composer change — just the wording, or the model too?
- The persona pill shows a coloured dot next to the name. What does the dot mean?
- What does the eraser button do — clear the message I'm typing, or delete the whole conversation?
- What is the difference between the **Record** button and the **Live transcription** button?
- What is the difference between **Join a meeting and transcribe** and **Live transcription with
  consent**? Both look like meeting buttons.
- What file types can I attach, and how large can they be?
- Does an attached file get sent to the AI provider, or is it only read locally?
- How do I send a multi-line message without submitting it? (Enter submits.)
- Typing `@` in the message box does something — what? Nothing on screen suggests it exists.
  (See the note at the end of this file; this is still unresolved.)
- The chat gets a title from my first message even with auto-titling off — can I rename it?
- What does the **Running** badge on the chat chip mean, and can I keep typing while it runs?
- How do I stop a reply that's already generating?
- The chip says "Switch between recent chats" — how is that different from the **Chat history** view?

## Assistant view — general

- What is the notification bell with a number on the right edge of the window, and how do I open it?
- What is the panel that slides out from the right edge?
- What does **Open new window** give me that tabs or chats don't? Can two windows use different
  personas at once?
- The bottom of the sidebar says **Light** — is that a theme switch, and is there a system-follow
  option?
- What does the orange **End-to-end encryption setup required to sync your data** banner block? Can
  I keep using Pia and set it up later?
- Can I dismiss that banner permanently?

## Personas (Settings → Assistant → Personas)

Walked end to end: **Add Persona** → the *Edit Persona* dialog → **Save**. The dialog's full field
list is: Describe this persona (+ *Draft with AI*), Name\*, Tagline, System Prompt\*, Guardrails,
Output Format, Archetype, Model type, Tool Access, Expertise, Emoji, Accent Colour, Preferred
Provider, Reasoning Effort.

### Entry points

- Where do I add a new persona?
- Why do some personas only offer **Duplicate** while others also show edit and delete?
- Can I edit or delete a built-in persona, or do I have to duplicate it first?
- Can I get a built-in persona back after I've changed it?
- Is there a way to share or export a persona, or move one to another machine?
- Can I reorder the persona grid, or set which persona is the default for new chats?

### Inside the Edit Persona dialog

- Only **Name** and **System Prompt** are marked required. What happens to the persona if I leave
  everything else blank?
- What is the difference between **System Prompt**, **Guardrails** and **Output Format**? All three
  look like "instructions to the model".
- In what order are those three combined, and can a Guardrail override the System Prompt?
- What does **Describe this persona** actually do — is it saved anywhere, or is it only input for
  *Draft with AI*?
- What does **Draft with AI** fill in, does it overwrite fields I've already typed, and which
  provider pays for that call?
- What is an **Archetype** and what do the six choices change? Mine defaulted to `custom`.
- What is **Model type** (`general` by default) and how does it differ from **Preferred Provider**?
- **Tool Access** on a persona has three settings and defaults to `Full`. How does that interact
  with Settings → Assistant → **Tool access**? Which one wins if they disagree?
- Can I use a persona to give one assistant fewer tools than another — is that the intended way?
- What is **Expertise** used for? Is it injected into the prompt or just a label?
- What does **Reasoning Effort** cost me, and what happens if my provider doesn't support it?
- If I set a **Preferred Provider** on a persona, does it override "Same for all modes" under
  Providers?
- What are the **Emoji** and **Accent Colour** toggles for? My saved persona shows neither an icon
  nor a coloured dot, unlike every built-in one.
- The coloured dot appears next to the persona name in the grid *and* on the composer pill — is it
  purely decorative or does it encode something?
- Is there any validation or preview before I save? Saving closed the dialog with no confirmation.
- How do I test a persona without making it my active one?

### After saving

- Where does my new persona show up — is it automatically in the composer dropdown?
- Does switching persona mid-conversation re-run the earlier turns, or only affect new messages?
- Do personas sync to my other devices, and does that need the encryption setup finished first?

## Optimize templates (Settings → Optimize)

Walked end to end: **Add Template** → the *Edit Template* dialog → **Save**. Fields are: Template
Name\*, Style Description\*, a *Generate Prompt* button, and Generated Prompt.

- Where do I create my own rewrite/optimize template?
- What *is* Optimize, and how is it different from just asking Pia in a chat?
- **Style Description** and **Generated Prompt** are two separate boxes. Which one is actually sent
  to the model when I run the template?
- What does **Generate Prompt** do, which provider does it call, and does it cost me tokens?
- Do I have to press **Generate Prompt**, or can I write the prompt myself? The hint says "You can
  edit the generated prompt manually", which suggests I can.
- **Generated Prompt is required to save, but isn't marked `*` like Name and Style Description.**
  You only find out by pressing Save and reading the error. Which fields are genuinely required?
- Why did my template card come out blank underneath the name, while the built-in ones show a
  description line? Is there a description field I missed?
- What does **Set Default** do, and where does the default template get used?
- Which template is used by the Optimize hotkey if I never set a default?
- What does **View Prompt** show me, and can I copy a built-in's prompt as a starting point?
- What does **Output Action** (Copy to Clipboard) control — where else can the result go?
- Can I use my optimize templates from inside an Assistant chat, or only in Optimize mode?
- Do templates sync between devices?
- Can I reorder templates or group them?

## Memory / vault (sidebar → Memory)

Opened and read. The view shows a header count ("48 objects · 52,6 KB"), a search box, a left column
of categorised entries, a right-hand **Vault at a glance** panel with a composition bar and a
**Source documents** list, and a footer reading **Embedding Model: Ready · text-embedding-3-small ·
384-dim** with a **Regenerate Embeddings** link.

- What is the difference between "memory" and the "vault"? Settings names an *Assistant files
  folder* and a *Memory vault* path underneath it, and this view calls things "objects".
- What is an "object" here, and is 48 objects a lot? Should I be pruning?
- Can I add or edit an entry from this view, or only by talking to Pia?
- How do I delete one thing Pia remembers without wiping everything?
- Where do the categories (Personal Profile, Preferences, People, Organizations, Products, Concepts,
  Regulations, Technology) come from — are they fixed, or can I add my own?
- What do the **PROFILE**, **PREFERENCE** and **NOTE** tags on entries mean, and can I change one?
- Products is 35% and Technology 29% of my vault. Is that composition bar telling me something I'm
  supposed to act on?
- What are **Source documents**, and how does a file get into the sources folder?
- What does "Compiled into 31 topic page(s)" mean — what is a topic page and can I read one?
- If I edit a source file in Explorer, does Pia notice and recompile?
- What does **Regenerate Embeddings** do, how long does it take, and when would I need it?
- What is the embedding model for, and what happens to my vault if I change it?
- The header has back / home / refresh / open-folder / help buttons — what does each one do?
- Does anything in this view get sent to the AI provider, and does the encryption banner affect it?

## Tool approval (in the Assistant chat)

Exercised: a chat message triggered a `create_todo` call, which raised an inline approval card
reading *"Create Todo — Create medium priority todo: Winwright walkthrough test"* with four buttons
(**Decline**, **Allow once**, **Allow this session**, **Always allow**) and an expander chevron. The
chat chip and the Chat history row both showed an orange **Waiting for confirmation** badge.

- What is the difference between **Allow once**, **Allow this session** and **Always allow**, and
  how long does each one last?
- What exactly does **Always allow** cover — this specific todo, the `create_todo` tool, or every
  todo operation?
- What does the chevron on the approval card expand to show? Can I see the exact arguments before
  I approve?
- If I **Decline**, does Pia try something else, or does the whole turn stop?
- What happens if I leave an approval unanswered and go do something else? (Here the turn
  eventually failed with a provider timeout — see note 3.)
- The chat chip says **Waiting for confirmation** — can I get back to a waiting chat from anywhere,
  or do I have to find it in Chat history?
- Do I get a notification when Pia is waiting on me, or only if I'm looking at the chat?
- Why did some tools in this session run without asking while this one asked?
- Where do I see everything I've already approved, and how do I take an approval back?
- If a persona sets its own **Tool Access**, and Settings → Assistant → Tool access sets another,
  which applies?

## Surfaces visible from the shell but not opened in this pass

These are labels a user sees on screen. Listing the questions the *label alone* provokes; the
behaviour behind them was not exercised here.

### Tool access and approval prompts

### Agent runs (Settings → Assistant → Agent runs)

- What is an agent run, and how is it different from the composer's **Agent** toggle?
- Where do I watch a run in progress, and what happens if I close the window mid-run?

### Meeting (Settings → Assistant → Meeting, marked *Beta*)

- What does the **Beta** badge mean for my data here?
- Do meeting participants get told that Pia is recording?

### Other shell surfaces

- **Chat history**, **Reminders**, **Todo** each have a sidebar entry — can I create items there, or
  are they read-only views of what chat produced?
- What is **Account** for if I'm not signed in to anything yet?
- What are **Plugins** and can I add one that isn't in the list?

---

## Notes for whoever runs the documentation check

Three things worth carrying into the docs review, all established by driving the UI rather than
reading labels:

1. **The Optimize template dialog has a required field that isn't marked as one.** Template Name
   and Style Description carry a `*`; **Generated Prompt does not, but Save refuses to complete
   without it.** The app does explain itself — pressing Save raises a modal reading *"Generated
   prompt is required. Describe your style and click 'Generate Prompt'."* (`TemplateEditContentDialog`
   validates on close). So this is a labelling gap, not a silent failure: the user learns the rule
   only after being refused. Docs on creating a template should state up front that all three boxes
   must be filled.

   Minor related observation: the error is a native `MessageBox`, and repeated failed saves
   **stack** additional boxes rather than replacing the existing one — two failed attempts left two
   modals queued on top of each other.

2. **The `@` picker in the composer is still unconfirmed.** The prior doc flagged it as never
   rendering; this pass did not resolve it either, but narrowed it:
   - The behaviour *is* wired up in `AssistantView.xaml`
     (`AtCommandAutocompleteBehavior.IsEnabled/PopupControl/AutocompleteService`).
   - The first-tier suggestion list is hardcoded and non-empty, so "the profile has no data" does
     not explain an empty popup.
   - Typing `@` and `@T`, via both synthetic text entry and real key events, produced no popup
     window and no expanded popup element.
   - Automation remains the most plausible culprit. The window reported `isFocused: false`
     throughout, and the same session saw Enter-to-send fail and physical clicks on sidebar items
     do nothing — both consistent with synthetic input not reaching a non-foreground window. A
     `StaysOpen=False` popup would close instantly under exactly that condition.

   **This needs one human check with the window actually focused:** type `@` in the message box and
   see whether a picker appears. Until someone does that, don't write `@` into user docs and don't
   file it as a bug.

3. **A tool approval left pending long enough dies of a provider timeout.** The approval UI itself
   is good and needs no criticism — see the correction below. What's worth documenting is the
   failure mode: the `create_todo` approval in this pass sat unanswered for roughly an hour, and
   when it was finally answered the turn did not resume. It ended with *"Provider 'Pia Cloud' did
   not respond within 300 seconds."* and the chat chip flipped to a red **Error**. So a pending
   approval doesn't expire on its own, but the upstream connection behind it does, and the work is
   lost either way. Docs should say that an approval is not something you can leave until tomorrow,
   and what to do once a turn has failed this way (Regenerate is offered).

### Coverage of this pass

Driven for real: the chat composer, a full chat turn including a tool-approval round, Settings →
Assistant → Personas (full create flow), Settings → Optimize (full create flow), Settings → General,
Settings → Assistant → General, the Memory/vault view, and Chat history.

Not reached: Reminders, Todo, Tool access, Agent runs, Meeting, Account, Plugins.

The sidebar appearing to "stop responding" mid-pass was an automation artifact with two causes, both
since diagnosed — a stacked native modal blocking input, and sidebar items that ignore synthetic
clicks entirely. Neither affects a human user. Details and suggested fixes are in
`docs/ui_automation/2026-08-16-ui-automation-gaps.md`.

Test artifacts left in the profile (safe to delete): persona **"Winwright Test"** and Optimize
template **"Ww Test Template"**. The todo was *not* created — that tool call is still awaiting
approval, so nothing was written. Chat session `7f807a05` is parked in `WaitingForTool`.
