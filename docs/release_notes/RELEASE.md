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

## Privacy

- The "Protected" shield now appears on every answer a protected model helped
  write, not only the ones whose last request went there. An agent step asks the
  server several times as it works, so a step answered under the shield and then
  finishing normally used to show nothing.

## Models

- Speech, voice and embedding models now download from Pia's own server only.
  When it cannot be reached the download fails instead of quietly pulling the
  file from Hugging Face or GitHub, so a machine that is allowed to reach Pia
  and nothing else behaves the same way every time.
