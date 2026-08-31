# Test fixtures

## `sample-mail.msg`

A synthetic Outlook `.msg` (OLE2/CFB) used by the `.msg` parser tests. `artifacts/` is gitignored,
so the real sample mails there cannot back the `dotnet test` gate — this file can.

It was written by the `cfb` npm package, an implementation independent of anything in this repo, so
it is an oracle for our reader rather than a self-check. It deliberately carries every trap the real
sample proved matters:

| Stream | Why it is here |
|---|---|
| `__substg1.0_1000001F` (PR_BODY, 182 B) | Below the 4096-byte cutoff, so it lives in the **mini stream** — a reader that skips the mini-FAT returns plausible garbage here rather than throwing. German umlauts prove the UTF-16LE decode. |
| `__substg1.0_007D001F` (6098 B) | At/over the cutoff, so it comes from the **regular FAT**. Both allocators are exercised by one file. |
| `__substg1.0_0E04001F` (PR_DISPLAY_TO) | Has a **trailing NUL**; `PR_SUBJECT` in the same file does not. Trailing NULs are inconsistent in real mail and must be trimmed. |
| `__substg1.0_0E03001F` (PR_DISPLAY_CC) | Present but **zero-length** — not the same as absent. |
| `__nameid_version1.0/__substg1.0_10130102` | The **flat-scan trap**. Its content is the literal `FLAT-SCAN-BUG-SENTINEL`. There is no `PR_HTML` at root, so a reader that scans the directory flat instead of walking the red-black tree will surface this sentinel as the message body. A test asserts the sentinel never appears in parser output. |
| `__recip_version1.0_#00000000/…` | A recipient sub-storage, whose `__properties_version1.0` uses a **24-byte** header where the root's uses 32. |
| `__properties_version1.0` (root) | Holds `PR_CLIENT_SUBMIT_TIME` (`0x0039`, PT_SYSTIME) as a FILETIME. |

Expected values are asserted in `tests/Pia.Wpf.Tests/Helpers/MsgReaderTests.cs`.
