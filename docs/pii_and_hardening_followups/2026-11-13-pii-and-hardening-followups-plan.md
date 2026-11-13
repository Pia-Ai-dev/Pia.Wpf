# PII and hardening follow-ups: plan

**Status:** Planned
**Owner:** Marco Altmann
**Written:** 2026-11-13
**Origin:** GitHub issues #92 (phone pattern), #88 (User-Agent) and #93 (tool-result tokenization);
test focus 3 of [../e2ee_pentest/2026-10-12-e2ee-pentest-brief.md](../e2ee_pentest/2026-10-12-e2ee-pentest-brief.md);
the *Not yet planned* lists of
[../account_switch_cleanup/2026-10-28-review-findings-fix-checklist.md](../account_switch_cleanup/2026-10-28-review-findings-fix-checklist.md)
and
[../installer_runtime_and_recall/2026-11-03-installer-runtime-and-recall-checklist.md](../installer_runtime_and_recall/2026-11-03-installer-runtime-and-recall-checklist.md).

Tracking: [2026-11-13-pii-and-hardening-followups-checklist.md](2026-11-13-pii-and-hardening-followups-checklist.md).

Seven small steps on `feature/pii-and-hardening-followups`, one commit each, in the order below. Step 6
changes what PII protection masks and is last so it can be reviewed, or dropped, on its own.

## Rules for every step

- The build reports `0 Warning(s)` in Debug and Release (`dotnet build -t:Rebuild -v:n`, again with
  `-c Release`), and the built test exe reports `Failed: 0`.
- A new string goes into the base, `de` and `fr` resx files; `LocalizationTests.AllTranslations_MustBeComplete`
  fails otherwise.
- Comments: one short line at most, no history, no issue numbers. Bring the comments of a member you
  touch into line.
- Logs carry ids, counts and field names only. No tool results, tokens, names or URLs.
- No edit to `docs/release_notes/RELEASE.md`.

## 1. Mask `(+49) …` and `Tel.+49 …` (#92)

With PII protection on, `(+49) 170 1234567` and `Tel.+49 30 1234567` reach the provider unmasked.

**Change.** `StructuredPiiDetector.PhoneRegex` (`src/Pia.Wpf/Services/StructuredPiiDetector.cs`), first and
third pattern line:

```
(?<![\w+(])(?<!\w[-/,])(?<!\w\.(?!\+))
(?!\(?\d{1,2}[-/]\d{1,2}[-/]\d{2,4}(?!\d))
(?:\(?\+|\(|(?=0))\d[\d\p{Zs}\t()/-]{5,22}\d
(?![-/.,]?\w)
```

A dot after a word character still blocks a match unless a `+` follows, and a match may start at the
parenthesis of `(+`. .NET evaluates the lookahead inside the lookbehind at the candidate start, so the
pattern behaves the same as under ICU. Checked with the .NET 10 regex interpreter against every
`DetectPii_*` case: all positives keep their value, all negatives stay empty. If the source generator
rejects the nested lookaround, `(?:(?<!\w\.)|(?=\+))` in place of `(?<!\w\.(?!\+))` is equivalent.

**Tests** (`StructuredPiiDetectorTests`). `DetectPii_FindsPhoneNumber` gains
`("Ruf an: (+49) 170 1234567.", "(+49) 170 1234567")`, `("Tel.+49 30 1234567", "+49 30 1234567")` and
`("Tel.:+49 30 1234567", "+49 30 1234567")`. `DetectPii_LeavesNumbersThatAreNotPhoneNumbersAlone` gains
`"Tel.030 1234567"`.

**Known limits, kept on purpose.** `Tel.030 …`, `Fon/030 …`, `Tel.(+49) …`, `555-123-4567` and dotted
forms stay unmasked; recovering them brings back the version, date and IP false positives. A
parenthesised signed number of seven or more digits (`(+1234567)`) and a `+` number glued to a dotted
word (`build 1.+4930123456`, `Nr.+1234567890`) are now masked; over-masking is accepted because the
token round trip restores the text.

## 2. Bound the Argon2id parameters of the recovery copy

`RecoveryCodeService.UnwrapUmkFromRecovery` hands `KdfMemoryCostKb`, `KdfTimeCost` and `KdfParallelism`
from the server's blob to Argon2id unchecked. A hostile server can make the client allocate without
limit. The server checks these values on write only and serves stored ones as they are.

**Limits.** The ceilings mirror the server's write rule (`KeyMaterial` in Pia): memory 262144 KiB, time 10,
parallelism 16. The lower bounds are costs of at least 1 and memory of at least 8 × parallelism, the
RFC 9106 minimum that Argon2id needs. The salt must be present and decode as base64. There is no floor at the defaults (65536 / 3 / 4): the server accepts down to
19456 / 1 / 1, so another client may store less. Weak parameters also gain a hostile server nothing,
since the derived key never leaves the device and a forged ciphertext cannot open without the code.

