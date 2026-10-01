# Pia

## Knowledge bases

- Knowledge-base managers can keep their group's knowledge bases current
  from the chat: read documents, add .txt or .md files, replace or remove
  documents and edit the description that tells Pia when to search.
  Changes ask first, until you pick "Allow this session" or "Always allow".
- The new "kb-manager" plugin is off by default and only listed for users
  your administrator or organisation manager made knowledge-base manager.
- The new persona "Pia · KB Curator" plans and explains knowledge-base
  changes step by step. It shows in the persona picker only for
  knowledge-base managers.

## Assistant

- Referring to a file with @Files no longer limits the assistant to file
  tools: in the same message it can also use MCP servers, Git, memory and
  the rest. Combined with another tag such as @Todo, the message still gets
  only the tools of the tagged areas.

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
