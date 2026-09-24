# Pia next

## Assistant

- Imported Open WebUI chats read as answers again. The model's thinking came
  over as raw markup in the middle of the reply; it now sits behind the same
  collapsible heading Pia uses for its own reasoning. Importing the same file
  again repairs the conversations you already brought over.
- Pia warns you when it is running as administrator. A notice at the top of
  the chat explains that file tools, agent runs and MCP servers inherit those
  rights, and suggests restarting without them. Dismissing it lasts for that
  session only.
- An agent run's steps now use the model your persona asks for. A step assigned
  to a coding persona reached the server without saying so and was answered by
  whatever model your group has set as its default, so picking a persona for its
  model only worked in ordinary chat.
- YAML keeps its indentation. A snippet lost its nesting on the way to the
  screen when the model indented the fence further than the snippet inside it,
  and code pasted into a message that also used an @-command reached the model
  flattened — either way the Copy button handed back a file that no longer
  parsed.
- YAML is syntax-highlighted in code blocks, the way JSON and the other
  languages already were.
- A long answer that uses many tools no longer stops with a provider timeout
  when every request was answered in time. The limit covered the whole turn,
  file edits between requests included, so a provider that replied within
  seconds a dozen times over still ran it out.
- A provider's "Timeout (seconds)" accepts up to 600; it stopped at 300.
- Answer text is sharp on every line. Single lines of a reply rendered
  blurred, most often the second or third, and which ones changed as the
  chat scrolled.

## Privacy

- The "Protected" shield now appears on every answer a protected model helped
  write, not only the ones whose last request went there. An agent step asks the
  server several times as it works, so a step answered under the shield and then
  finishing normally used to show nothing.

## Administration

- A sign-in method your organization's policy turns off is now refused, not
  just hidden, and anyone still signed in with it is signed out the next time
  Pia starts.

## Models

- Speech, voice and embedding models now download from Pia's own server only.
  When it cannot be reached the download fails instead of quietly pulling the
  file from Hugging Face or GitHub, so a machine that is allowed to reach Pia
  and nothing else behaves the same way every time.
