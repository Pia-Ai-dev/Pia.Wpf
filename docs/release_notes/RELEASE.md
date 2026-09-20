# Pia next

## Assistant

- Ask Pia about Pia. Questions like "can I run agentic tasks?" or "how do
  I change the language Pia speaks in?" are now answered from the built-in user
  guide and from your own settings, naming the exact path through Settings in
  your language. Pia no longer searches the web for them or guesses.
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
- An agent run no longer asks you to approve its own working notes. Pia drafts
  these under `.scratch/`, which is never published, so they now run without a
  confirmation — and they no longer appear as a second file link next to the
  deliverable they were notes for.
- An agent run clears its working notes away when it finishes. Those notes are
  normally thrown out with the run's private copy of your folder, but a run that
  could not make one wrote them into the folder itself and left them there,
  where Pia's own file list does not show them. Only files the run wrote go.
- An agent run no longer leaves its private copy of your folder behind. A run
  that used version control inside it left a directory Windows refused to
  delete, and every launch since retried and failed. Those are cleared away
  now, including the ones already stranded.
- An agent run gets started sooner. Preparing its private copy of your folder
  could stall for half a minute before anything reached the screen, and nothing
  on screen said what it was waiting for.
- Agent mode now recognises a question. Ask one with the Agent lever on and Pia
  answers it straight away instead of drawing up a plan for it, marking the
  reply so you can see it chose to. Real multi-step work still plans as before.
- A new chat no longer starts in Agent mode because you once tried it. The
  Chat/Agent lever now belongs to each conversation, and what a new chat starts
  on is a setting you choose in Settings > Assistant.
- An agent run shows its clock running while it thinks. Drawing up a plan can
  take half a minute and reported nothing for the whole of it, which read as a
  hung window. The clock deliberately stops while a run is waiting on you.
- Pia no longer makes you wait behind its own background syncing. A request you
  are waiting for used to queue behind every housekeeping upload already in
  flight; the two are paced separately now.
- Conversations from before you signed in now reach the cloud. The first upload
  sent your whole history at once, was turned away for going too quickly, and
  began again from nothing at the next launch, so most of it never arrived. It
  now keeps what it managed and carries on from there.
- The file list behind a message's "+N" chip stays open while you scroll it. It
  shut on the first wheel notch, which put every file past the first few out of
  reach.

## Transcription

- Saving or summarizing a long recording now covers the whole session. Past
  about an hour, everything before that was missing from the saved file, the
  vault copy and the summary — silently, and it could not be recovered
  afterwards.

## Privacy

- Meeting transcripts no longer say who spoke which line unless you ask for
  it. Telling speakers apart means measuring each participant's voice, so that
  is now something you switch on rather than something Pia does by default.
  The setting sits with the other transcription options in Settings.

- Log files no longer carry your Windows account name, the web addresses Pia
  calls, the names you gave your providers, or which applications you have open.
  Folder paths are written as `%APPDATA%\Pia` instead of the full path through
  your user folder, so a log you attach to support no longer identifies you.

## Text to speech

- Pia reads aloud in Alba by default now, and two voices have been withdrawn.
  The recordings Lessac and Ryan were built from are licensed for research and
  non-commercial use only, so Pia no longer offers them. If you had one
  selected, Pia moves to another installed voice or asks you to pick one.
