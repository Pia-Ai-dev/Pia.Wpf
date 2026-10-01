# Pia

## Desktop

- Pia now opens centred on the main screen when the monitor it was last on
  is disconnected, instead of off-screen and out of reach. A window
  position on a monitor left of or above the main one is now remembered.

## Security

- Your local history (chats, prompts, memories, to-dos) is now encrypted
  on disk with a key bound to your Windows account. The first start of this
  version converts it and keeps the unencrypted original as a zip in Pia's
  Backups folder until you delete it; an older version only opens that copy.
- Links in assistant replies open only web pages and e-mail addresses.
  Links to Windows settings, other programs or files no longer open on a
  click.
- Pia installs an update only when every program file in it is signed by
  neo42 or by the makers of the components Pia ships with.
- A new device shows which device approved it and that device's
  fingerprint, and takes the key only after you choose "It matches".
  Devices set up earlier ask once, at their next sign-in.
- The trustSelfSignedCertificates policy now relaxes certificate checks for
  your Pia server only, never for other hosts.

## Privacy

- Settings > Cloud Sync shows "Your synced data is not end-to-end
  encrypted" while an account syncs without it; "Turn on encryption" starts
  the usual setup.
- Copying your recovery code keeps it out of Windows clipboard history and
  the cloud clipboard.
- Dictation recordings stay in Pia's own data folder, and leftovers from an
  interrupted recording are deleted at the next start.
- Pia's log files no longer record plugin names, the programs behind
  captured windows, or what you searched for in memories.