**Change.**
- New `Services/E2EE/RecoveryCopyRejectedException` (`InvalidOperationException`; the message names the
  field and, for the three costs, the value and range).
- `RecoveryCodeService`: `internal const` ceilings and an `internal static CheckedKdfSalt(blob)` that runs
  the range checks and the memory floor of 8 × parallelism, then decodes the salt.
  `UnwrapUmkFromRecovery` calls it first, before any decode or KDF.
- `E2EEOnboardingViewModel.ActivateWithRecoveryCodeAsync`: catch the new type before the generic catch
  and in the retry's inner catch, log
  `LogWarning(ex, "The server's recovery copy was refused")`, show `E2EE_Onboarding_Recovery_CopyRejected`
  and return to the code entry state.
- `DeviceManagementService.ActivateViaRecoveryAsync` needs no change: the unwrap precedes the key store
  and every POST.

**String** `E2EE_Onboarding_Recovery_CopyRejected` (`ViewStrings`):
- en: Pia did not use the recovery data stored on the server because it is not valid. Your recovery code
  was not checked. Confirm this device from another device, or contact your administrator.
- de: Pia hat die auf dem Server gespeicherten Wiederherstellungsdaten nicht verwendet, weil sie ungültig
  sind. Dein Wiederherstellungscode wurde nicht geprüft. Bestätige dieses Gerät von einem anderen Gerät
  aus oder wende dich an deinen Administrator.
- fr: Pia n'a pas utilisé les données de récupération enregistrées sur le serveur, car elles ne sont pas
  valides. Votre code de récupération n'a pas été vérifié. Confirmez cet appareil depuis un autre
  appareil ou contactez votre administrateur.

**Tests.**
- `RecoveryCodeServiceTests`: `UnwrapUmk_RefusesCostsAboveTheCeiling` (262145, `int.MaxValue`, time 11,
  parallelism 17), `UnwrapUmk_RefusesCostsArgon2idCannotRunWith` (0, negative, and memory below
  8 × parallelism: 3 / 1 / 1, 63 / 1 / 16, 64 / 1 / 16), `UnwrapUmk_RefusesASaltThatIsNotBase64`,
  `CheckedKdfSalt_AcceptsTheCeilingItself`, `CheckedKdfSalt_AcceptsEightKibPerLane` (8 / 1 / 1,
  128 / 1 / 16),
  `UnwrapUmk_OpensACopyWrappedBelowTheDefaults` (8192 / 1 / 1).
- `DeviceManagementServiceRecoveryTests`:
  `ARecoveryCopyWithAnOversizedCost_IsRefusedBeforeAnythingIsStoredOrSent`.
- `E2EEOnboardingViewModelTests`: `ARefusedRecoveryCopy_IsNotReportedAsAWrongCode`.

**Step 2b, behind gate G1.** Also refuse a salt that is not exactly 32 bytes and costs below
19456 / 1 / 1, the server's full write rule. Two constants and one length check in `CheckedKdfSalt`.

## 3. Refuse *Run now* while a local-data clear is pending

The scheduler does not start while the reset marker exists, but `IScheduledJobRunner.RunNowAsync` still
runs a job from the Routines page or the assistant's `run_routine` tool.

**Change.**
- `ScheduledJobRunNowResult.ResetPending`.
- `ScheduledJobBackgroundService`: optional constructor parameter
  `ILocalDataResetIndicator? resetIndicator = null` (last, as `SyncClientService` takes it). First
  statement of `RunNowAsync`: when a reset is pending, log
  `"Run-now refused for job {Id}: a local-data clear is pending"` and return `ResetPending`, before any
  job lookup.
- `RoutinesViewModel.RunNowAsync`: arm `ResetPending` → `Settings_ScheduledJobs_RunResetPending`.
- `ScheduledJobToolHandler.PrepareRunRoutine`: arm `ResetPending` → `Tool_Routine_Exec_ResetPending`.
- Remove the *Refuse Run now* bullet from the *Not yet planned* list of the account-switch checklist.

**Strings.**
- `Settings_ScheduledJobs_RunResetPending` (`ViewStrings`)
  - en: Not run: Pia has not finished removing local data. Restart Pia first.
  - de: Nicht ausgeführt: Pia hat das Entfernen der lokalen Daten noch nicht abgeschlossen. Starte Pia
    zuerst neu.
  - fr: Non exécutée : Pia n'a pas encore terminé la suppression des données locales. Redémarrez
    d'abord Pia.
