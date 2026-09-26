# Pia next

## Assistant

- On a wide or maximized window the chat stays in a centered column, so your
  own messages sit next to the answers instead of at the far edge.
- The chat switcher at the top left, and the folder buttons on an empty chat,
  now look like buttons instead of plain text, so they are easier to find.
- Answer text is sharp on every line. Single lines of a reply rendered
  blurred, most often the second or third, and which ones changed as the
  chat scrolled.
- Copying a selection from the chat keeps its emoji. They pasted as blank
  spaces, and so did code blocks in answers and @-commands in your own
  messages; only the Copy button handed back the full text. Pasted into Word
  or Outlook, a code block still arrives blank.
- "New chat" asks before it throws away a message you have typed but not
  sent. The "+" under the message box is easy to mistake for attaching a
  file, and one click cleared the draft.

## Help

- Asked about Pia in German or French, the assistant names settings the way
  your screen shows them, such as "Tool-Zugriff" or "Erweiterungen", instead
  of translating the English guide. It can also tell you your hotkeys,
  privacy, Optimize and startup settings, and which version is installed.

## Code blocks

- YAML keeps its indentation, both in answers and in code you paste into a
  message that also uses an @-command. Either could lose its nesting, and the
  Copy button then handed back a file that no longer parsed.
- YAML is syntax-highlighted in code blocks, the way JSON and the other
  languages already were.

## Providers

- A long answer that uses many tools no longer stops with a provider timeout
  when every request was answered in time. The limit covered the whole turn,
  file edits between requests included, so a provider that replied within
  seconds a dozen times over still ran it out.
- A provider's "Timeout (seconds)" accepts up to 600; it stopped at 300.
- A Mistral provider with "Enable web search" and a "Mistral Agent ID" no
  longer ends every answer in an error. The reply arrived in full, but the
  chat was flagged "Error" each time.

## Routines

- A routine with a long, multi-line goal no longer takes over the list. Each
  entry shows the goal on one line; the full text is in the details.
- "Run now" confirms with a short message that the routine has started.

## Administration

- A sign-in method your organization's policy turns off is now refused, not
  just hidden, and anyone still signed in with it is signed out the next time
  Pia starts.

## Models

- Speech, voice and embedding models now download from Pia's own server only.
  When it cannot be reached the download fails instead of quietly pulling the
  file from Hugging Face or GitHub, so a machine that is allowed to reach Pia
  and nothing else behaves the same way every time.

## Privacy

- You can export and delete your cloud account yourself. Settings → Account
  now has "Export data", which saves everything the account holds as a ZIP
  archive, and "Delete account", which offers that export once more before
  it deletes. Your access ends at once; the server removes the data later.
- End-to-end encryption works with a server URL that includes a path, such
  as https://example.com/pia. "Enable End-to-End Encryption", "Check for
  device requests" and the recovery code went to the server's root address
  instead and failed.
