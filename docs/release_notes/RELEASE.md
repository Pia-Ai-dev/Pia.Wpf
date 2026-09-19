# Pia next

## Assistant

- Long conversations are cheaper and faster from the second turn on, on
  providers that cache a prompt prefix. The saving grows with the chat, so it is
  largest exactly where a reply used to feel slowest.
- Every reply starts sooner. Pia paced its own requests half a second apart even
  when nothing had asked it to wait; that gap is now a tenth of a second. Agent
  runs feel it most, because they pay it on every round of thinking.
- Agent runs stop thinking once they have what they need. Building a plan and
  checking the finished work each spent one extra round on an answer nothing
  read.
- A run that took a single step and wrote no file no longer spends a round
  double-checking its own answer. There was nothing there to check it against.
- Agent mode now recognises a question. Ask one with the Agent lever on and Pia
  answers it straight away instead of drawing up a plan for it, marking the reply
  so you can see it chose to. Real multi-step work still plans as before.
- A new chat no longer starts in Agent mode because you once tried it. The
  Chat/Agent lever now belongs to each conversation, and what a new chat starts
  on is a setting you choose in Settings > Assistant.

## Transcription

- Saving or summarizing a long recording now covers the whole session. Past
  about an hour, everything before that was missing from the saved file, the
  vault copy and the summary — silently, and it could not be recovered
  afterwards.

## Privacy

- Log files no longer carry your Windows account name. Folder paths are written
  as `%APPDATA%\Pia` instead of the full path through your user folder, so a
  log you attach to a support request no longer identifies you.
