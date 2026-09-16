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
