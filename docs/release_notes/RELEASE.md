# Pia 1.4

## Vault

- The German UI now calls the vault "Vault" everywhere you can save into it.
  The transcription overlay, the meeting overlay, the meeting details dialog
  and the answer export used to alternate between Vault and Wissensspeicher,
  and only one of them showed an icon. They all say Vault now, and each of
  those buttons carries the same icon.

## Meetings

- You can now drag a meeting invite onto the "Join a Teams meeting" panel and
  Pia fills in the meeting link for you. Drag it straight out of classic
  Outlook, or drop a saved .msg, .eml or .ics file — no need to open the
  invite and hunt for the join link. Pia picks the actual join link out of the
  invite and ignores the organizer's meeting-options link and the dial-in
  numbers that sit next to it. If the invite carries no Teams link, it says so
  under the box and leaves what you typed alone. Consent is unchanged: filling
  in the link never starts a meeting.

## Live transcription

- The sentence a participant has to say to be transcribed is shorter in all
  three languages, and the German one no longer has a comma in the middle:
  "My name is [Name] and I accept this recording by Pia." Speech recognition
  only accepts the sentence if it hears the whole thing as one utterance, and
  the old, longer wording invited a pause in the middle that split it in two —
  which is why it often took several attempts. The previous wording still
  works, so nobody who learned it is locked out.
- Pia now recognises itself when speech recognition writes its name as
  "Pieer", which the Parakeet model does. That spelling is too far from "Pia"
  for the automatic repair to bridge, so a participant saying the sentence
  perfectly could be refused over nothing but a mis-heard name.
- Each of the three sentences now has a copy button next to it, on its own
  line with its language marked. Paste one into the meeting chat — that is the
  only way somebody joining over system audio ever gets to read it, and the
  pre-start panel it used to live in is only ever on your screen.
- The same sentence, in your UI language, now stays at the top of the
  transcription window for the whole session, with its own copy button. It
  used to appear only before you pressed Start, so by the time you noticed a
  participant was not being transcribed it was gone.
- A short chime now confirms each accepted consent sentence, so you hear that
  a participant was let in without watching the chips. You hear it, not the
  far end: your conferencing app removes your own loudspeaker output from what
  it sends out.
- A refused consent sentence now records which of the four required parts was
  missing — the name introduction, the acceptance, the reference to the
  recording, or the reference to Pia. It goes in the log you would attach to a
  support request, and it names the part only, never anything anyone said.

## Assistant

- The files an agent step changes now collapse into one line. A step that
  wrote twenty files used to leave twenty bordered diff cards behind, each
  with its own "Auto-approved" footer under it, so scrolling back through a
  finished run meant scrolling past a screen of near-identical boxes. Applied
  edits are now folded into a single "N file(s) changed" row with the total
  added and removed lines; open it to see one line per file, and open a file
  to see its diff exactly as before. A write still waiting for your approval
  keeps its own full card and its own buttons, and a write you declined stays
  visible on its own instead of disappearing into the roll-up.

## Fixes

- Starting a meeting transcription no longer raises Windows Firewall
  "Security Alert" prompts. The browser Pia drives was asking Windows for
  permission to listen for Chromecast and smart-TV devices on the network,
  which it never needed. On a managed PC nobody without administrator rights
  could clear that prompt, and it came back after every browser update —
  including for a scheduled recording with nobody at the keyboard.
