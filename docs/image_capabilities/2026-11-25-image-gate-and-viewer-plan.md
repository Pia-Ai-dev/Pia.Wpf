# Image Gate Fix and Image Viewer — Implementation Plan

**Status:** Ready for execution
**Owner:** Marco Altmann
**Written:** 2026-11-25
**Origin:** GitHub issue #96 ("Image capabilities in the desktop client"), which is the client part of
ADO User Story 8235. This plan covers #96's steps **0**, **A1** and **A2**. The rest is tracked in
[2026-11-25-image-capabilities-checklist.md](2026-11-25-image-capabilities-checklist.md).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Tick the matching step in [2026-11-25-image-capabilities-checklist.md](2026-11-25-image-capabilities-checklist.md) in the commit that lands it.

**Goal:** The composer image gate checks the provider the turn will actually run on. Any image the
user attached, in the composer or in a sent message, opens in a full-window viewer with zoom, pan,
previous/next, copy and save.

**Architecture:**
- **Gate.** `AssistantViewModel` gets one private resolver that the attachment budget already uses
  (active persona, then `AssistantTurnProvider.ResolveAsync`). The image gate reads it instead of the
  mode default.
- **Viewer.** A new `ImageViewerOverlay` UserControl sits in `MainWindow` beside the dialog host. It
  is reached through an `IImageViewerHost` service, the same way `DialogOverlayHost` is reached through
  `IDialogOverlayService`, and it is driven by a WPF-free `ImageViewerViewModel`.
- **Opening it.** `AssistantViewModel` opens the viewer through a new `IDialogService` method, so its
  constructor, which 16 test files call, does not change.

**Tech Stack:** .NET 10 WPF, WPF-UI, CommunityToolkit.Mvvm, xunit v3, NSubstitute, NetArchTest.

**Spec:** GitHub #96, sections "Findings to fix independently" (step 0) and "A — image viewer".

## Why this slice

- **Step 0** is still live on `develop` (`AssistantViewModel.cs:2166`). Both #96 and ADO 8235 say to
  fix it first.
- **A1 and A2** need nothing from the server. B1–B4 wait for the output-image wire contract.
- **PR #99 overlap.** PR #99 (`feature/unified-assistant-flow`) changes `AssistantView.xaml` only in
  hunks between lines 101 and 1115. The thumbnail templates sit at lines 31–48 and 636–666, and the
  gate and its four call sites are unchanged in #99.
- **Files that will conflict with #99:** `ViewStrings*.resx`, `ViewAutomationIdTests.cs` (the
  `AssistantView` row) and `ui-automation-playbook.md`. Whichever PR merges second updates the
  `AssistantView` counts and puts its resx keys beside related ones, never at the end of the file.

## Global Constraints

- **Branches.** Through the `release-flow` skill, from `develop`. Use **two PRs**: Task 1 on
  `feature/image-gate-turn-provider`, so the data-boundary fix can ship alone, and Tasks 2–4 on
  `feature/image-viewer`, branched after Task 1 merges, or from Task 1's branch if it has not.
- **Zero warnings** in a Debug and a Release Rebuild (`dotnet build -t:Rebuild -v:n`, then
  `-c Release`). Read the `N Warning(s)` summary line.
- **Tests run from the built exe**, redirected and never piped, with one filter kind per run:
  `tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe -class "<FQN>" > test.log 2>&1`.
  The final gate is an unfiltered run with `Failed: 0`. Never `dotnet test`.
- **Architecture rules** (`tests/Pia.Wpf.Tests/Architecture/DependencyInjectionTests.cs`):
  - A ViewModel outside `AssistantViewModel` must not depend on `System.Windows`, so
    `ImageViewerViewModel` handles bytes and never `BitmapSource`.
  - ViewModel constructors take only interfaces, value types, strings, records, delegates or
    ViewModels.
