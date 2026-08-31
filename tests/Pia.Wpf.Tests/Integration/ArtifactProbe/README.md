# Declared-artifact probe

Pins how the verifier renders a declared artifact — which strings read as a file reference, and which
outcome arm each candidate path prints. Plan:
[`docs/hermes_checkup/artifact-evidence.md`](../../../../docs/hermes_checkup/artifact-evidence.md).

## What is here

| File | What |
|---|---|
| `DeclarationCorpus.cs` | The corpus: 20 declarations, each with the outcome text the probe prints after the arrow, written out by hand rather than computed. |
| `DeclarationCorpusReplayTests.cs` | Replays the corpus through `AgentVerifier.VerifyAsync` and reads the fact block off the System prompt. Also pins flattening, truncation, the 3-candidate cap, the probe budget and the report cap. |
| `DeclarationClassifierParityTests.cs` | Replays `../../../../scripts/artifact-declaration-cases.json` through the same verifier, so the PowerShell reimplementation of the file-shapedness rule cannot drift from it unnoticed. |
| `NearDuplicateDeliverableTests.cs` | The near-duplicate deliverable hint: the observed real pair fires it, and one theory row per conjunct proves what never does. |

## What this measures

The classifier's rule (which declarations yield candidate paths at all) and the exact arm text and line
shape of each outcome — including whether a candidate prints bare or prefixed with its own token, which is
what makes the fact block parseable or not.

## What this does NOT measure

- The corpus is hand-written. Any share it reports is a property of this file, and it cannot stand in for
  the outstanding measurement of what real runs declare.
- Every corpus row runs against an empty throwaway folder, so a probed candidate always reads `NOT FOUND`.
  The `found` arm is pinned once, as a rendering check.
- The artifact a step reports about *itself* is now probed too — it renders as the `reported:` half of the
  same fact line — but no corpus row exercises it. That channel is pinned in
  `../../Services/AgentVerifierTests.cs` and in `NearDuplicateDeliverableTests.cs`.

## Outcome arms and who pins them

Nine outcome sites, eight distinct strings — the two probe-budget arms share their text — plus the tally
line for declarations past the report cap and the two lines the near-duplicate hint adds.

| Arm | Pinned by |
|---|---|
| `not a file reference` | `DeclarationCorpusReplayTests` (corpus rows, and the report-cap test) |
| `NOT FOUND` | `DeclarationCorpusReplayTests` (corpus rows) |
| `found (<size>, modified <utc>Z)` | `DeclarationCorpusReplayTests.FileNameInProse_ThatExistsOnDisk_RendersTheFoundArm` |
| `found, but it is a folder, not a file` | `../../Services/AgentVerifierTests.cs` — already pinned there |
| `not a resolvable path inside the assistant files folder (not probed)` | `DeclarationCorpusReplayTests` (the sandbox-escape corpus row) |
| `not probed (probe budget reached)`, per candidate | `DeclarationCorpusReplayTests.ProbeBudget_Exhausted_…` |
| `not probed (probe budget reached)`, per declaration (prints bare) | `DeclarationCorpusReplayTests.ProbeBudget_Exhausted_…` |
| `not probed (could not be inspected)` | **nothing pins this.** It needs a stat call to throw, which is not portably forceable — a known hole, not a faked one. |
| `names a vault reference — outside the working folder, not probed` | `../../Services/AgentVerifierTests.cs` — `VerifyAsync_ReportedVaultReference_IsNotReportedAsMissing` |
| `- (<n> further declared artifact(s) not probed — probe budget reached)` | `DeclarationCorpusReplayTests.ReportCap_KeepsTwentyFactLines_AndTalliesTheRest` |
| `reported: <ref> → <arm>`, the second half of a step line | `../../Services/AgentVerifierTests.cs` — `VerifyAsync_DeclaredAndReportedDiffer_RenderOnOneLineDeclaredFirst` |
| `- possible duplicate deliverable: …` and its HINT sentence | `NearDuplicateDeliverableTests` |

Three corpus rows overlap assertions that already live in `../../Services/AgentVerifierTests.cs` (`12.5`,
`v1.0` and a bare `.md` as prose, plus the sandbox-escape arm). They are kept here for arm completeness,
not offered as new coverage.

## Conventions

Namespace is `Pia.Tests.Integration.ArtifactProbe`, mirroring `Integration/Compaction`.

Everything here is an ordinary `[Fact]` / `[Theory]` inside the default gate — no network and no database,
but it does create and delete a throwaway temp directory.

```bash
dotnet test                                                              # the gate; the bar is failed: 0
dotnet test -- --filter-namespace "Pia.Tests.Integration.ArtifactProbe"
```

The suite runs on **Windows or CI only**. Besides the `net10.0-windows` TFM, the containment check the probe
resolves through goes to a Win32 `GetFinalPathNameByHandle` call.

## The harness is duplicated on purpose

The NSubstitute `IAiClientService` + stubbed `ISettingsService` + `VerdictStream` harness in both test files
here is a trimmed copy of the one in `../../Services/AgentVerifierTests.cs`;
`../../Services/AgentVerifierWorkspaceRootTests.cs` is a third copy of the same shape. If it is ever
extracted, `AgentVerifierTests.cs` is the file to fold the others into.

## Two tables, two jobs

`../../../../scripts/artifact-declaration-cases.json` and `DeclarationCorpus.Cases` are not duplicates.
The JSON table is a cross-language contract: one boolean per declaration, shared with the PowerShell
measurement script, which reimplements the rule and must be held to it. `DeclarationCorpus.Cases` pins the
arm *text* and the bare-versus-prefixed rendering form — neither of which a boolean can carry.