- `Tool_Routine_Exec_ResetPending` (`MessageStrings`, `{0}` is the routine name)
  - en: Routine {0} was not started: Pia has not finished removing local data. Tell the user to restart
    Pia first.
  - de: Routine {0} wurde nicht gestartet: Pia hat das Entfernen der lokalen Daten noch nicht
    abgeschlossen. Bitte den Nutzer, Pia zuerst neu zu starten.
  - fr: La routine {0} n'a pas été démarrée : Pia n'a pas encore terminé la suppression des données
    locales. Demandez à l'utilisateur de redémarrer d'abord Pia.

**Tests.**
- `ScheduledJobBackgroundServiceTests`: `RunNowAsync_RefusedWhileALocalDataClearIsPending_AndTouchesNothing`,
  `RunNowAsync_WithNoClearPending_StillDispatches`.
- `RoutinesViewModelTests`: `ResetPending` row in `RunNow_ConfirmsNothingThatDidNotStart`;
  `RunNow_SaysAClearIsPending_NotThatTheJobIsGone`.
- `ScheduledJobToolHandlerTests.RunRoutine_MapsEveryOutcomeToItsOwnResult`: row
  `(ResetPending, "Tool_Routine_Exec_ResetPending")`.

## 4. Send a User-Agent (#88)

The server records the `User-Agent` of every sign-in. The desktop client sends none, so its sign-ins show
no client.

**Change.**
- `AppVersionInfo.UserAgent`: `Pia/<Version> (Windows NT <major>.<minor>)`. `Version` is the informational
  version without the `+commit` suffix.
- `Bootstrapper`, inside `ConfigureHttpClientDefaults`:
  `builder.ConfigureHttpClient(client => client.DefaultRequestHeaders.UserAgent.TryParseAdd(AppVersionInfo.UserAgent));`

The server reads `Pia/<1-4 digits>.<1-4 digits>` as "Pia desktop major.minor" and answers "Other" when the
text contains bot, spider, crawl, slurp, headless or `+http`. Every client that talks to Pia Server comes
from the factory; the `new HttpClient` instances (provider SDK transports, the OpenRouter catalogue,
provider model lists, the plugin endpoint ping) talk to third parties and stay as they are. A default
header applies only when a request carries none, so the OpenAI SDK keeps its own.

**Tests.**
- `AppVersionInfoTests.UserAgent_HasTheShapeTheServerReads`.
- `HttpClientDefaultsTests`: `EveryFactoryClient_CarriesThePiaUserAgent` (runs the production
  `HttpClientFactoryOptions.HttpClientActions` for the default and the `meeting-redirect` name on a plain
  `HttpClient`), `ARequestWithItsOwnUserAgent_KeepsIt`, `AnOpenAiCompatibleRequest_KeepsTheSdkUserAgent`.

**For the PR description.** The header rides on existing calls: no new endpoint, method or body, no change
to an account's devices or keys, nothing overwritten or deleted. The bearer token is enough. Third-party
recipients of the header: asset download hosts, the Teams meeting-link resolver, external plugin icon
hosts; none depends on it. Server side effect: a desktop session's `DeviceName` becomes the User-Agent
text.

## 5. Docs: the server bump is done

Pia fills `IsE2EEEnabled` in `LocalAuthService` and `LoginCodeExchangeService` and pins `lib/Pia.Wpf` at
`ede8dae3`.

- Tick *Server bump* in
  [../e2ee_pentest/2026-10-20-e2ee-downgrade-hardening-checklist.md](../e2ee_pentest/2026-10-20-e2ee-downgrade-hardening-checklist.md).
- In [../e2ee_pentest/2026-10-20-e2ee-downgrade-hardening.md](../e2ee_pentest/2026-10-20-e2ee-downgrade-hardening.md)
  set `**Status:** Done` and replace *Server follow-up* with a *Server side* section in the present tense:
  where the field lives, that Pia fills it, that a missing field reads as no evidence, and that the pin
  carries `e2eeRequiredAccountIds` in `ClientPolicyContract.DeniedKeys`.
- In the account-switch checklist's *Not yet planned* list, cut the *Server bump* bullet down to the
  WinWright B6 login-body re-run.

## 6. Tokenize tool results and replayed assistant text verbatim (#93)

Tool results are matched like the user's own text: any case, as a substring, plus a fuzzy pass over custom
keywords. Detokenizing restores the stored value, so a file tool can write back text that differs from
what it read: `max(a, b)` returns as `Max(a, b)`, `Schmitt.pdf` as `Schmidt.pdf`.

**Rule.** In tool results and in the assistant's replayed text a stored value matches only when
- it occurs with exactly its stored characters (ordinal comparison), and
- the character before it is not a letter, combining mark or digit, or is the end of an escape `\n`,
  `\r`, `\t`, `\b`, `\f` or `\uXXXX`, and the character after it is not a letter, mark or digit. Each
  side applies only where the value itself starts or ends with a letter or digit.

