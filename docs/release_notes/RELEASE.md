# Pia 1.4

## Vault

- The German UI now calls the vault "Vault" everywhere you can save into it.
  The transcription overlay, the meeting overlay, the meeting details dialog
  and the answer export used to alternate between Vault and Wissensspeicher,
  and only one of them showed an icon. They all say Vault now, and each of
  those buttons carries the same icon.

## Fixes

- Starting a meeting transcription no longer raises Windows Firewall
  "Security Alert" prompts. The browser Pia drives was asking Windows for
  permission to listen for Chromecast and smart-TV devices on the network,
  which it never needed. On a managed PC nobody without administrator rights
  could clear that prompt, and it came back after every browser update —
  including for a scheduled recording with nobody at the keyboard.
