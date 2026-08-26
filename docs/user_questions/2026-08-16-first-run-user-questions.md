# First-run questions for Pia

Grounded in a UI walkthrough of Pia v1.3.0.0 (Assistant mode) on 2026-08-16. Every view, setting
and default named below was read off the running app. Individual tool names come from the handler
switches in `src/Pia.Wpf/Services/*ToolHandler.cs` — code, not screen. Anything I could not confirm
either way is flagged inline as unverified.

Two readings of "questions a user could ask" are covered:

- **Part 1 — Prompts to type into Pia.** The main deliverable: what a new user would actually say
  in the chat box on day one.
- **Part 2 — Questions *about* Pia.** Things a new user wonders while poking at Settings, with the
  place in the UI that answers them.

---

## Part 1 — What to ask Pia on day one

### Getting oriented

Pia's suggestion chips on an empty chat already model this ("Remind me to pick up the package on
Friday", "Add \"buy groceries\" to my todo list", "Update my preferred language to French"), so
these are the natural next step up.

- "What can you actually do for me?"
- "What tools do you have access to right now?"
- "Which of my folders can you read and write?"
- "What do you already know about me?"
- "Are you allowed to change things without asking me first?"

### Memory — what Pia remembers about you

Pia keeps a persistent memory (`remember` / `recall` / `forget`) plus a structured vault you can
browse under **Memory** in the sidebar.

- "Remember that I prefer short, direct answers with no preamble."
- "Remember my partner's name is Sam and their birthday is 12 March."
- "What do you remember about my work preferences?"
- "Forget everything you stored about my old job."
- "Show me everything you've saved about people."
- "Update my personal profile — I moved to Berlin."

### Your knowledge vault — documents Pia has read

Drop files into the vault's `sources` folder and Pia compiles them into recallable topic pages
(`ingest`, `browse_index`, `read_topic`, `read_source`, `create_source`, `update_source`). The
**Memory** view shows the source documents and how many topic pages each produced.

- "What documents do you have in my vault?"
- "Give me an index of the topics you know about."
- "Summarise the business plan I gave you."
- "What does my vault say about pricing?"
- "Ingest the new file I put in the sources folder."
- "Which of my notes mention Anthropic?"

### Todos

Kanban-style board with columns, priorities and due dates (`create_todo`, `query_todos`,
`complete_todo`, `update_todo`, `move_todo`, `delete_todo`, `list_columns`).

- "Add 'call the dentist' to my todo list."
- "What's overdue?"
- "What's on my list for this week?"
- "Mark 'buy groceries' as done."
- "Move 'review the visual refresh' to the First column."
- "Set the 'call mum' todo to high priority and due Friday."
- "Create a column called 'Waiting on others'."

### Reminders

Time-based, with snooze/dismiss states (`create_reminder`, `query_reminders`, `update_reminder`,
`delete_reminder`). The **Reminders** view is read-only — it says outright that reminders are
created via the Assistant chat.

- "Remind me to send the invoice tomorrow at 9."
- "Remind me every Monday morning to review my week."
- "What reminders do I have coming up?"
- "Snooze the invoice reminder until Thursday."
- "Cancel the reminder about the package."

### Files in your assistant folder

Pia is sandboxed to the assistant files folder (`C:\Users\<you>\Documents\Pia Assistant` by
default) and cannot reach outside it (`list_files`, `read_file`, `write_file`, `search_files`,
`delete_file`).

- "What files do I have in my Playground folder?"
- "Read my meeting notes from last week and pull out the decisions."
- "Write a draft project brief to `brief.md`."
- "Search my files for anything mentioning 'Q3 budget'."
- "Turn these bullet points into a proper document and save it."

### Git

Read and write git operations, restricted to the same sandbox folder (`git_status`, `git_log`,
`git_diff`, `git_branch`, `git_show`, `git_init`, `git_add`, `git_commit`, `git_switch`,
`git_restore`, `git_stash`).

- "Is there a git repo in this folder? What's the status?"
- "Show me what changed since yesterday."
- "Initialise a repo here and commit what I've got."
- "Commit these notes with a sensible message."
- "What did I change in the last three commits?"

### Meetings and voice

The composer has buttons for **Record**, **Attach file**, **Join a meeting and transcribe**, and
**Live transcription with consent**. Speaker separation and smart speaker detection are on by
default; the meeting browser joins hidden unless you show it.

- "Join this Teams meeting and take notes." (paste the link)
- "Start transcribing this conversation." (in-person, with consent)
- "Summarise the meeting you just transcribed."
- "Who said what about the deadline?"
- "Pull the action items out of that transcript and add them to my todos."
- "Save that meeting summary to my vault."

### Agent mode and background work

The composer has a **Chat / Agent** toggle. Agent runs are multi-step, bounded by the limits under
Settings → Assistant → Agent runs (24 steps, 10 tool rounds per step, 20 min, 2 re-plans by
default), and the planner can hand individual steps to different personas.

- "Research our three biggest competitors and write me a one-page comparison." (Agent)
- "Go through my vault, find everything about the product launch, and draft a status update."
- "Run that in the background and tell me when it's done."
- "Every Monday at 8, check for news about our industry and write me a summary."
- "What scheduled jobs do I have?"
- "Stop the run that's going."

### Personas and writing help

Ten personas ship or exist in this profile (Pia · Personal, Pia · Business, Experienced Coder,
Marketing Writer, Financial Expert, Worldwide Company CEO, Explain It Simply, plus custom ones).
Optimize mode (Ctrl+Alt+O) rewrites selected text with a template.

- "Explain this like I'm not technical." (Explain It Simply)
- "Rewrite this email so it sounds professional."
- "Give me the risk-aware read on these numbers." (Financial Expert)
- "Write launch copy for this in our brand voice." (Marketing Writer)
- "Review this C# and tell me what's wrong with it." (Experienced Coder)

