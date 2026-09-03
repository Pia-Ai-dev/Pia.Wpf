# Pia 1.4

## Settings

- Turning a built-in plugin off in Settings → Plugins now sticks. The switch
  was kept for the session only, so every restart brought the plugin back on.
- A setting your administrator supplies a default for can now be changed to
  any value, including the one Pia itself ships. Picking that value read as
  "never touched it", so the administrator's default came back on the next
  save — most visibly, the interface language could not be set to English
  under a German default.

## Navigation

- Navigation labels are readable in the dark theme again. They were drawn in
  black on the dark sidebar.
- The appearance switch at the bottom of the sidebar now says "Appearance"
  and explains itself on hover, instead of showing only the name of the theme
  it is currently on.
- A Help entry sits next to it and opens the Pia desktop guide in your
  browser, in the language the interface is set to.

## Routines

- A failed run now says why. The run list under a routine shows the reason
  beneath "Failed" — a server or provider message word for word — and
  opening the chat of a failed chat-mode routine shows the request and the
  failure instead of an empty chat.
- A failure Pia Cloud reports mid-answer no longer ends as "The model returned
  no answer." Its actual message reaches the routine's run, the agent run's
  failure card and an interactive chat alike, so a timeout or an upstream
  error reads as what it was.

## Transcription

- Stopping a live or meeting transcription keeps what you just said. Audio the
  microphone had already handed over could be dropped while the recogniser shut
  down, cutting the last words off the transcript.

## Performance

- Leaving a screen and coming back no longer leaves the old copy behind in
  memory. A long session that moves between the chat and the other views used
  to climb into the gigabytes; it now stays flat.