- **AutomationIds.** Every new button carries an `AutomationProperties.AutomationId`. Per-item ids are
  bound to `ImageAttachment.Id`. Prefixes: `ImageViewer_*` for the viewer,
  `Assistant_OpenPendingImage_{0}` for the composer, `Assistant_OpenSentImage_{0}` for the bubble. No
  existing id starts with any of them. Add the `ViewAutomationIdTests` rows and the playbook entries
  in the same change.
- **Strings** go in en/de/fr. Write umlauts and accents as literal characters, never `\u` escapes.
- **Privacy.** A source path is a user-named item: log it only through `SensitiveDebug`.
- **Comments:** none by default; one short line only when the reason is hidden.
- **Unchanged:** input images still exist only for the live session (decision A-D7), and the viewer
  shows the stored `JpegBytes`, the ≤1568 px version the model received.
- **Requests to the server:** none new. Say so in both PR descriptions.

## Review Focus

1. **A persona pinned to a bring-your-own-key provider while the mode default is Pia Cloud.** Paste,
   drop, screen capture and Send must all refuse. Pinned in Task 1 (availability and send).
2. **Switching persona after attaching an image.** Expected: Send refuses with the provider warning
   and the image stays in the composer. Pinned in Task 1.
3. **A copy while another app holds the clipboard.** `Clipboard.SetImage` throws. Expected: a warning
   snackbar, no crash, and the viewer stays open. Pinned in Task 2.
4. **Previous/next at either end, and a single-image message.** Expected: the buttons are disabled at
   the ends and hidden for one image, and the keyboard arrows do nothing there. Pinned in Task 2.
5. **A dialog opening while the viewer is up** (for example the screen-capture hotkey), or opening
   the viewer while a turn streams. Expected: the dialog draws above the viewer and takes Esc first.
   The next Esc closes the viewer, and focus returns to the thumbnail that opened it. Checked in the
   Task 5 walkthrough; the layering and focus return are too thin to unit-test.

**Known gap, not closed here:** images already in a sent message are re-sent on every later turn
(`AssistantMessage.BuildChatMessage`, `ChatSession.cs:417-445`). So after a switch to a BYOK-pinned
persona, a text-only follow-up still carries earlier pictures to that provider. That is checklist step
**0b**, gated on an owner decision (refuse the turn, or strip the images with a placeholder). Neither
PR may claim that the D2 boundary is fully closed.

---

### Task 1: The image gate reads the turn's provider

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/AssistantViewModel.cs`. Touch `AvailableAttachmentTokensAsync`
  (about lines 2060–2077) and `AssistantProviderTakesImagesAsync` (2166–2178).
- Modify: `tests/Pia.Wpf.Tests/ViewModels/AssistantViewModelScreenCaptureTests.cs`. Promote the
  inline `Substitute.For<IPersonaService>()` in `CreateSut` to a field `_personas`.

**Interfaces:**
- Produces: `private async Task<AiProvider?> ResolveTurnProviderAsync()`. It reads settings, calls
  `_personaService.ResolveActiveAsync(WindowMode.Assistant, settings.UserOperatingMode ?? UserOperatingMode.Personal)`,
  then `AssistantTurnProvider.ResolveAsync(persona, _providerService)`, the same three calls as
  `ChatSessionManager.cs:800-805`. Both callers keep their own `try/catch`. The gate keeps its
  refuse-on-failure behaviour.

**Refresh.**
- **A persona switch** already refreshes availability: `OnActivePersonaChanged` →
  `PersistActivePersonaAsync` → `SettingsService.SaveSettingsAsync`, which raises `SettingsChanged`
  (`SettingsService.cs:55-60`), and the VM refreshes on that event.
- **A persona edit does not.** Changing the active persona's pinned provider raises only
  `IPersonaService.PersonasChanged`. `OnPersonasChanged` (`AssistantViewModel.cs:857`) reloads the
  personas but never refreshes the gate. Add `StartScreenCaptureAvailabilityRefresh()` to that
  handler. The sync pull raises the event every cycle; the refresh is one provider lookup.

- [ ] **Step 1: Write the failing tests** in `AssistantViewModelScreenCaptureTests`, under "the
  provider gate". The helper `UsePinnedByokPersona()` sets up three things:
  - `_providers.GetProviderAsync(byokId)` returns an `AiProviderType.OpenAI` provider.
  - `_personas.ResolveActiveAsync(WindowMode.Assistant, Arg.Any<UserOperatingMode>())` returns
    `new Persona { Name = "P", SystemPrompt = "", PreferredProviderId = byokId }`.
  - The mode default stays Pia Cloud.

```csharp
[Fact]
public async Task Availability_FollowsThePersonasPinnedProvider_NotTheModeDefault()
{
    UsePinnedByokPersona();
    var vm = CreateSut();
    await vm.PendingScreenCaptureAvailabilityRefresh;
    Assert.False(vm.IsScreenCaptureAvailable);
}

