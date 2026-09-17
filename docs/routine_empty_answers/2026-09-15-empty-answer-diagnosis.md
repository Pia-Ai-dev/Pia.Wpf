# Routines ending as "The model gave no answer"

**Status:** client mitigation and server fix landed, both unmerged; the cause
is still inferred rather than confirmed from a server log
**Owner:** Marco Altmann
**Written:** 2026-09-15
**Origin:** user report — "MongoDB Checkup" routine failing on 09-14 09:00 and
09-15 09:00, succeeding on 09-10, 09-11 and 09-14 10:14

## What the run did

Scheduled job `851c15f5`, run `469fe4b3`, chat `efbae788`, from
`artifacts/pia-2026-09-15.log` (INFO level) and the server's token-usage table.
Times below are local (CEST); the server table is UTC.

| local | what |
|---|---|
| 09:00:07.6 | client POST 1 to `/api/ai/chat`, 42 tools offered |
| 09:00:25 | server ran `websearch:search` inside request 1 |
| 09:00:36.9 | client round 1 ends: one tool call, `recall` |
| 09:00:37.0 | client POST 2, 200 headers in 39 ms |
| 09:00:53 · 09:01:16 · 09:01:35 | server ran `websearch:search` three times |
| 09:01:37.2 | client: "produced empty content" → run Failed |

The server billed request 2 as `chat / aki-io-glm-5-3 / stop / in 52504 /
out 410 / 3 tools`. No error frame reached the client: the
`PiaCloudStreamException` path added for relayed upstream errors did not fire,
and the failure reason recorded was the generic `Empty response`.

So: **the server completed request 2 without an error and the client received
no visible text from it.** The last server-side round took ~2 s, which is short
for a 100+ token answer.

## The leading hypothesis

`ToolChatOrchestrator.MaxServerToolRounds` is **3**, and request 2 burned
exactly three server rounds on `websearch:search`. That drops the loop into the
iteration-cap branch, which differs from every other exit:

- `ContentHandbackSse` reads only `choices[0].message.content`. A final buffered
  call that answers in `reasoning_content`, or that returns another tool call,
  yields `content: ""` — a valid-looking chunk carrying nothing.
- Unlike the WPF client's own wrap-up, the cap branch adds **no "answer now"
  nudge** before `CallRawAsync`, so the model is free to reach for the search
  tool a fourth time.
- Surviving *client* tool calls in that buffered response are dropped;
  the buffered `RunAsync` path hands them back intact.

This is consistent with everything observed, but it is not proven. Two other
shapes produce the same client-side symptom and the same clean server bill:

- the final streaming round carries only `reasoning_content` chunks, so
  `contentLines` is non-empty but holds no `content`;
- the upstream stream is cut with no finish reason — `RunStreamingAsync` bills
  `roundFinish ?? "stop"`, so a null finish is indistinguishable from a real
  one in the usage table.

## What settles it

From the server log for 2026-09-15 07:00:37Z–07:01:38Z:

- does `Server tool streaming loop hit the iteration cap (3)` appear?
- the `Tool streaming round N: …` lines (tool count, finish reason per round);
- every `Stream completed: finishReason=… hasTokenUsage=…` — a `<null>` finish
  means the upstream stream was cut rather than finished.

## Server change made

`feature/cap-round-forced-answer` in `Pia-Ai-dev/Pia`, commit `2b1538c1`.

Both cap branches of `ToolChatOrchestrator` now spend one final call with the
tools removed and a message telling the model to answer from what it has — the
buffered one used to return the last response, which is a tool call.
`ContentHandbackSse` falls back to `reasoning_content` when `content` is empty
and hands back a surviving client tool call instead of dropping it. A streaming
round that ends with no chunk at all now warns, and `roundFinish ?? "stop"` is
gone, so the usage table can tell a cut stream from a finished one.

`MaxServerToolRounds` is left at 3. If research routines keep reaching the cap,
raising it is the next lever — the forced answer round makes reaching it
survivable, not free.

## Client change made

`AiClientService` counts the visible characters a turn produced. A turn that
ends with no tool calls and no visible text now spends one tool-less re-ask
(the same wrap-up call round exhaustion already used, with its own nudge text)
before the turn is reported empty, and logs a WARN naming the finish reason,
the reasoning character count and the response content types first.

`feature/empty-answer-reask`, commit `66d6fdfe`.

This is a mitigation, not the fix: it converts a nondeterministic empty answer
into a second chance on every provider, and it makes the next occurrence
diagnosable from a release log.

## Unrelated, seen in the same run

`MemoryService.RecallAsync` threw `fts5: syntax error near "."` and fell back
to the other tiers (10 hits still returned). Any recall query containing an
unquoted `8.0` hits this, so this routine hits it on every run.