Underscore, hyphen, dot, slash, brackets and quotes separate words. There is no fuzzy pass. Existing
tokens and GUIDs are left whole. Emails and phone numbers the detector finds are still tokenized, each
spelling under its own token, so a token always stands for exactly the text it replaced.

With "Max" and the keyword "Schmidt" stored: `max(a, b)`, `maximum`, `MaxLength`, `maria-schmidt.md` and
`Schmitt.pdf` stay as they are; `Max(a, b)`, `Max_Mustermann.pdf` and `Schmidt.pdf` are tokenized and
come back identical.

The user's own messages keep today's matching.

**Trade-off.** A known name that appears in a tool result or a replayed reply in another case, with a
typo, or glued to letters (`Schmidts`, `SCHMIDT`) reaches the provider unmasked.

**Change.**
- `ITokenMapService.TokenizeVerbatim(string)`.
- `TokenMapService`: an ordinal `_exactValueToToken` map beside the case-insensitive one, filled by
  `Tokenize`; `TokenizeVerbatim` (mask GUIDs, one regex pass over the stored values with the boundaries
  above that skips tokens and GUID placeholders, then the detector's matches through `TokenizeExact`,
  then unmask); `TokenizeExact` mints a token for a spelling that has none. `Clear` resets the new state.
- `TokenizingAiClientService.TokenizeMessages`: assistant text goes to `TokenizeVerbatim`, user text stays
  on `TokenizeStructuredResult`.
- `TokenizingAiClientService.WrapToolHandler`: a string result goes to `TokenizeVerbatim`. An object
  result is tokenized leaf by leaf (string values and property names) and then written as JSON, because
  the serialized text escapes umlauts, `+` and line breaks, which no stored value matches.
- `HeadlessTurnExecutor.Tokenize` (replayed results and seeded arguments): `TokenizeVerbatim`.
- `AiFeedbackService`, `SendRequestAsync` and `OptimizeViaPiaCloudAsync` carry user text and stay.

**Tests.**
- `TokenizedToolPipelineTests`, real token map and detector, through
  `TokenizingAiClientService.GetChatCompletionWithToolsAsync`, with `read_file`'s real output shape:
  `ACodeFileThatSpellsAKnownName_IsWrittenBackByteForByte`,
  `AnEditAnchorCopiedFromTheReadResult_MatchesTheFile`,
  `ANearMissOfACustomKeyword_KeepsItsSpelling`,
  `AnExactWholeWordName_AndNewContactDetails_AreStillMasked`,
  `AnObjectResultWithALineBreakBeforeAName_IsMasked`,
  `TheUsersOwnMessage_KeepsCaseInsensitiveAndFuzzyMatching`,
  `AReplayedAssistantReply_IsTokenizedVerbatim`.
- `TokenMapServiceTests`: `TokenizeVerbatim_MatchesWholeWordsInExactCaseOnly`,
  `TokenizeVerbatim_ThenDetokenize_ReturnsTheInput`, `TokenizeVerbatim_LeavesExistingTokensAndGuidsWhole`,
  `TokenizeVerbatim_GivesACaseVariantOfAKnownEmailItsOwnToken`.
- Every `Substitute.For<ITokenMapService>()` that stubs `TokenizeStructuredResult` gets the same stub for
  `TokenizeVerbatim`: `TokenizingAiClientServiceTests`, `UnattendedApprovalParkTests`,
  `TokenizationLatchTests`.

**Known limits.** Text the user types stays on the fuzzy path, so a file name typed with a near-miss of a
keyword is still rewritten; the same holds for user-role messages the app writes itself. A replayed
object result in a headless run is tokenized as JSON text, where escaped umlauts and `+` do not match.

## 7. Stop *Regenerate embeddings* at the first unavailable error

`VaultViewModel.ExecuteRegenerateEmbeddings` logs one warning per memory when embeddings are unavailable.

**Change.** Catch `EmbeddingUnavailableException` before the generic catch in the loop, log
`"Regenerating embeddings stopped after {Processed} of {Total}: embeddings are unavailable"` and leave the
loop. The existing count snackbar reports the result. Remove the matching bullet from the installer
checklist's *Not yet planned* list.

**Test.** `VaultViewModelTests.RegenerateEmbeddings_StopsAtTheFirstUnavailableError`.

## Decision gates

| Gate | Question | Blocks |
|---|---|---|
| G1 | Did Pia.Mac ever store a recovery copy with a salt other than 32 bytes, or with Argon2id costs outside 19456–262144 KiB / 1–10 passes / 1–16 lanes? | 2b |
| G2 | May a known name in another case, with a typo, or glued to letters reach the provider unmasked in tool results and replayed replies, in exchange for byte-exact file round trips? | 6 |
