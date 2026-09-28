# Pia next

## Privacy

- The end-to-end encryption texts in the setup wizard, Settings → Account and
  the recovery code dialog now say what the encryption covers: your device
  encrypts the content, while IDs, timestamps and order stay readable. They
  also say plainly that encryption cannot be turned off in the app.
- Assistant chats synced before end-to-end encryption was turned on are now
  uploaded again, encrypted, instead of staying readable on the server until
  their next change. This happens when you turn encryption on and, if it is
  already on, once after this update.
- "Sign Out" and "Delete account" now remove the end-to-end encryption key
  from this device, so another account signing in here cannot encrypt under
  it. Signing back in takes it back without setup if this device set up
  encryption or used the recovery code; an approved device needs the code.
- "Approve from another device" now accepts the encryption key only with the
  signature of an active device of your account, and approvals are signed
  over the key they hand over. This needs an up-to-date Pia server; until
  then, and for devices approved earlier, use "Use recovery code".
