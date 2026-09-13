# Pia 1.4

## Assistant

- A new chat's working folder can be changed from the chat itself. Hover the
  folder shown under "Start a conversation with Pia" and click it to pick
  another; a chat keeps its folder once the first message is sent.
- Making a folder in that picker moves the chat into it. The new folder was only
  highlighted, so a chat you created one for went on writing into the folder it
  started in.
- Typing @ in the message box offers Files at the top of the list and picks it
  by default, so Enter goes straight to your files. It is missing only if you
  turned the file tools off under Settings → Assistant.
- A failed answer reads as a sentence again. Some upstream failures reached the
  chat as the raw JSON the server sent, error braces and all, in the bubble
  where the answer belongs.
- Running out of credit is reported in your own language, and a reset more
  than a day away is counted in days. The notice was English whatever the app
  language was, and named the wrong number of hours that far out.
- Being signed out of Pia Cloud is reported in your own language. Sending a
  message while signed out answered in English whatever the app language was.
- The cards that ask to run a tool read as German again. Their titles were built
  verb first, so every one of them said "Erstellen Aufgabe" rather than "Aufgabe
  erstellen". English and French are unchanged.
- A maximized window stays maximized. Opening a routine's chat from the flow
  rail, or bringing Pia back from the tray, shrank it to a smaller window first.
- Minimizing Pia no longer strands the window. Ctrl+Alt+P and a double-click on
  the tray icon bring it back up, instead of returning it to the taskbar still
  minimized and leaving the taskbar button as the only way in.
- Part of a message you sent can be selected and copied, the way Pia's answers
  already could be. The copy button beside the bubble still takes the whole
  message.
- Your own messages are a lighter blue in the light theme, so the conversation
  sits with the rest of the window rather than over it. The dark theme is
  unchanged.
- The plan panel at the top of a chat fades into the conversation below it
  instead of cutting the first line off.
- Moving between the chat and Chat history no longer leaves memory behind. Each
  switch through a long conversation held on to everything it had drawn, so the
  window grew slower the more often you went back and forth.
- A long chat opens at once rather than after most of a minute. It shows its
  most recent messages, with "Show older messages" above them to bring the rest
  back a block at a time, and it keeps your place when it does.

## Agent runs

- An agent run started in a chat can take the conversation with it, so a
  follow-up like "the file isn't in the working folder" is planned against what
  was said. A bar above the message box asks once per chat — "Send a summary",
  "Send the conversation" or "Send nothing" — and holds sending until you pick.
- The Chat/Agent lever stays where a finished run left it. It fell back to Chat
  when a run settled, then flipped itself back to Agent a few minutes later
  whenever Pia synced.
- A running plan now says what it is doing. While a step works, the panel and
  the conversation below it show how many tools it has used and which one was
  last. The chat used to stay still for minutes, because a step writes its
  answer only when it finishes.
- The blank bubble between two steps is gone. A step that finished without
  anything to say still left Pia's mark in the conversation with nothing
  beside it, and it stayed there. Those are no longer written, and the ones
  already in your chats no longer show.
- A plan with a step that writes two files no longer comes back empty. The plan
  box showed no steps at all and the run quietly became a single turn, because
  one step naming both its files was enough to discard the whole plan. A step
  that cannot be read is now dropped on its own, and the rest of the plan stands.
- A run that still ends up without a plan says so, instead of showing an empty
  plan box beside a spinner. It reads "could not build a plan — working through
  the goal in one turn", which is what it is doing.
- A plan you approved writes into the chat's folder, and a routine into its own,
  even when that folder is too large to set aside for the run. Those runs put
  their files at the top of your assistant files folder instead.
- Tool activity counts what happened rather than how often it was asked. Two
  requests you approved were listed as four decisions, two of them "not run"
  for calls that did run, because approving one is recorded twice. They now
  read as one approval each, and as yours rather than automatic.

## Chat history

- A long question no longer fills the pane. Chat history folds it the way the
  chat itself does, so the answer to it stays in view.
- The preview pane opens a long conversation at once as well. It shows the most
  recent messages with "Show older messages" above them, keeps your place when
  you bring more back, and stays put when the list refreshes behind it instead
  of drawing the whole chat again.
- Imported chats stay. An archive of older conversations counted as untouched
  for as long as its own dates said, so most of it was cleared shortly after
  the next start; Import now counts as opening them. What an earlier version
  cleared is gone from Pia Cloud and your other devices too — import it again.
- Chat history lists every conversation from the moment you open it. It started
  on the last 30 days, so an imported archive of older chats read as empty until
  you cleared the dates. Pick a start date yourself to narrow it again.

## Text optimization

- The optimized text is marked as AI-generated. A line under it asks you to
  check it before you use it.
- Templates under Settings → Optimize are a list with a panel beside it, the
  way Routines and Personas already are. Pick a template to read its prompt,
  and edit it in place instead of in a pop-up window.
- A template can be duplicated, which is the way to start from a built-in one,
  and now carries a description of its own.

## Screen

- A picture of your screen can go into a message. The Capture screen button
  next to the paperclip opens a picker of your displays and open windows; the
  shot lands in the message box as a thumbnail and is sent only when you send
  the message. Pia's own windows are never offered.
- The picker also opens on a shortcut of your own, set under Settings →
  General beside the other shortcuts. It works while Pia is still answering:
  the picture waits in the message box for your next message.
- The picker opens with the first window it can capture already picked, so the
  arrow keys go straight to the one you want.
- Pia can ask to look by itself when a question needs the screen. It asks
  first, on the same card any other tool is approved on, and the card names
  the program it wants to see — never the window title.
- All of this needs Pia Cloud. On any other provider the Capture screen button
  is greyed out and says so, and Pia's own request is turned down before
  anything is captured.
