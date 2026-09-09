# Pia 1.4

## Assistant

- A new chat's working folder can be changed from the chat itself. Hover the
  folder shown under "Start a conversation with Pia" and click it to pick
  another; a chat keeps its folder once the first message is sent.

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
  under Settings → Assistant → "Screen capture while you are away", and never a
  whole display. Each of those captures leaves a notice in the flow rail.

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
- The chat history you built up before signing in now reaches your other
  devices. That first upload could be marked as done while the server was still
  refusing it, and nothing retried it afterwards.

## Routines

- A routine that fires now does the work instead of asking about itself. It
  used to read its own instructions as a request to set a routine up and reply
  with a question about the schedule, which nobody was there to answer — and
  the run counted as finished.
- Routines now have their own working folder, picked with the same folder
  browser a chat uses. A routine reads and writes inside that folder, and the
  chat a run produces opens on it. The folder is remembered on this device
  only, and existing routines keep working across the whole folder.

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
