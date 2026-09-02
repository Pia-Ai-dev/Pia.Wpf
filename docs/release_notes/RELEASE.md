# Pia 1.4.5

Successor to 1.3.389, covering roughly six weeks of work.

## Since 1.4.0

- Dragging a card by its text works again. On the todo board and in the
  reorderable lists, grabbing a card on the words rather than the space around
  them did nothing at all.
- Routines has a Home button in its header that returns the pane to where it
  started, from anywhere — including the blueprint catalog, which nothing else
  closed.
- No ready-made routine blueprint starts before 08:00 any more. Several
  defaulted to a time before you are likely to be at the machine.
- The window title is "Pia AI Assistant" without the mode name after it, which
  read as a second, unrelated product sitting next to the assistant.
- On the live transcription start screen, the consent sentence participants
  have to speak now leads the screen instead of sitting below the fine print,
  large enough to read off the host's display.
- The Personas tab is a list beside a detail card instead of a modal dialog.
  Selecting a persona shows its system prompt, guardrails, output format,
  expertise, provider and reasoning effort without opening the editor at all,
  and Edit, Duplicate and Delete sit on that card. Editing happens in the same
  pane, the way Routines already worked, and the emoji and accent-colour
  pickers stay with their field as that pane scrolls.
- "New routine" no longer throws away an open editor. It discarded whatever you
  had typed on its way to the blueprint catalog; it is now refused while an
  editor is open.
- Scroll bars fade out while nothing is scrolling, and come back on a scroll or
  when the pointer reaches the thumb. The thin bar had been reading as part of
  the layout in the denser views.
- Status badges are a shade quieter: the pill's tint is dialled back without
  fading the text on it.

## Agent runs

- Ask for something larger than a single answer and Pia plans it, executes it
  step by step, and runs a final check over its own work. Progress is visible on
  a run panel: the current activity, every step and its state, and a read-only
  trace of the tools each step actually called.
- A run's first plan is held for you to read. On an interactive run of three
  steps or more, Pia shows the plan and waits for Approve or Reject before
  anything executes.
- Pause a run mid-flight, then edit, insert, reorder or skip the remaining steps
  and leave a note steering what happens next. Resuming picks up the amended
  plan; pausing a run that has fanned out parks its children instead of killing
  them.
- "Run in background" hands the work to a headless run that keeps going while
  you use the rest of the app, and reports back into the chat when it is done.
  If it produced nothing, it now says why.
- Each run works in its own isolated workspace — a git worktree where the folder
  is a repository, a bounded copy otherwise — so a run never edits your files in
  place. Finished work is promoted out of it; a failed run offers to publish
  what it got to.
- A step can fan out into child runs that execute on their own slot pool, each
  with a persona resolved for the step and grants narrower than the parent's.
- Long runs no longer fall off the end of the context window. Pia summarises
  earlier steps when it has to, counts image attachments against the budget
  rather than guessing, and says which run and step it shortened.
- A run that reaches its budget parks instead of stopping half-done, and offers
  a Continue you can press later. Parked runs survive a restart.

## Routines

- Scheduled work moved out of Settings into its own top-level Routines view.
- Twenty ready-made blueprints to start from, split into ones that work out of
  the box and ones that read your own data. Fill in the slots on a card rather
  than writing a job from a blank box.
- A routine can pin the persona and the reasoning effort it runs with.
- Recurring routines now fire on the day you picked, every time. Each routine
  keeps a run history you can read after the fact, can be fired by hand, and a
  missed run is offered rather than silently skipped.
- A failed save no longer throws away your edits.

## Meetings

- A routine can join a Teams meeting at a set time, and several scheduled
  meetings can run side by side.
- Meetings can be recorded unattended, with no browser window to watch.
- "Save to vault" on the transcript overlays writes the meeting into your memory
  vault with a details form; transcripts are filed under `sources/transcripts/`.