[Fact]
public async Task Availability_RefreshesWhenThePersonaSwitchIsSaved()
{
    var vm = CreateSut();
    await vm.PendingScreenCaptureAvailabilityRefresh;
    Assert.True(vm.IsScreenCaptureAvailable);

    UsePinnedByokPersona();
    _settings.SettingsChanged += Raise.Event<EventHandler<AppSettings>>(_settings, new AppSettings());
    await vm.PendingScreenCaptureAvailabilityRefresh;
    Assert.False(vm.IsScreenCaptureAvailable);
}

[Fact]
public async Task Send_WithAnImage_AfterSwitchingToAPersonaPinnedToByok_IsRefused_AndKeepsTheImage()
{
    var vm = CreateSut();
    vm.PendingAttachments.Add(Attachment());
    UsePinnedByokPersona();

    await vm.SendMessageCommand.ExecuteAsync(null);

    _ = _localization.Received(1)["Msg_File_ImageProviderUnsupported"];
    Assert.Single(vm.PendingAttachments);
    await _manager.DidNotReceiveWithAnyArgs().StartTurnAsync(default!, default!, default, default!);
}
```

```csharp
[Fact]
public async Task Availability_RefreshesWhenTheActivePersonaIsEdited()
{
    var vm = CreateSut();
    await vm.PendingScreenCaptureAvailabilityRefresh;

    UsePinnedByokPersona();
    _personas.PersonasChanged += Raise.Event();
    await vm.PendingScreenCaptureAvailabilityRefresh;
    Assert.False(vm.IsScreenCaptureAvailable);
}
```

  Adjust `StartTurnAsync`'s argument list to its real signature on `IChatSessionManager`. Check the
  name of the command that wraps `ExecuteSendMessage` before you write the test. The
  `SettingsChanged` raise stands in for `SettingsService.SaveSettingsAsync`, which raises it for real.

- [ ] **Step 2: Run** `-class "Pia.Tests.ViewModels.AssistantViewModelScreenCaptureTests"`. Expected:
  the new tests FAIL, except `Availability_RefreshesWhenThePersonaSwitchIsSaved`, which may already
  pass once the resolver is right. Every existing test passes.
- [ ] **Step 3: Add `ResolveTurnProviderAsync`.** Route both callers through it, and add the refresh
  to `OnPersonasChanged`. Rewrite the gate's
  summary to one line, for example `Refused rather than assumed on a failed read: the picture may be
  the user's whole desktop.` That summary may stay because the reason is not obvious from the code.
- [ ] **Step 4: Run the same class, then** `-class "Pia.Tests.ViewModels.AssistantViewModelPendingFilesTests"`,
  which also drives the gate. Expected: all pass.
- [ ] **Step 5: Commit.** Use `fix: check images against the provider the turn runs on`, then open PR 1
  with the `release-flow` skill.

### Task 2: `ImageViewerViewModel` and image copy

**Files:**
- Create: `src/Pia.Wpf/ViewModels/ImageViewerViewModel.cs`
- Modify: `src/Pia.Wpf/Services/Interfaces/IClipboardService.cs` and `src/Pia.Wpf/Services/ClipboardService.cs`
- Modify: `src/Pia.Wpf/Resources/Strings/MessageStrings{,.de,.fr}.resx`. Add two keys beside the
  other `Msg_File_*` keys.
