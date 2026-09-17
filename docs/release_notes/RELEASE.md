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
- Selecting a server now opens a detail page like a persona's: whether it is
  running, its command, working directory and the names of its environment
  variables, and the full list of tools it offers with the ones you withheld
  marked. Test connection sits next to Edit and Delete there, so a server that
  is switched off can still tell you what it offers.
- Switching one server on or off no longer greys out every other switch. The
  row you touched shows a spinner and reads Starting… or Stopping… until its
  process is up or gone.
- Servers that finish starting after the Settings page opens no longer keep
  reading "Not running". Every server now reports its real state, instead of
  only whichever one happened to be up when you got there.
- A tool's description moved into a hover icon next to its name, so a server
  with a dozen tools is a list you can scan rather than a wall of text.
- Adding or editing a server no longer loses what you typed when another
  server starts or stops in the background.
- Saving a server shows that it is restarting, instead of a Save button that
  goes grey for a few seconds with nothing else to see.
- A local server's tools carry the server name as a prefix, so they can never
  collide with Pia's own read_file or write_file. Servers Pia would reach over
  the network are not supported here — only ones it starts itself.
