# Claude Code — Hand-off Prompt

Copy everything between the lines below into Claude Code as your first message.

---

You are working on the **Pia.Wpf** desktop application (.NET 10 · WPF UI · Markdig · ColorCode.Core).
Your task is a visual refresh — **no structural or view-model changes**. Only `ResourceDictionary`,
`Style`, `ControlTemplate` and new `UserControl`s.

**Inputs in this folder:**

- `01-migration-guide.md` — base visual refresh (Phase 1)
- `02-modern-pro-controls.md` — new chat controls for long-form responses (Phase 2)
- `tokens/PiaTokens.Light.xaml`, `tokens/PiaTokens.Dark.xaml`, `tokens/PiaStyles.xaml` — ready to copy in
- `reference/` — screenshots showing the target look

**Working agreement:**

1. **Phase 1 first.** Read `01-migration-guide.md` end-to-end. Then execute its steps 1–7 in order.
   After each step: build, run, screenshot the main chat window, and pause for my review.
2. **Phase 2 only after I approve Phase 1.** Read `02-modern-pro-controls.md` end-to-end. Then build
   the seven new controls in the listed order. Same review cadence: build, render a test Markdown
   payload in `PiaAssistantMessage`, screenshot, pause.
3. **Do not touch any `*ViewModel.cs` file** except for the additive properties listed in
   `02-modern-pro-controls.md` Step 10. If something seems to require a VM change, stop and ask me.
4. **Use the tokens.** Every color must come from `PiaTokens.*.xaml` via `{DynamicResource}` — no
   hex literals in component XAML.
5. **WPF-UI overrides.** When overriding WPF-UI's internal brushes (e.g. `AccentFillColorDefaultBrush`),
   verify the override actually wins. Some WPF-UI controls re-resolve their brushes when the theme
   manager runs — see the `ApplyPiaTheme` hook in the guide.

**First action:** open `01-migration-guide.md`, then `App.xaml` and `MainWindow.xaml` in the
repository, and reply with:
- A short summary of what you found in the current XAML.
- The exact list of files you'll need to create or modify for Phase 1.
- Any open questions before you start.

Do not write code until I confirm.

---

## Notes for the human

- Keep this prompt + the markdown guides in the same folder as the project (or pass them via Claude
  Code's file attachment).
- After Phase 1 ships, the visual difference is already substantial — feel free to release that
  alone if Phase 2 needs more time.
- Phase 2 introduces `Markdig.Wpf` and the ColorCode WPF formatter as dependencies. Verify NuGet
  versions before Claude Code runs.