- Create: `tests/Pia.Wpf.Tests/ViewModels/ImageViewerViewModelTests.cs`

**Interfaces:**
- Produces:

```csharp
// IClipboardService
void SetImage(byte[] encodedImage);   // decodes and places a bitmap; may throw if the clipboard is locked

public sealed partial class ImageViewerViewModel : ObservableObject
{
    public ImageViewerViewModel(IReadOnlyList<ImageAttachment> images, int startIndex,
        IClipboardService clipboard, IFileDialogService fileDialogs, ILocalizationService localization,
        Wpf.Ui.ISnackbarService snackbar, ILogger<ImageViewerViewModel> logger);

    public const double MinZoom = 0.1, MaxZoom = 8.0, ZoomStep = 1.25;
    [ObservableProperty] int CurrentIndex;          // clamped to [0, Count-1] in the ctor
    [ObservableProperty] bool IsFitToWindow = true;
    [ObservableProperty] double Zoom = 1.0;
    [ObservableProperty] double FitScale = 1.0;     // set by the view: the scale "fit" shows the image at
    public byte[] CurrentImageBytes { get; }        // Images[CurrentIndex].JpegBytes
    public int CurrentWidth { get; }  public int CurrentHeight { get; }
    public bool HasMany { get; }                    // Count > 1
    public string PositionText { get; }             // "2 / 4"
    IRelayCommand PreviousCommand, NextCommand, ZoomInCommand, ZoomOutCommand, ActualSizeCommand,
                  FitToWindowCommand, CopyCommand, SaveAsCommand, CloseCommand;
    public event EventHandler? CloseRequested;
}
```

**Decisions:**
- **Navigation.** Changing `CurrentIndex` resets to fit (`IsFitToWindow = true`, `Zoom = 1.0`) and
  raises `PropertyChanged` for every `Current*` property and `PositionText`.
- **Zoom.** `ZoomIn` and `ZoomOut` multiply or divide by `ZoomStep`, are clamped, and set
  `IsFitToWindow = false`. While fit is on they start from `FitScale`, not from `Zoom`, so the first
  click never jumps to 100 %. `ActualSize` sets 1.0 and turns fit off; `FitToWindow` turns it on.
- **Save as.** Call `fileDialogs.PromptSaveFile(localization["ImageViewer_Save_Tooltip"], "JPEG (*.jpg)|*.jpg", name, null)`.
  - `name` is `Path.GetFileNameWithoutExtension(SourcePath) + ".jpg"`, or `"image.jpg"` when there is
    no source path. The file is always JPEG, because that is what is stored.
  - A null result writes nothing. Write with `File.WriteAllBytesAsync`.
  - An `IOException` or `UnauthorizedAccessException` shows `Msg_ImageViewer_SaveFailed`, a caution
    snackbar of 4 s.
- **Copy.** `clipboard.SetImage(CurrentImageBytes)`. An `ExternalException` (the clipboard is locked)
  or a `COMException` shows `Msg_ImageViewer_CopyFailed`. Success shows nothing.
- **New strings:**
  - `Msg_ImageViewer_CopyFailed`: "Could not copy the image: another app is using the clipboard." /
    "Das Bild konnte nicht kopiert werden: Eine andere App verwendet die Zwischenablage." /
    "Impossible de copier l'image : une autre application utilise le presse-papiers."
  - `Msg_ImageViewer_SaveFailed`: "Could not save the image." / "Das Bild konnte nicht gespeichert
    werden." / "Impossible d'enregistrer l'image."

- [ ] **Step 1: Write the failing tests.** Build a 3-image list with the `Attachment()` shape from
  `AssistantViewModelScreenCaptureTests` and distinct `JpegBytes`. Then:

