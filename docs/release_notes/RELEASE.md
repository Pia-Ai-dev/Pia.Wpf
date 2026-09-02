# Pia 1.4

## Vault

- The German UI now calls the vault "Vault" everywhere you can save into it.
  The transcription overlay, the meeting overlay, the meeting details dialog
  and the answer export used to alternate between Vault and Wissensspeicher,
  and only one of them showed an icon. They all say Vault now, and each of
  those buttons carries the same icon.

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
