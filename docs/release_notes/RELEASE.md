# Pia 1.4

## Assistant

- A new chat's working folder can be changed from the chat itself. Hover the
  folder shown under "Start a conversation with Pia" and click it to pick
  another; a chat keeps its folder once the first message is sent.
- Typing @ in the message box offers Files at the top of the list and picks it
  by default, so Enter goes straight to your files. It is missing only if you
  turned the file tools off under Settings → Assistant.
- A failed answer reads as a sentence again. Some upstream failures reached the
  chat as the raw JSON the server sent, error braces and all, in the bubble
  where the answer belongs.
- Running out of credit is reported in your own language, and a reset more
  than a day away is counted in days. The notice was English whatever the app
  language was, and named the wrong number of hours that far out.
- The cards that ask to run a tool read as German again. Their titles were built
  verb first, so every one of them said "Erstellen Aufgabe" rather than "Aufgabe
  erstellen". English and French are unchanged.
- A maximized window stays maximized. Opening a routine's chat from the flow
  rail, or bringing Pia back from the tray, shrank it to a smaller window first.

## Chat history

- A long question no longer fills the pane. Chat history folds it the way the
  chat itself does, so the answer to it stays in view.

## Screen

- A picture of your screen can go into a message. The Capture screen button
  next to the paperclip opens a picker of your displays and open windows; the
  shot lands in the message box as a thumbnail and is sent only when you send
  the message. Pia's own windows are never offered.
- The picker also opens on a shortcut of your own, set under Settings →
  General beside the other shortcuts.
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
- A voice can be removed again from its row under Settings → General → Speech,
  which gives back the space it took. Pia asks first, and if it was the voice
  in use it moves to another installed one.

## Notifications

- Windows notifications from Pia are headed "Pia AI Assistant" rather than
  "Pia.Wpf". Windows binds that name the first time it sees an app and never
  re-reads it, so Pia registers under a new identity: its entry in Settings →
  Notifications is recreated, and notifications already sent keep the old name.

## Updates

- "Check for updates automatically" under Settings → General can be unticked
  to stay on the version you have. Left on, which is the default, Pia looks for
  a new version in the background, downloads it, and then offers to restart and
  update.
