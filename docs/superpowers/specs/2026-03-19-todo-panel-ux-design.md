# Todo Panel UX Enhancement Design

## Overview

Improve the TodoPanelControl across all 3 modes (Optimize, Assistant, Research) with a visual redesign, drag-and-drop reordering, voice input, and completion animations.

## Scope

| Feature | Description |
|---------|-------------|
| Panel redesign | Clean Elevated style — shadow, colored left-border priority, rounded corners |
| Panel animation | Expand/collapse width (0 ↔ 280px) instead of instant show/hide |
| Drag-and-drop | Reorder pending todos with persisted SortOrder (local + sync) |
| Voice input | Record button using existing Whisper service in the todo input area |
| Completion animation | Strikethrough + fade out + row collapse on todo completion |

## 1. Data Model & Persistence

### TodoItem (client)
Add `SortOrder` (int) property. Default derived from creation order.

### SQLite schema
Add `SortOrder INTEGER NOT NULL DEFAULT 0` column. Migration sets initial sort order from `CreatedAt` ordering.

### SyncTodo DTO (Pia.Shared)
Add `public int SortOrder { get; set; }` — backwards compatible. Old clients ignore the field; old data defaults to 0.

### Server (Pia project at C:\projects\Pia)
- Add `SortOrder` (int) to `ServerTodo` entity (`src/Pia.Server/Models/ServerTodo.cs`)
- EF Core migration to add the column with DEFAULT 0
- Update `SyncService.cs` upsert logic to read/write `SortOrder`
- Update pull response to include `SortOrder`

### TodoService (client)
- `GetPendingAsync` orders by `SortOrder ASC, CreatedAt ASC` — replaces the current `Priority DESC, CreatedAt ASC` ordering. Drag-and-drop fully owns the sort order; priority is visual-only (colored left border) and no longer affects list position.
- New `UpdateSortOrderAsync(List<(Guid Id, int SortOrder)> updates)` for batch reorder saves
- New todos: `CreateAsync` assigns `SortOrder = MAX(SortOrder) + 1` (append to end of list)

### SQLite migration detail
Follow the existing `MigrateSchema` pattern (PRAGMA table_info check, then ALTER TABLE):
```sql
ALTER TABLE Todos ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;
UPDATE Todos SET SortOrder = rowid_order FROM (
  SELECT Id, ROW_NUMBER() OVER (ORDER BY Priority DESC, CreatedAt ASC) - 1 AS rowid_order
  FROM Todos WHERE Status = 0
) AS sub WHERE Todos.Id = sub.Id;
```

### Backwards compatibility
- Old clients: JSON deserialization ignores unknown `SortOrder` field — no crash
- Old client sends without field: server stores default 0
- New client receives old data: all `SortOrder = 0`, falls back to `CreatedAt` secondary sort
- No breaking changes in either direction

### E2EE consideration
`SortOrder` is sent in plaintext (not included in the encrypted payload). It is a numeric index that reveals ordering but not content. This matches the existing pattern where `Priority` and `Status` are also plaintext integers.

## 2. Panel Visual Redesign (Clean Elevated)

### Panel container
- Background: `SecondaryBackgroundBrush`
- `CornerRadius="12,0,0,12"` (rounded left corners only)
- `DropShadowEffect` — horizontal offset, low opacity, ~20px blur. Shadow color should be a theme resource: dark theme uses black at ~30% opacity, light theme uses black at ~15% opacity.
- Remove hard `BorderThickness="1,0,0,0"` left border