```csharp
[Fact] public void StartsOnTheRequestedImage_AndClampsAnOutOfRangeStart();       // start 1 → bytes of #2; start 9 → index 2
[Fact] public void PreviousAndNext_AreDisabledAtTheEnds();                         // CanExecute false at 0 / at 2
[Fact] public void ASingleImage_HasNoNavigation();                                // HasMany false, both CanExecute false
[Fact] public void Next_ResetsToFit_AndRaisesTheCurrentImage();                   // after ZoomIn, Next → IsFitToWindow, Zoom 1.0, PropertyChanged("CurrentImageBytes")
[Fact] public void Zoom_IsClampedBetweenMinAndMax();                              // 30× ZoomIn → 8.0; 30× ZoomOut → 0.1
[Fact] public void ZoomIn_FromFit_StartsAtTheFitScale();                           // FitScale 0.5 → ZoomIn → Zoom 0.625, IsFitToWindow false
[Fact] public void Copy_PutsTheCurrentBytesOnTheClipboard();                      // clipboard.Received(1).SetImage(images[1].JpegBytes)
[Fact] public void Copy_WhenTheClipboardIsLocked_WarnsAndStaysOpen();             // SetImage throws ExternalException → snackbar Show(..., "Msg_ImageViewer_CopyFailed" text, ...), no CloseRequested
[Fact] public async Task SaveAs_WritesTheStoredBytes_ToThePromptedPath();         // temp path; File.ReadAllBytes == JpegBytes
[Fact] public async Task SaveAs_Cancelled_WritesNothing();                        // PromptSaveFile returns null
[Fact] public void SaveAs_ProposesTheSourceFileName_WithAJpgExtension();          // SourcePath "C:\\x\\Holiday.png" → PromptSaveFile(_, _, "Holiday.jpg", null)
[Fact] public void Close_RaisesCloseRequested();
```

- [ ] **Step 2: Run** `-class "Pia.Tests.ViewModels.ImageViewerViewModelTests"`. Expected: FAIL, the
  type does not exist.
- [ ] **Step 3: Implement the VM and `ClipboardService.SetImage`.** The clipboard decodes with
  `BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)`,
  takes `Frames[0]` and calls `Clipboard.SetImage`. Add the two keys to all three `MessageStrings`
  files.
- [ ] **Step 4: Run that class, then** `-class "Pia.Tests.Architecture.DependencyInjectionTests"`.
  Expected: all pass. The architecture class proves the VM stays free of WPF types.
- [ ] **Step 5: Commit.** `feat: image viewer model with zoom, navigation, copy and save`

### Task 3: The viewer overlay in `MainWindow`

**Files:**
- Create: `src/Pia.Wpf/Views/Controls/ImageViewerOverlay.xaml` and `.xaml.cs`
- Create: `src/Pia.Wpf/Converters/EncodedImageToBitmapConverter.cs`
- Create: `src/Pia.Wpf/Services/Interfaces/IImageViewerHost.cs` and `src/Pia.Wpf/Services/ImageViewerHost.cs`
- Modify: `src/Pia.Wpf/MainWindow.xaml` (lines 228–236) and `MainWindow.xaml.cs` (constructor, next to
  `SetOverlayHost` at line 49)
- Modify: `src/Pia.Wpf/Bootstrapper.cs`. Register next to `IDialogOverlayService` (line 525), with
  the same lifetime.