- A routine or a background run can only see windows you named in advance,
  under Settings → Assistant → Tool access → "Screen capture while you are
  away", and never a whole display. Each of those captures leaves a notice in
  the flow rail.
- Those windows can be picked from a list rather than typed. "Choose from open
  windows..." beside the fields fills in the program and the window title for
  you, and both stay editable — a title you can shorten is one that still
  matches tomorrow.

## Designing with Pia

- Templates, routines and personas each have an "Advanced…" button beside the
  one-shot assist. It starts from the same single sentence, then Pia asks you
  the few things it cannot work out on its own — the tone to hit, a sample of
  the text you want rewritten, which day a routine should run — and fills the
  editor in from your answers.
- Everything it produces lands in the editor as an ordinary draft, so you can
  change any of it before saving. "Generate Prompt" on a template, and "Draft
  with AI" on a routine or persona, are unchanged and still there.

## Personas

- The built-in personas now ask Pia Cloud for the model that suits them:
  Pia · Personal and Explain It Simply for a fast one, Experienced Coder for a
  coding one. Every other provider answers as before.
- A persona's Model type offers "private" in its list, and says beneath the
  field that it only takes effect if your cloud provider offers a private
  model.

## Cloud sync

- Chats no longer leave this device before you sign in to Pia Cloud. They were
  offered to the server and refused, so none of them was ever stored — but they
  should not have been sent at all.
- Signing in now uploads the chat history you built up before it. That first
  upload could be marked as done while the server was still refusing it, and
  nothing retried it afterwards. An install that already signed in under an
  earlier version keeps those chats on this device only.
- Opening a chat tells Pia Cloud you still want it. Reading a conversation
  counted as using it on that device alone, so an old chat you kept coming back
  to still looked untouched to your other devices — and the first one to clear
  out old chats cleared it everywhere.
- Clearing out old chats now asks Pia Cloud when each one was last opened,
  instead of going on what this device happens to know. A chat you read on
  another computer is kept, and a device that cannot reach the server clears
  nothing at all rather than guessing.

## Routines

- A routine that fires now does the work instead of asking about itself. It
  used to read its own instructions as a request to set a routine up and reply
  with a question about the schedule, which nobody was there to answer — and
  the run counted as finished.
- Routines now have their own working folder, picked with the same folder
  browser a chat uses. A routine reads and writes inside that folder, and the
  chat a run produces opens on it. The folder is remembered on this device
  only, and existing routines keep working across the whole folder.
- "Draft with AI" no longer reads as a dead button. An answer that comes back
  empty is tried once more and then reported, and a draft that had nothing to
  fill in — because the name and the instruction were already written — says so.
- The editor and a routine's details keep a readable line length on a wide
  window instead of stretching across it, and the list beside them now grows
  and shrinks with the window.
- A routine can repeat "Manual only", which never runs on its own. Use it for a
  routine you start by hand — a long task you want set up once, with its own
  folder, persona and tool grants — and it keeps its next run empty rather than
  waiting for a time.
- Asking Pia in a chat to start a routine now starts the routine itself. Tag it
  with @Routine and say to run it: Pia asks to confirm, then the routine runs
  with its own working folder, persona and tool grants, and its answer arrives
  as a new chat. It used to re-read the instructions in the chat you were in,
  which ran them in the wrong folder and without the agent steps.

## Todo

- A task opens for reading rather than straight into a form. Double-click a
  card, or use the pencil on it, to see the notes, the priority and the due
  date; the Edit button in that panel turns it into the fields, which now have
  room for a long note.

## Meetings

- Pia can join a Teams meeting again. Teams stopped accepting the brackets
  around the "AI notetaker" label, leaving the assistant on the join screen
  until it gave up; the name it joins under now reads
  "Alex's assistant - AI notetaker".
- A meeting stays silent on your machine even with "Show the meeting browser
  window" ticked. It used to play out of your speakers, which echoed if you
  were in the same call from the Teams app on the same device.

## Transcription

- The record of who consented to being transcribed is kept for 14 days and then
  deleted, folder and all. Pia clears what is due every time it starts, and once
  a day while it runs.

## Spoken replies

- Pia's voices now come from Pia's own servers instead of a third-party model
  host, and there is no separate speech engine to download any more. Networks
  that blocked the old host can install a voice again.
- Your voice has to be picked once more: the voices downloaded by earlier
  versions cannot be used by the new speech engine and are removed on first
  start, which gives back the space they took. Choose one again under
  Settings → General → Speech.
- Spoken answers begin sooner, and the short phrases Pia says while it thinks
  are ready as soon as a voice is installed.
- A voice is selected as soon as its download finishes, so speech works without
  a second step. If the saved voice is no longer on this device, Pia takes up
  one that is instead of staying silent.
- A voice's download bar runs the full width of its row instead of stopping
  short of the buttons beside it.
- A voice can be removed again from its row under Settings → General → Speech,
  which gives back the space it took. Pia asks first, and if it was the voice
  in use it moves to another installed one.

## Notifications

- Windows notifications from Pia are headed "Pia AI Assistant" rather than
  "Pia.Wpf". Windows binds that name the first time it sees an app and never
  re-reads it, so Pia registers under a new identity: its entry in Settings →
  Notifications is recreated, and notifications already sent keep the old name.

## Flow rail

- Cards clear themselves after a while instead of piling up. How long depends
  on the card: a failed routine or a fired reminder stays a month, a finished
  background chat two weeks, a completed routine a week, an app error a day. A
  run waiting on you, or an overdue task, never goes on its own.

## Updates

- "Check for updates automatically" under Settings → General can be unticked
  to stay on the version you have. Left on, which is the default, Pia looks for
  a new version in the background, downloads it, and then offers to restart and
  update.
