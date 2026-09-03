# Pia 1.4

## Routines

- A failed run now says why. The run list under a routine shows the reason
  beneath "Failed" — a server or provider message word for word — and
  opening the chat of a failed chat-mode routine shows the request and the
  failure instead of an empty chat.
- A failure Pia Cloud reports mid-answer no longer ends as "The model returned
  no answer." Its actual message reaches the routine's run, the agent run's
  failure card and an interactive chat alike, so a timeout or an upstream
  error reads as what it was.

## Performance

- Leaving a screen and coming back no longer leaves the old copy behind in
  memory. A long session that moves between the chat and the other views used
  to climb into the gigabytes; it now stays flat.
