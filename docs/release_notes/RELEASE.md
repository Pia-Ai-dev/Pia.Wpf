# Pia 1.4.205

## Window behaviour

- Minimizing Pia now sends it to the taskbar, like any other app. Closing the
  window with the X is what puts it away into the notification area.

## Routines

- A routine whose model answers with nothing now gets asked once more before
  the run is given up on. The second ask carries everything the run already
  gathered and takes no further tool steps, so a run that used to end as "The
  model gave no answer" finishes with one.
- A run that still ends without an answer records what the model did send —
  its finish reason, and how much of the turn went into reasoning — so a log
  attached to a support mail says why.