### Using `@` to point Pia at something

Typing `@` in the composer is meant to open a picker that tags a specific item into the turn. Per
`AutocompleteService.cs` the domains are `@Memory`, `@Todo`, `@Reminder` and `@Routine` (which
resolves to scheduled jobs, and still accepts the older `@Research` spelling without offering it),
plus `@Files` and `@Assignment` — those two only when a sandbox folder is configured, or when the
Pia server offers background assignments, because tagging either restricts the turn to that
domain's tools.

**Unverified:** the picker did not render during this walkthrough under either synthetic text entry
or real keystrokes, so the list above is from the source, not from the screen. Worth a manual check
before putting it in user-facing docs.

- "Update @Todo:\"Call mum\" — push it to next week."
- "What's in @Files:notes.md?"
- "Cross-check @Memory against what I just told you."

---

## Part 2 — Questions about Pia itself

Grouped by where the answer lives.

### Getting started

- "Why is there an orange banner about end-to-end encryption?" → Settings → **Account**. You either
  approve this device from another device that already has Pia, or enter your recovery code. Until
  then your data doesn't sync.
- "Pia isn't in my taskbar — where did it go?" → It lives in the system tray. Press **Ctrl+Alt+P**
  for Assistant, **Ctrl+Alt+O** for Optimize.
- "Can I change the hotkeys?" → Settings → General → **Hotkeys**. There's also an unset **Fast
  Path** hotkey that captures, optimizes and applies in one keystroke.
- "Can I run Pia in German or French?" → Settings → General → Application → **Interface Language**
  (EN/DE/FR). It auto-detects from Windows on first run.
- "Should Pia start with Windows?" → Settings → General → Application. **Launch at Windows startup**
  is on by default; **Start minimized to system tray** is off.

### Privacy and data

- "Does my personal data get sent to the AI provider?" → Settings → General → **Privacy**. PII
  tokenization is **on** by default: names, emails, phone numbers, addresses and dates are swapped
  for anonymous tokens before the request leaves your machine.
- "Can I mark my own words as private?" → Same tab, **Private Keywords** — add any word or phrase
  that should always be tokenized.
- "Are my chats stored anywhere?" → Settings → Assistant → General. **Save chat history** is on,
  with a 30-day retention slider and a **Delete all chat history now** button. The **Chat history**
  view has search, date-range and state filters.
- "How do I wipe everything?" → Settings → General → Application → **Reset & Restart** (settings,
  providers, history, memories — not undoable).

### Models and cost

- "Which AI is answering me?" → Settings → **Providers**. This profile has Pia Cloud (built-in),
  Mistral, OpenRouter, a local Ollama, and OpenAI. **Same for all modes** is ticked, so Assistant
  and Optimize share one default.
- "Can I run it fully local?" → Yes — add an Ollama provider pointing at `localhost` and make it
  the default. Speech-to-text and text-to-speech models also run on-device.
- "What costs me extra tokens?" → Two settings say so explicitly: **Show follow-up suggestions**
  ("adds a small extra request per turn") and **Auto-generate chat titles (uses tokens)**. Both are
  off in this profile.

### Permissions — what Pia can do unasked

- "Will Pia change my files without asking?" → Settings → Assistant → **Tool access**. Pia asks
  before any tool changes anything; you answer Allow once / Allow this session / Always allow.
- "What is Agent autonomy?" → Same tab. **Auto-approve Pia's own write tools during agent runs and
  in voice mode** is on. It covers notes, memory, todos, reminders, scheduled jobs and files —
  never deletions, git commands, or external MCP tools.
- "How do I take a permission back?" → Same tab, **Always-allowed tools** — revoke a grant and Pia
  prompts again on the next call.
- "Can Pia touch files outside its folder?" → No. It's restricted to the assistant files folder,
  and both file access and git access are individual toggles under Settings → Assistant → General.

### Speech and meetings

- "How do I talk to Pia instead of typing?" → The mic button in the composer. Engine choice is
  Settings → General → **Speech**: Whisper (broad language coverage) or Parakeet TDT v3 (faster,
  multilingual, ~340 MB). Models download on first use.
- "Can Pia read answers back to me?" → Yes — download a voice under Speech → **Text-to-Speech**,
  then set it active.
- "Will people see a browser window pop up when Pia joins my meeting?" → Not by default. Settings →
  Assistant → **Meeting** → *Show the meeting browser window* is off, so the browser joins hidden
  and silent.
- "Will Pia know who's speaking?" → **Identify individual speakers** and **Smart speaker detection**
  are both on; changes apply to the next meeting you join.

### Extending Pia

- "What are plugins?" → Settings → **Plugins**. Seven built-ins (files, git, ingest, memory,
  reminder, scheduled-research, todo) plus MCP servers — this profile has one called Pia Docs. Each
  has an on/off toggle.
- "Can I make my own persona?" → Settings → Assistant → **Personas** → *Add Persona*, or duplicate a
  built-in and edit it.
- "Can I make my own rewrite template?" → Settings → **Optimize** → *Add Template*.

---

## Notes for whoever turns this into onboarding

- The encryption banner is the loudest thing on screen on first launch and the doc set doesn't
  currently explain it. It's the single most likely first question.
- **Reminders** and **Chat history** are read-only views — a new user will try to add a reminder
  from the Reminders page. The empty state does say "Create reminders via the Assistant chat", but
  only once the list is empty.
- The **Chat / Agent** toggle in the composer is unlabelled beyond the two words. Nothing on the
  main screen explains what changes when you flip it.
- Nothing in the composer hints that `@` does anything, and I couldn't get the picker to appear
  under automation. Confirm by hand whether it works at all before documenting it for users.
