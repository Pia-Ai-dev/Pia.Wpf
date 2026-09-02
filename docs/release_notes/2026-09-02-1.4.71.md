# Pia 1.4

Successor to 1.4.16. Mostly about the files Pia can work with: what it can
open, what it can search, and what happens to something you drop into a chat.

## Files

- The assistant can find files by name. Ask for "every spreadsheet under
  reports" and it matches the pattern directly instead of listing folder by
  folder or reading everything. It is also told which folder it is working in,
  so it stops guessing at relative paths, and a mistyped name now suggests the
  near-matching file next to it.
- Editing a file no longer means rewriting it. Pia sends just the piece that
  changes, so a one-word correction cannot garble the rest of a long document
  on the way through. You still see and approve the same diff.
- Word documents and Excel workbooks can be edited in place. Only the
  paragraphs or cells that change are touched; everything else in the file is
  left exactly as it was, including formatting Pia never saw.
- Pia can look inside those documents. Searching a folder's contents now
  covers Word documents, Excel workbooks and saved Outlook mail, which were
  skipped as unreadable before. A file it genuinely cannot read — a PDF, an
  image — is named in the result, so an unsearchable document no longer looks
  like an absent answer. Saved mail is read-only: Pia will not write over a
  .msg or .eml.
- Searching no longer depends on getting the capitalisation right. Pia ignores
  it, so a search for "cookie" finds "Cookies" and the other way round. It can
  still be asked to match capitals exactly, for the times you want the marker
  TODO rather than every mention of the word.

## Dropping files into a chat

- A dropped text file becomes a chip beside the composer instead of dumping
  its contents into the input box on top of whatever you were typing.
- Mail can be dragged straight out of Outlook's message list, not just from a
  saved .msg on disk, and both .msg and .eml are read into text — so you can
  drop a mail in and ask about it.
- Attachments stay in sight. Each chip has a save button that copies the file
  into the chat's working folder, the sent message keeps its chips, and a
  saved file can be opened or revealed later. Without that the file was gone
  the moment you pressed send.
- A file staged in one chat no longer follows you into the next one.

## Agent runs

- A run that pauses for permission now holds the whole call it was about to
  make, and carries it out unchanged once you allow it. Before, only the tool's
  name survived the pause, so the write you approved never actually happened.
- The approval panel shows the full call rather than a truncated line, so you
  can read what it will act on before deciding.
- A paused run remembers what it had already read. Resuming used to drop every
  tool result from before the pause, and the run would then tell you it could
  not read a file it had read twice.
- Steps no longer duplicate each other's work. A step is told what the
  remaining steps have promised to produce, so two of them stop writing two
  versions of the same summary.
- A step that is asked to file something in your memory vault is now told how
  to do it, instead of writing into its own scratch folder where the file
  looked saved and never arrived. The final check confirms the file is really
  there rather than taking the run's word for it.
- A run that asks you a question stops and waits instead of spending its
  remaining turns first.
- "Awaiting approval" disappears when the run is finished, and planning now
  follows the same model routing an administrator set for everything else.

## Memory vault

- Ingest no longer makes a page for every name it meets. A market report used
  to produce a page per company, index and person mentioned; there is now a
  bar for what earns a page and a ceiling on how many one source can add.
- Different spellings of one thing land on one page — "Azure OpenAI" and
  "Azure OpenAI Service" no longer each get their own.
- Near-duplicate pages can be merged from the vault view, and the merge is
  real: the sources move with it, so the next ingest does not recreate the
  page it just absorbed.
- Pia can draft your vault charter — the note that decides which topics are
  worth keeping — rather than leaving you an empty file to fill in.
- Pages of the same kind now follow the same shape, so a person page and a
  product page each read the way their category should.
- Browsing the vault shows a one-line summary per topic, so finding the right
  page no longer means opening several.
- Ingest is faster on a large vault, and a page whose title had been written
  in a form Pia could not read back is repaired instead of failing forever.

## Chats

- The composer's eraser is now two buttons: "+" starts a new chat and leaves a
  running answer to finish in the one it belongs to, and a trash can deletes
  the chat you are in.
- The folder named in an empty chat is a link — clicking it opens that folder.
- The protected-route shield sits with the answer it describes and stays there.

## Personas and chrome

- The Personas tab is a list beside a detail card instead of a modal dialog.
  Selecting a persona shows its system prompt, guardrails, output format,
  expertise, provider and reasoning effort without opening the editor at all,
  and Edit, Duplicate and Delete sit on that card. Editing happens in the same
  pane, the way Routines already worked.
- "New" no longer throws away an open editor in Routines or Personas; it is
  refused while one is open instead of discarding what you typed.
- Scroll bars fade out while nothing is scrolling, and come back on a scroll or
  when the pointer reaches the thumb.
- Status badges are a shade quieter: the pill's tint is dialled back without
  fading the text on it.

## Fixes

- Chat history showed timestamps on UTC rather than your own clock, and froze
  at the day the app was started.
- The Optimize history stopped reloading after the first visit, so it kept
  showing whatever it saw the first time it was opened.
- On loudspeakers, the other party was transcribed a second time as you.
- Joining a Teams meeting failed 15 seconds in on a German Windows install.
- Devices reported their version as 1.4.0.0 whatever they were actually
  running, and only ever once — the fleet view was wrong for everyone.