### Todo item cards
- Background: `TertiaryBackgroundBrush`
- `CornerRadius="8"`
- Priority: colored left border (`BorderThickness="3,0,0,0"`) instead of dot
  - High: `TodoPriorityHighBrush` (#E74C3C)
  - Medium: `TodoPriorityMediumBrush` (#F39C12)
  - Low: `TodoPriorityLowBrush` (#3498DB)
- Checkbox: WPF-UI default style

### Progress bar
- Keep current design, add `CornerRadius` on track and fill

### Input area
- Layout: `[TextBox] [Record] [Add]`
- TextBox: `TertiaryBackgroundBrush` background, `CornerRadius="6"`
- Record button: `ui:Button`, `Appearance="Secondary"`, 32x32, `Record24` icon
- Add button: `ui:Button`, `Appearance="Primary"`, 32x32, `Add16` icon

### "Open full view" link
- Unchanged — accent-colored text with arrow

## 3. Panel Open/Close Animation

### Open (expand)
- `DoubleAnimation` on `Border.Width` from 0 to 280
- Duration: ~250ms, `CubicEase EaseOut`
- Content `Visibility` set to `Visible` at start, clipped by expanding border

### Close (collapse)
- `DoubleAnimation` on `Border.Width` from 280 to 0
- Duration: ~200ms, `CubicEase EaseIn`
- On completion, set `Visibility` to `Collapsed`

### Implementation
- Replace `BooleanToVisibilityConverter` with `DataTrigger` on `IsTodoPanelOpen` in all 3 host views (OptimizeView, AssistantView, ResearchView)
- Use `EnterActions`/`ExitActions` with storyboards
- `ClipToBounds="True"` on panel border

## 4. Drag-and-Drop Reordering

### Mechanism
Attached behavior (static class with attached properties) — no code-behind.

### Drag handle
The drag gesture initiates from the **text area of the todo item** (not the checkbox). The checkbox remains a normal click target. A subtle grip icon (6 dots / drag handle) appears on the right side of each item on hover to signal draggability.

### Interaction flow
1. Press and hold on the item body (~150ms) to distinguish from click — checkbox area excluded
2. Grabbed item gets subtle visual lift (slight scale + opacity change)
3. Translucent drag adorner follows cursor
4. Other items shift with smooth translate animation
5. On drop: reorder, recalculate sequential SortOrder (0, 1, 2...)
6. Call `UpdateSortOrderAsync` to persist

### Drag visual
Semi-transparent card copy with increased drop shadow.

### Edge cases
- Auto-scroll when dragging to top/bottom of scrollable area
- No-op for 0-1 items
- Completed todos not draggable (not shown in panel)

## 5. Voice Input

### Button
- `ui:Button`, `Appearance="Secondary"`, 32x32, `Record24` icon
- Positioned between text input and add button
- ToolTip: new localization key `TodoPanel_Record` (add to existing `.resx` / `loc:Str` resources)

### Wiring
- `TodoViewModel` receives `IVoiceInputService` via DI
- New `RecordTodoCommand` (AsyncRelayCommand)
- Calls `CaptureVoiceInputAsync()`, sets result into `NewTodoTitle`
- Same pattern as `AssistantViewModel.ExecuteToggleRecording`

### Behavior
- Transcribed text fills input field (not auto-submitted)
- User can review/edit before pressing Enter or add button

## 6. Completion Animation

### Sequence (on checkbox checked)
1. **Strikethrough** — `Line` overlay on title, animate `X2` from 0 to full width (~200ms)
2. **Fade out** — After ~150ms delay, `Opacity` 1→0 over ~300ms
3. **Row collapse** — Simultaneously animate `MaxHeight` to 0 and `Margin` to 0 over ~250ms, `CubicEase EaseIn`
4. **Cleanup** — On animation `Completed`, fire `CompleteTodoCommand`

### Key detail
Command execution deferred until animation finishes. Checkbox triggers animation storyboard, which calls the command on `Completed` event. This requires a small behavior or code-behind handler in `TodoPanelControl.xaml.cs` to wire the storyboard `Completed` event to the ViewModel command.

### Error handling
If `CompleteAsync` fails after the animation, the item reappears: reset `Opacity` to 1, `MaxHeight` to auto, remove strikethrough, uncheck the checkbox. The existing error dialog pattern continues (via `IDialogService`).

## Files Affected

### Client (Pia.Wpf)
| File | Change |
|------|--------|
| `src/Pia.Wpf/Models/TodoItem.cs` | Add `SortOrder` property |
| `src/Pia.Wpf/Infrastructure/SqliteContext.cs` | Add column + migration |
| `src/Pia.Wpf/Services/TodoService.cs` | Sort query, batch update method, new todo sort assignment |
| `src/Pia.Wpf/Services/Interfaces/ITodoService.cs` | Add `UpdateSortOrderAsync` to interface |
| `src/Pia.Wpf/ViewModels/TodoViewModel.cs` | Voice command, drag-drop logic, animation triggers |
| `src/Pia.Wpf/Views/TodoPanelControl.xaml` | Full visual redesign, animations, voice button |
| `src/Pia.Wpf/Views/TodoPanelControl.xaml.cs` | Completion animation storyboard-to-command wiring |
| `src/Pia.Wpf/Views/AssistantView.xaml` | Replace BooleanToVisibilityConverter with animation trigger |
| `src/Pia.Wpf/Views/OptimizeView.xaml` | Replace BooleanToVisibilityConverter with animation trigger |
| `src/Pia.Wpf/Views/ResearchView.xaml` | Replace BooleanToVisibilityConverter with animation trigger |
| `src/Pia.Wpf/Behaviors/DragDropReorderBehavior.cs` | New directory + attached behavior for drag-and-drop |
| Localization resources | Add `TodoPanel_Record` key |

### Shared (Pia.Shared)
| File | Change |
|------|--------|
| `src/Pia.Shared/Models/SyncTodo.cs` | Add `SortOrder` property |

### Server (C:\projects\Pia)
| File | Change |
|------|--------|
| `src/Pia.Server/Models/ServerTodo.cs` | Add `SortOrder` property |
| `src/Pia.Server/Migrations/YYYYMMDD_AddTodoSortOrder.cs` | New EF Core migration |
| `src/Pia.Server/Sync/SyncService.cs` | Read/write `SortOrder` in upsert + pull |
