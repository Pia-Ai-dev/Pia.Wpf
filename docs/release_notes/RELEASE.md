# Pia 1.4.205

## AI providers

- Anthropic is a provider type you can add with your own API key. Pick it in
  Settings → AI Providers, enter a Claude model name, and the endpoint fills in
  as https://api.anthropic.com.
- Enable web search on an Anthropic provider and Claude looks things up itself
  while it answers, instead of relying on what it already knows.
- Cache the prompt prefix (5 min) makes a long conversation re-read its history
  instead of paying for it again on every turn. It costs a little more on the
  first turn, so leave it off for short prompts and turn it on where the same
  long instructions come back turn after turn.

## MCP servers

- Add MCP servers that run on your own machine under Settings → Assistant → MCP
  servers. Paste an mcpServers entry from another client to fill the form in, or
  type the command, arguments and environment variables yourself. Environment
  values are encrypted for your Windows account.
- Test connection starts the server, lists what it offers and tells you why it
  failed if it did not start — so you find a wrong path or a missing token
  before the assistant does.
- Tick the tools a server may offer the assistant. Untested servers offer all of
  them; a tool you untick is not just un-approved, the assistant never sees it.
- Selecting a server opens a detail page like a persona's: whether it is
  running, its command, working directory and the names of its environment
  variables, and the full list of tools it offers with the ones you withheld
  marked. Test connection sits next to Edit and Delete there, so a server that
  is switched off can still tell you what it offers.
- Switching one server on or off no longer greys out every other switch, a
  server that finishes starting later reports its real state instead of reading
  "Not running", a tool's description sits in a hover icon next to its name, and
  editing a server keeps what you typed when another one starts in the
  background.

## Images

- A chat message can carry up to four images instead of one. Dropping, pasting
  or capturing another one adds it to the strip above the composer rather than
  silently replacing what was already there, and each thumbnail has its own
  remove button.
- read_file now looks at an image in your files folder instead of telling you to
  attach it, so you can ask about a screenshot by name. search_files no longer
  reports every image in the tree as a file it failed to read.
- Writing text into an image file is refused rather than corrupting it.

## Assistant

- Chats can be marked as favorites. A star sits beside every row in Chat history
  and in the chat list that drops down from the title, and the ones you star
  collect in a Favorites group above the date groups — however old they are.
- "Delete chats not opened for N days" no longer applies to a favorite. A chat
  you marked to keep stays whatever happens; deleting one by hand still works as
  before.
- Favorites sync, so a chat starred on one machine is starred on the next,
  end-to-end encryption included.

## Routines

- A routine whose model answers with nothing now gets asked once more before
  the run is given up on. The second ask carries everything the run already
  gathered and takes no further tool steps, so a run that used to end as "The
  model gave no answer" finishes with one.
- A run that still ends without an answer records what the model did send —
  its finish reason, and how much of the turn went into reasoning — so a log
  attached to a support mail says why.

## Window behaviour

- Minimizing Pia now sends it to the taskbar, like any other app. Closing the
  window with the X is what puts it away into the notification area.