- Modify: `src/Pia.Wpf/Services/Interfaces/IDialogService.cs` and `src/Pia.Wpf/Services/DialogService.cs`
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx`
- Modify: `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs`. Add one row.
- Modify: `docs/ui_automation/ui-automation-playbook.md`, section "Stable AutomationIds".

**Interfaces:**
- Consumes: `ImageViewerViewModel` from Task 2.
- Produces:

```csharp
public interface IImageViewerHost { void SetHost(ImageViewerOverlay host); ImageViewerOverlay GetHost(); }
// ImageViewerOverlay
public Task ShowAsync(ImageViewerViewModel viewModel);   // completes when the viewer closes
// IDialogService
Task ShowImageViewerAsync(IReadOnlyList<ImageAttachment> images, int startIndex);
```

**Decisions:**
- **Hosting.** The viewer has its own host, not an `OverlayDialogPanel`. That panel is capped at 480
  px, scrolls only vertically and pins a button row.
  - In `MainWindow.xaml`, place `<controls:ImageViewerOverlay x:Name="RootImageViewer" Visibility="Collapsed" />`
    with **no** `Panel.ZIndex`, directly **before** `RootContentDialogPresenter` (line 230). That
    presenter has no ZIndex either, so a ContentDialog that opens while the viewer is up still draws
    above it. The screen-capture hotkey's picker is one such dialog.
  - Overlay dialogs (20), the snackbar (17), the Flow view (16) and the `TitleBar` (later in the root
    `Grid`) all stay above the viewer. Copy and save warnings stay visible, and the window keeps its
    caption buttons.
- **`ShowAsync`.** It mirrors `DialogOverlayHost.ShowAsync`: remember `Keyboard.FocusedElement`,
  show, focus the overlay, complete on `CloseRequested`, collapse, then restore focus. There is no
  animation, because an image viewer should open at once.
- **Layout.**
  - A backdrop `#E6000000` that closes on a click. A click on the image itself does not close.
  - A top toolbar holds `PositionText` and the zoom, copy, save and close buttons.
  - Left and right edge buttons step through images and are visible when `HasMany` is true.
  - In the centre, a `ScrollViewer` holds an `Image`. Its source comes from `CurrentImageBytes`
    through `EncodedImageToBitmapConverter`, which uses `BitmapCacheOption.OnLoad` and `Freeze`. The
    converter is the only place bytes become a bitmap.
- **Fit.** When `IsFitToWindow` is true, the `Image` uses `Stretch="Uniform"` and the scroll bars are
  disabled. Otherwise it uses `Stretch="None"` with a `LayoutTransform` `ScaleTransform` bound to
  `Zoom`, and the scroll bars are `Auto`.
- **Leaving fit.** The code-behind sets the VM's `FitScale` on the `ScrollViewer`'s `SizeChanged`
  and on every `CurrentIndex` change: `min(viewportW / CurrentWidth, viewportH / CurrentHeight, 1.0)`.
  Only the view knows the viewport. The VM's zoom commands start from `FitScale` while fit is on (see
  Task 2), so the toolbar buttons and the keys behave the same.
- **Input.** All of this lives in the code-behind and is view-only.
  - Wheel zooms, with no modifier needed.
  - Left-drag pans when zoomed (capture the mouse, then `ScrollToHorizontalOffset` and
    `ScrollToVerticalOffset`).
  - Keys: Esc closes. Left and Right step through images. `+` and `-` zoom (`OemPlus`, `Add`,
    `OemMinus`, `Subtract`). `0` fits and `1` sets 100 %. Ctrl+C copies and Ctrl+S saves. All of them
    run through the VM's commands.
- **AutomationIds and strings.** Each button takes `AutomationProperties.Name` and `ToolTip` from
  the key in the table below.

| AutomationId | String key | en | de | fr |
|---|---|---|---|---|
| `ImageViewer_Close` | `ImageViewer_Close_Tooltip` | Close (Esc) | Schließen (Esc) | Fermer (Échap) |
| `ImageViewer_Previous` | `ImageViewer_Previous_Tooltip` | Previous image | Vorheriges Bild | Image précédente |
| `ImageViewer_Next` | `ImageViewer_Next_Tooltip` | Next image | Nächstes Bild | Image suivante |
| `ImageViewer_ZoomIn` | `ImageViewer_ZoomIn_Tooltip` | Zoom in | Vergrößern | Zoom avant |
| `ImageViewer_ZoomOut` | `ImageViewer_ZoomOut_Tooltip` | Zoom out | Verkleinern | Zoom arrière |
| `ImageViewer_ActualSize` | `ImageViewer_ActualSize_Tooltip` | Actual size (100 %) | Originalgröße (100 %) | Taille réelle (100 %) |
| `ImageViewer_Fit` | `ImageViewer_Fit_Tooltip` | Fit to window | An Fenster anpassen | Ajuster à la fenêtre |
| `ImageViewer_Copy` | `ImageViewer_Copy_Tooltip` | Copy image | Bild kopieren | Copier l'image |
| `ImageViewer_Save` | `ImageViewer_Save_Tooltip` | Save image as… | Bild speichern unter… | Enregistrer l'image sous… |

