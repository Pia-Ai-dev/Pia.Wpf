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
- A local server's tools carry the server name as a prefix, so they can never
  collide with Pia's own read_file or write_file. Servers Pia would reach over
  the network are not supported here — only ones it starts itself.
