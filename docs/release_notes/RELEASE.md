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

## Transcription

- A message in a meeting transcript can be put on the right speaker. Right-click
  it: "This is another speaker…" moves it to someone already in the
  transcript, "Detect the speaker again" asks for a second opinion. It stays
  where you put it — detection keeps running and no longer moves it back.
- Renaming a speaker also holds now. A name you typed could disappear later in
  the meeting, when detection merged that voice into another one.
- Speaker numbers in a meeting transcript stay put. Detection keeps refining
  itself while a meeting runs, and each round used to renumber the speakers —
  so someone became Speaker 2 without having said a word. Numbers now hold for
  the whole meeting, which means the sequence can skip one.
