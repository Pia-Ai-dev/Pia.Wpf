# Pia 1.4

## Shortcuts

- Holding a global shortcut no longer flips its window open and shut. The
  Optimize window reopened and closed for as long as Ctrl+Alt+O was held.
- The Assistant window can be tucked away with its own shortcut, the way the
  Optimize window already could — but only when nothing is typed, attached or
  still running.

## Assistant

- Opening a chat from the history or the picker shows its newest message,
  wherever you had scrolled to in the chat before it.
- A long draft no longer fills the window. The input stays a few lines tall
  and offers to grow, and goes back once the message is sent.
- A long message you have sent no longer fills the window either. Its bubble
  shows five lines and offers "Show more"; the answer to it stays in view.
- A chat can be given a name of your own. Pick it in the history, next to
  Resume, and it is what the history shows from then on — useful above all
  with automatic titles switched off.
- The same rename sits in the chat picker at the top of the Assistant: hover a
  row, click the pencil, type and press Enter. The list stays open around you.
- A chat also keeps the name it has. Every save used to write the first
  message back over it, so an automatic title lasted until the next reply.
- A new chat's working folder can be changed from the chat itself. Hover the
  folder shown under "Start a conversation with Pia" and click it to pick
  another; a chat keeps its folder once the first message is sent.

## Screen

- A picture of your screen can go into a message. The Capture screen button
  next to the paperclip opens a picker of your displays and open windows; the
  shot lands in the message box as a thumbnail and is sent only when you send
  the message. Pia’s own windows are never offered.
- The picker also opens on a shortcut of your own, set under Settings →
  General beside the other shortcuts.
- Pia can ask to look by itself when a question needs the screen. It asks
  first, on the same card any other tool is approved on, and the card names
  the program it wants to see — never the window title.
- All of this needs Pia Cloud. On any other provider the Capture screen button
  is greyed out and says so, and Pia’s own request is turned down before
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

## Notifications

- The ✕ on a message that slides in at the top left now closes it while the
  flow rail is open. The click went to the rail instead.

## Todo

- The Closed column stays open once you open it. Adding or removing a task
  collapsed it again.

## Connection

- Losing your internet connection now says so, in your language, and says the
  message can be sent again. It used to land in the chat as an English socket
  error that stayed in the transcript.

## Cloud sync

- Chats no longer leave this device before you sign in to Pia Cloud. They were
  offered to the server and refused, so none of them was ever stored — but they
  should not have been sent at all.
- The chat history you built up before signing in now reaches your other
  devices. That first upload could be marked as done while the server was still
  refusing it, and nothing retried it afterwards.

## Optimize

- Text too long for Pia Cloud's optimizer is turned down with a sentence that
  names your length and the limit, instead of an English server message.

## Files

- PDFs can be dropped on the Assistant and on Optimize. Their text is read the
  way a Word or Excel file's already is; a scanned PDF says it holds no text
  rather than failing silently.
- A file Pia turns down for its size now says what the limit is, instead of
  only that the file was too large.

## Settings

- Turning a built-in plugin off in Settings → Plugins now sticks. The switch
  was kept for the session only, so every restart brought the plugin back on.
- A setting your administrator supplies a default for can now be changed to
  any value, including the one Pia itself ships. Picking that value read as
  "never touched it", so the administrator's default came back on the next
  save — most visibly, the interface language could not be set to English
  under a German default.
- The Edit and Delete buttons on your own Optimize templates are fully
  visible again. They sat past the edge of the card and could not be
  clicked.

## Navigation

- Navigation labels are readable in the dark theme again. They were drawn in
  black on the dark sidebar.
- The appearance switch at the bottom of the sidebar now says "Appearance"
  and explains itself on hover, instead of showing only the name of the theme
  it is currently on.
- A Help entry sits next to it and opens the Pia desktop guide in your
  browser, in the language the interface is set to.

## Routines

- A failed run now says why. The run list under a routine shows the reason
  beneath "Failed" — a server or provider message word for word — and
  opening the chat of a failed chat-mode routine shows the request and the
  failure instead of an empty chat.
- A failure Pia Cloud reports mid-answer no longer ends as "The model returned
  no answer." Its actual message reaches the routine's run, the agent run's
  failure card and an interactive chat alike, so a timeout or an upstream
  error reads as what it was.
- A routine that fires now does the work instead of asking about itself. It
  used to read its own instructions as a request to set a routine up and reply
  with a question about the schedule, which nobody was there to answer — and
  the run counted as finished.
- Routines now have their own working folder, picked with the same folder
  browser a chat uses. A routine reads and writes inside that folder instead of
  the whole assistant files folder, the chat a run produces opens on that same
  folder, and the folder is remembered on this device only. Existing routines
  keep working across the whole folder.

## Meetings

- Pia can join a Teams meeting again. Teams stopped accepting the brackets
  around the "AI notetaker" label, leaving the assistant on the join screen
  until it gave up; the name it joins under now reads
  "Alex's assistant - AI notetaker".
- A meeting stays silent on your machine even with "Show the meeting browser
  window" ticked. It used to play out of your speakers, which echoed if you
  were in the same call from the Teams app on the same device.

## Transcription

- Stopping a live or meeting transcription keeps what you just said. Audio the
  microphone had already handed over could be dropped while the recogniser shut
  down, cutting the last words off the transcript.
- Saving a transcript to a file now opens in the working folder of the chat you
  are in, rather than Pia's own meetings folder, and the suggested name leads
  with the date — 2026-09-03_meeting.md. Saving a second transcript on the same
  day asks before it overwrites.
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

## Performance

- Leaving a screen and coming back no longer leaves the old copy behind in
  memory. A long session that moves between the chat and the other views used
  to climb into the gigabytes; it now stays flat.