- **`DialogService`.** `ShowImageViewerAsync` builds the VM with injected `IClipboardService`,
  `IFileDialogService`, `ISnackbarService` and `ILoggerFactory`, then awaits
  `_imageViewerHost.GetHost().ShowAsync(vm)`. With an empty list it returns at once.

- [ ] **Step 1: Write the failing tests.**
  - Add `[InlineData(typeof(Pia.Views.Controls.ImageViewerOverlay), 9, 0, "")]` to
    `ViewAutomationIdTests.EveryInteractiveControl_CarriesAnAutomationId`.
  - Create `tests/Pia.Wpf.Tests/Converters/EncodedImageToBitmapConverterTests.cs`. One test encodes a
    4×3 JPEG with `JpegBitmapEncoder` and expects a frozen `BitmapSource` of 4×3. A second expects
    `null` for `null`, for an empty array and for garbage bytes (`DependencyProperty.UnsetValue` is
    also fine; pick one and assert it).
- [ ] **Step 2: Run** `-class "Pia.Tests.Views.ViewAutomationIdTests"` and the converter class.
  Expected: FAIL, the types do not exist.
- [ ] **Step 3: Implement** the overlay, the converter, the host service and its registration, the
  `MainWindow` wiring, the `IDialogService`/`DialogService` method and the nine strings in all three
  `ViewStrings` files. Add the `ImageViewer_*` ids to the playbook.
- [ ] **Step 4: Run both classes, then**
  `-class "Pia.Tests.Architecture.DependencyInjectionTests"`. Expected: all pass.
- [ ] **Step 5: Commit.** `feat: full-window image viewer`

### Task 4: Open composer and sent images in the viewer

