# Pia next

## Privacy

- The end-to-end encryption description in Settings → Account now says what
  the encryption covers, like the setup wizard: your device encrypts the
  content, while IDs, timestamps and order stay readable.

## Live transcription

- The transcript now shows speech under the name of someone who said the
  consent sentence only when the voice clearly matches theirs. A voice that
  merely resembles theirs stays out of the transcript, like any speaker who
  has not consented.
- A name you give a speaker before they have said the consent sentence no
  longer reaches the consent log or the file name of the consent record. Both
  keep the label Pia detected the speaker under.
- When someone says the consent sentence, their chip in the transcription
  window now switches to their name instead of a second chip appearing while
  the first one keeps waiting for consent.

## Meetings

- "Name speakers" in Settings → Assistant → Meeting can now be switched off.
  Speakers then keep labels such as "Speaker 1": no name from the consent
  sentence, no renaming, and no attendee list in summaries or saved notes.
  Your administrator can enforce it.
- A scheduled meeting Pia attends on its own is still saved to your vault, but
  the AI model no longer evaluates its transcript by itself. Ask the assistant
  to ingest it when you want it in your topic pages.

## Providers

- Your administrator can now limit which AI provider types and which addresses
  Pia may use, and switch off the providers' own web search. Settings →
  Providers offers only what is allowed, and a provider outside the limits is
  refused before anything is sent to it.
- Adding or editing a provider now says who receives what you send: whoever
  runs the address you enter.

## Plugins

- A plugin your administrator distributes now waits for you to switch it on,
  unless your administrator switched it on for everyone; ones already running
  keep running. Once switched off, it stays off when Pia starts or the server
  updates it, and switching it back on starts it right away.
- Your administrator can now keep local MCP servers off this device. Adding
  one says that it runs as a program with your permissions and receives what
  the assistant passes to its tools.