- Direct transcription captures microphone and system audio behind a spoken
  consent gate, for conversations that are not in a meeting app at all.
- Speaker labelling is steadier: the attendee roster caps how many speakers can
  be invented, a pass no longer mints speakers it can never correct, and an
  exported document will not name the same speaker twice. You can also take the
  transcript with no speaker labels at all.
- The bundled meeting browser is kept current, and superseded copies are cleaned
  off clients instead of piling up.

## Speech recognition

- Parakeet TDT v3 is the new default engine — faster on the languages it
  supports, about 340 MB on disk. Whisper is still there under Settings →
  General → Speech, with its model-size and language options, and remains the
  one to pick for broader language coverage.
- The engine underneath moved from Whisper.net to sherpa-onnx. Among other
  things, this fixes model loading for Windows accounts whose user name is not
  plain ASCII.

## Chats and memory

- Chat history moves in and out of Pia. Export your chats, import them back, and
  import an Open WebUI export directly.
- Pia can search and read your past chats when an answer needs them, and shows
  which chats and vault pages it read as chips under the answer. When a search
  finds nothing, it now says what it searched for.
- Open your memory vault in Obsidian from inside Pia, with an offer to register
  the vault the first time. Other tools' dot-folders no longer leak into the
  recall index.
- New vault tools let the assistant correct or stage a source document, and
  per-topic synthesis during ingest now runs in parallel.
- Pia can look inside the documents in your assistant folder. Searching the
  folder's contents now covers Word documents, Excel workbooks and saved
  Outlook mail, which were previously skipped as binary files; reading one
  shows the same extracted text. A file it cannot read at all — a PDF, an
  image — is named in the result, so an unsearchable document no longer looks
  like an absent answer. Saved mail is read-only: Pia will not write over a
  .msg or .eml.
- Answers can be exported to a file, and Pia asks where it should go.

## Privacy, security and administration

- One control surface for tool authority: allow a tool once, for this session,
  or always — the same rules on every surface, including voice mode and
  background runs. Unattended runs start with a deliberately narrow write grant.
- Enterprise policy is delivered per group from the server and merged with the
  local policy file. Changed policy applies live; only privacy settings ask for
  a restart. Providers, personas and meeting capture are all manageable this
  way, and personas published by an administrator arrive read-only.
- Sign-in hardening: the loopback callback is filtered on a nonce and redeems a
  one-time code instead of carrying tokens in the URL, and a login is no longer
  lost when the browser tab closes.
- Pia names itself as an AI system, marks generated output — exported files
  carry a machine-readable AI marking — and offers a feedback channel.
- Settings → Export diagnostics hands over the app's own logs, redacted. Logs
  are kept for a month, and rolled files ship with the export.
- A client that cannot decrypt a synced record now refuses the pull instead of
  writing blank rows over good data.

## Updates and hosting

- The update feed and the lazily-downloaded speech and detection models are
  served from Pia's own storage, with GitHub as a fallback for cached models. A
  deployment can point both somewhere else entirely.

## Fixes

- Reopening a chat left it invisible to screen readers and UI automation.
- Deleting the current chat left it on screen.
- Importing an Open WebUI export truncated long chats and froze the UI.
- A failed sign-in showed no error, and the setup wizard could be walked past
  its declaration step.
- Chat timestamps were read back as local time instead of UTC.
- The privacy tokenizer wrote its placeholders into finished deliverables.
- Parallel tool calls could merge into one unusable call.

## Upgrading

- Speech models: previously downloaded `ggml-*.bin` files under
  `%LOCALAPPDATA%\Pia\Models\` are no longer used and can be deleted to reclaim
  disk space. The replacement bundles download on first use.
- Transcripts from Whisper can differ slightly from previous releases for the
  same audio, because the engine changed. Parakeet and speaker attribution are
  unaffected.
- Scheduled research moved into Routines. Your existing scheduled jobs keep
  running unchanged, but their results now arrive as an ordinary chat; the
  separate Research view is gone.