**Files:**
- Create: `src/Pia.Wpf/ViewModels/AssistantViewModel.ImageViewer.cs` (a new partial, so it does not
  collide with #99's edits to `AssistantViewModel.cs`)
- Modify: `src/Pia.Wpf/Views/AssistantView.xaml`, the bubble template (lines 31–48) and the composer
  template (lines 643–664)
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings{,.de,.fr}.resx`. Add `Assistant_OpenImage_Tooltip`
  beside `Assistant_RemoveAttachment_Tooltip`: "Open image" / "Bild öffnen" / "Ouvrir l'image".
- Modify: `tests/Pia.Wpf.Tests/Views/ViewAutomationIdTests.cs`. The `AssistantView` row goes from
  `34, 7` to `36, 9`, or by the same +2/+2 from whatever value is there when you rebase on #99.
- Modify: `docs/ui_automation/ui-automation-playbook.md`
- Create: `tests/Pia.Wpf.Tests/ViewModels/AssistantViewModelImageViewerTests.cs`. Copy the `CreateSut`
  harness from `AssistantViewModelScreenCaptureTests`, holding `IDialogService` in a field.

**Interfaces:**
- Consumes: `IDialogService.ShowImageViewerAsync` from Task 3.
- Produces two `[RelayCommand]` methods on `AssistantViewModel`:
  - `Task OpenPendingImageAsync(ImageAttachment image)` opens the viewer over `PendingAttachments`
    at the image's index.
  - `Task OpenSentImageAsync(ImageAttachment image)` finds the message in `Messages` whose
    `Attachments` contains the image and opens the viewer over that list.
  - An image that is not found is a no-op.

**XAML:**
- Wrap each `Image` in a `ui:Button` with `Appearance="Transparent"`, `Padding="0"` and
  `Cursor="Hand"`.
- `Command` is bound to `DataContext.OpenPendingImageCommand` (or `OpenSentImageCommand`) through
  `RelativeSource AncestorType=UserControl`. An `ItemsControl` ancestor would find the per-message
  list in the bubble.
- `CommandParameter="{Binding}"`. `AutomationId` is `{Binding Id, StringFormat='Assistant_OpenPendingImage_{0}'}`
  or `'Assistant_OpenSentImage_{0}'`. `AutomationProperties.Name` and `ToolTip` come from
  `Assistant_OpenImage_Tooltip`.
- The composer's existing `ToolTip="{Binding SourcePath}"` moves to the button, and falls back to the
  string when the path is null.

- [ ] **Step 1: Write the failing tests:**

```csharp
[Fact] public async Task OpenPendingImage_ShowsThePendingList_AtThatImage();   // 3 pending, open #3 → ShowImageViewerAsync(list equal to PendingAttachments, 2)
[Fact] public async Task OpenSentImage_ShowsThatMessagesImages_AtThatImage();  // message with 2 attachments in Messages, open #2 → (that list, 1)
[Fact] public async Task OpenSentImage_ForAnImageNoMessageHolds_DoesNothing(); // DidNotReceiveWithAnyArgs
```

- [ ] **Step 2: Run** `-class "Pia.Tests.ViewModels.AssistantViewModelImageViewerTests"` and the
  `ViewAutomationIdTests` class. Expected: FAIL.
- [ ] **Step 3: Implement** the partial, the two templates, the string in all three files, the counts
  row and the playbook lines.
- [ ] **Step 4: Run both classes.** Expected: all pass.
- [ ] **Step 5: Commit.** `feat: open attached images in the viewer`

### Task 5: Gate and walkthrough

- [ ] **Step 0: If PR #99 merged first, rebase.** #99 adds `AssistantViewModel.JobComposer.cs` and
  keeps Send open beside a running chat job. Check that every path sending `PendingAttachments` still
  runs through `AssistantProviderTakesImagesAsync`: `git grep -n "PendingAttachments" src/Pia.Wpf/ViewModels`,
  and look at each site that clears the list. Then redo the `AssistantView` counts in
  `ViewAutomationIdTests`.
- [ ] **Step 1:** Rebuild Debug and Release. Expected: `0 Warning(s)` and `0 Error(s)` in both.
- [ ] **Step 2:** Run the full test exe. Expected: `Failed: 0`.
- [ ] **Step 3: WinWright walkthrough.** Read `docs/ui_automation/ui-automation-playbook.md` first.
  - **Profile.** Use a throwaway profile **signed in to the 18081 e2e stack with a Pia Cloud
    provider**. The image gate refuses images on any other provider, so the local-model setup PR #99
    used cannot attach one. Never use 8081.
  - **Composer.** Paste two images, then click `Assistant_OpenPendingImage_*`. Expected: the viewer
    opens on that image, and `ImageViewer_Next` steps through both.
  - **Zoom and copy.** Zoom with the wheel, pan by dragging, press `1` and then `0`. Copy, then paste
    into Paint.
  - **Save.** Save to a temp folder, and check that the file opens.
  - **Close.** Press Esc. Expected: focus is back in the composer.
  - **Sent image.** Send, then click `Assistant_OpenSentImage_*` in the bubble. Expected: the viewer
    opens with that message's images.
  - **Gate.** In settings, pin the active persona to a bring-your-own-key provider. This is a persona
    edit, so it goes through `PersonasChanged`. Expected: the capture button is disabled, and paste is
    refused with the provider warning. Then switch to an unpinned persona. Expected: the button is
    enabled again.
- [ ] **Step 4: Release notes.** Add no `RELEASE.md` entry in the feature PR; that is written on the
  release branch. In the PR description, list the user-visible change for the release-notes author.
- [ ] **Step 5:** Tick 0, A1 and A2 in the checklist. Comment on #96 with the PR links. Leave #96 and
  ADO 8235 open.
