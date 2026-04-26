# Todo Panel UX Enhancement Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enhance the TodoPanelControl with a Clean Elevated visual redesign, expand/collapse animation, drag-and-drop reordering (with persisted SortOrder synced across devices), voice input via Whisper, and a strikethrough + fade completion animation.

**Architecture:** All changes follow the existing MVVM pattern. Data flows through: TodoItem model -> TodoService (SQLite) -> TodoViewModel -> TodoPanelControl XAML. Sync flows through SyncMapper <-> SyncTodo DTO <-> Server SyncService <-> ServerTodo (EF Core/PostgreSQL). Drag-and-drop uses an attached behavior. Voice input reuses the existing IVoiceInputService.

**Tech Stack:** WPF (.NET 10), WPF-UI, SQLite (raw ADO.NET), EF Core (server), CommunityToolkit.Mvvm, NAudio/Whisper (voice)

**Spec:** `docs/superpowers/specs/2026-03-19-todo-panel-ux-design.md`

---

## Chunk 1: Data Model & Persistence

### Task 1: Add SortOrder to TodoItem model

**Files:**
- Modify: `src/Pia.Wpf/Models/TodoItem.cs:1-18`

- [ ] **Step 1: Add SortOrder property to TodoItem**

Add after line 16 (`UpdatedAt`):

```csharp
public int SortOrder { get; set; }
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Models/TodoItem.cs
git commit -m "feat(todo): add SortOrder property to TodoItem model"
```

---

### Task 2: Add SortOrder to SyncTodo DTO

**Files:**
- Modify: `src/Pia.Shared/Models/SyncTodo.cs:1-31`

- [ ] **Step 1: Add SortOrder property to SyncTodo**

Add after `UpdatedAt` (line 18), before the E2EE comment:

```csharp
public int SortOrder { get; set; }
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/Pia.Shared/Pia.Shared.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Shared/Models/SyncTodo.cs
git commit -m "feat(sync): add SortOrder to SyncTodo DTO (backwards compatible)"
```

---

### Task 3: Add SortOrder column to SQLite schema and migration

**Files:**
- Modify: `src/Pia.Wpf/Infrastructure/SqliteContext.cs`
  - CREATE TABLE at lines 92-103: add SortOrder column
  - `MigrateSchema()` at lines 115-138: add migration block

- [ ] **Step 1: Add SortOrder to CREATE TABLE statement**

In the Todos CREATE TABLE (around line 92-103), add `SortOrder INTEGER NOT NULL DEFAULT 0` after `UpdatedAt`:

```sql
CREATE TABLE IF NOT EXISTS Todos (
    Id TEXT PRIMARY KEY,
    Title TEXT NOT NULL,
    Notes TEXT,
    Priority INTEGER NOT NULL DEFAULT 1,
    Status INTEGER NOT NULL DEFAULT 0,
    DueDate TEXT,
    LinkedReminderId TEXT,
    CreatedAt TEXT NOT NULL,
    CompletedAt TEXT,
    UpdatedAt TEXT NOT NULL,
    SortOrder INTEGER NOT NULL DEFAULT 0
);
```

- [ ] **Step 2: Add migration block in MigrateSchema**

Follow the existing pattern (PRAGMA table_info check, then ALTER). Add a new migration block after the existing `ProcessingTimeMs` migration:

```csharp
// Add SortOrder column to Todos if it doesn't exist
using var todoPragma = _connection!.CreateCommand();
todoPragma.CommandText = "PRAGMA table_info(Todos)";
using var todoReader = todoPragma.ExecuteReader();
var hasSortOrder = false;
while (todoReader.Read())
{
    if (todoReader.GetString(1) == "SortOrder")
    {
        hasSortOrder = true;
        break;
    }
}
todoReader.Close();

if (!hasSortOrder)
{
    using var addCol = _connection.CreateCommand();
    addCol.CommandText = "ALTER TABLE Todos ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0";
    addCol.ExecuteNonQuery();

    // Backfill sort order from existing priority + creation order
    using var backfill = _connection.CreateCommand();
    backfill.CommandText = """
        UPDATE Todos SET SortOrder = (
            SELECT COUNT(*) FROM Todos AS t2
            WHERE t2.Status = Todos.Status
            AND (t2.Priority > Todos.Priority
                 OR (t2.Priority = Todos.Priority AND t2.CreatedAt < Todos.CreatedAt)
                 OR (t2.Priority = Todos.Priority AND t2.CreatedAt = Todos.CreatedAt AND t2.Id < Todos.Id))
        )
        """;
    backfill.ExecuteNonQuery();
}
```

Note: SQLite does not support `ROW_NUMBER()` in UPDATE-FROM syntax, so we use a correlated subquery with an `Id` tiebreaker to guarantee unique SortOrder values even when Priority and CreatedAt are identical.

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Infrastructure/SqliteContext.cs
git commit -m "feat(todo): add SortOrder column to SQLite schema with migration"
```

---

### Task 4: Update TodoService for SortOrder

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/ITodoService.cs:1-24`
- Modify: `src/Pia.Wpf/Services/TodoService.cs`
  - SQL SELECT queries (all need SortOrder in column list)
  - `GetPendingAsync` at line 81: change ORDER BY
  - `CreateAsync` at line 24: assign SortOrder
  - `AddTodoParameters` at line 270: add SortOrder param
  - `MapTodoItem` at line 295: read SortOrder
  - INSERT/UPDATE SQL: include SortOrder
  - New method: `UpdateSortOrderAsync`

- [ ] **Step 1: Add UpdateSortOrderAsync to ITodoService**

Add to the interface after `UpdateAsync` (around line 16):

```csharp
Task UpdateSortOrderAsync(IReadOnlyList<(Guid Id, int SortOrder)> updates);
```

- [ ] **Step 2: Update all SELECT queries to include SortOrder**

Every SQL SELECT that reads from Todos needs `SortOrder` added to the column list. There are queries in: `GetAsync`, `GetAllAsync`, `GetPendingAsync`, `GetCompletedAsync`, `GetCompletedTodayAsync`. Change every occurrence of:

```sql
SELECT Id, Title, Notes, Priority, Status, DueDate, LinkedReminderId, CreatedAt, CompletedAt, UpdatedAt
```

to:

```sql
SELECT Id, Title, Notes, Priority, Status, DueDate, LinkedReminderId, CreatedAt, CompletedAt, UpdatedAt, SortOrder
```

- [ ] **Step 3: Change GetPendingAsync ORDER BY**

In `GetPendingAsync` (line 88), change:
```sql
ORDER BY Priority DESC, CreatedAt ASC
```
to:
```sql
ORDER BY SortOrder ASC, CreatedAt ASC
```

- [ ] **Step 4: Update MapTodoItem to read SortOrder**

In `MapTodoItem` (line 295-309), add after the `UpdatedAt` line:

```csharp
SortOrder = reader.GetInt32(10)
```

- [ ] **Step 5: Update AddTodoParameters to write SortOrder**

In `AddTodoParameters` (line 270-282), add:

```csharp
command.Parameters.AddWithValue("@SortOrder", todo.SortOrder);
```

- [ ] **Step 6: Update INSERT SQL in CreateAsync**

Change the INSERT statement (around line 37-39) to include SortOrder:

```sql
INSERT INTO Todos (Id, Title, Notes, Priority, Status, DueDate, LinkedReminderId, CreatedAt, CompletedAt, UpdatedAt, SortOrder)
VALUES (@Id, @Title, @Notes, @Priority, @Status, @DueDate, @LinkedReminderId, @CreatedAt, @CompletedAt, @UpdatedAt, @SortOrder)
```

- [ ] **Step 7: Assign SortOrder in CreateAsync**

Before the INSERT, query for the max sort order and assign:

```csharp
// Assign SortOrder = max + 1 (append to end)
using var maxCmd = connection.CreateCommand();
maxCmd.CommandText = "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Todos WHERE Status = 0";
var maxResult = await maxCmd.ExecuteScalarAsync();
todo.SortOrder = Convert.ToInt32(maxResult);
```

- [ ] **Step 8: Update ImportAsync INSERT SQL**

Change the INSERT OR REPLACE statement in `ImportAsync` (around line 157-159) to include SortOrder:

```sql
INSERT OR REPLACE INTO Todos (Id, Title, Notes, Priority, Status, DueDate, LinkedReminderId, CreatedAt, CompletedAt, UpdatedAt, SortOrder)
VALUES (@Id, @Title, @Notes, @Priority, @Status, @DueDate, @LinkedReminderId, @CreatedAt, @CompletedAt, @UpdatedAt, @SortOrder)
```

- [ ] **Step 9: Update UpdateAsync SQL**

Change the UPDATE statement in `UpdateAsync` (around line 138-143) to include SortOrder:

```sql
UPDATE Todos
SET Title = @Title, Notes = @Notes, Priority = @Priority, Status = @Status,
    DueDate = @DueDate, LinkedReminderId = @LinkedReminderId,
    CompletedAt = @CompletedAt, UpdatedAt = @UpdatedAt, SortOrder = @SortOrder
WHERE Id = @Id
```

- [ ] **Step 10: Implement UpdateSortOrderAsync**

Add new method to `TodoService`:

```csharp
public async Task UpdateSortOrderAsync(IReadOnlyList<(Guid Id, int SortOrder)> updates)
{
    var connection = _context.GetConnection();
    using var transaction = connection.BeginTransaction();

    try
    {
        foreach (var (id, sortOrder) in updates)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Todos SET SortOrder = @SortOrder, UpdatedAt = @UpdatedAt WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", id.ToString());
            command.Parameters.AddWithValue("@SortOrder", sortOrder);
            command.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        transaction.Commit();
        _logger.LogInformation("Updated sort order for {Count} todos", updates.Count);
        OnTodoChanged();
    }
    catch
    {
        transaction.Rollback();
        throw;
    }
}
```

- [ ] **Step 11: Build and verify**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: Build succeeded

- [ ] **Step 12: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/ITodoService.cs src/Pia.Wpf/Services/TodoService.cs
git commit -m "feat(todo): update TodoService for SortOrder persistence and reordering"
```

---

### Task 5: Update SyncMapper for SortOrder

**Files:**
- Modify: `src/Pia.Wpf/Services/SyncMapper.cs`
  - `ToSyncTodo` at line 338: add SortOrder mapping
  - `FromSyncTodo` at line 376: add SortOrder mapping (both E2EE and plaintext paths)

- [ ] **Step 1: Update ToSyncTodo**

In the `SyncTodo` initializer (line 340-345), add `SortOrder` — it's sent in plaintext regardless of E2EE:

```csharp
var sync = new SyncTodo
{
    Id = todo.Id,
    SortOrder = todo.SortOrder,
    CreatedAt = ToUtc(todo.CreatedAt),
    UpdatedAt = ToUtc(todo.UpdatedAt)
};
```

- [ ] **Step 2: Update FromSyncTodo (E2EE path)**

In the E2EE return block (line 386-398), add `SortOrder = sync.SortOrder` (SortOrder comes from the plaintext sync, not the decrypted payload):

```csharp
return new TodoItem
{
    Id = sync.Id,
    Title = decrypted.Title ?? "",
    Notes = decrypted.Notes,
    Priority = (TodoPriority)decrypted.Priority,
    Status = (TodoStatus)decrypted.Status,
    DueDate = decrypted.DueDate,
    LinkedReminderId = decrypted.LinkedReminderId,
    CreatedAt = sync.CreatedAt,
    CompletedAt = decrypted.CompletedAt,
    UpdatedAt = sync.UpdatedAt,
    SortOrder = sync.SortOrder
};
```

- [ ] **Step 3: Update FromSyncTodo (plaintext path)**

In the plaintext return block (line 401-413), add `SortOrder = sync.SortOrder`:

```csharp
return new TodoItem
{
    Id = sync.Id,
    Title = sync.Title ?? "",
    Notes = sync.Notes,
    Priority = (TodoPriority)sync.Priority,
    Status = (TodoStatus)sync.Status,
    DueDate = sync.DueDate,
    LinkedReminderId = sync.LinkedReminderId,
    CreatedAt = sync.CreatedAt,
    CompletedAt = sync.CompletedAt,
    UpdatedAt = sync.UpdatedAt,
    SortOrder = sync.SortOrder
};
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/SyncMapper.cs
git commit -m "feat(sync): map SortOrder through SyncMapper (plaintext, not in E2EE payload)"
```

---

### Task 6: Server-side SortOrder changes

**Files:**
- Modify: `C:\projects\Pia\src\Pia.Server\Models\ServerTodo.cs:1-24`
- Modify: `C:\projects\Pia\src\Pia.Server\Sync\SyncService.cs`
  - Todo upsert logic at lines 712-791
  - Todo pull logic at lines 207-244
- Create: `C:\projects\Pia\src\Pia.Server\Migrations\<timestamp>_AddTodoSortOrder.cs` (via EF tooling)

**Important:** These changes are in the **server project** at `C:\projects\Pia`, not the WPF client.

- [ ] **Step 1: Add SortOrder property to ServerTodo**

Add after `UpdatedAt` (line 15):

```csharp
public int SortOrder { get; set; }
```

- [ ] **Step 2: Generate EF Core migration**

Run from `C:\projects\Pia`:

```bash
dotnet ef migrations add AddTodoSortOrder --project src/Pia.Server/Pia.Server.csproj
```

This auto-generates the migration file. Verify the `Up()` method adds the column:

```csharp
migrationBuilder.AddColumn<int>(
    name: "SortOrder",
    table: "todos",
    type: "integer",
    nullable: false,
    defaultValue: 0);
```

- [ ] **Step 3: Update SyncService upsert — existing todo update (SortOrder is always plaintext)**

In `SyncService.cs`, `SortOrder` must be set regardless of E2EE mode. Add it next to `existing.UpdatedAt = todo.UpdatedAt;` (line 761), which is already outside the if/else E2EE branch:

```csharp
existing.UpdatedAt = todo.UpdatedAt;
existing.SortOrder = todo.SortOrder;
```

Also add `SortOrder` to the conflict resolution block (around lines 722-730). In the `SyncTodo` conflict object initializer, add:

```csharp
SortOrder = existing.SortOrder,
```

- [ ] **Step 4: Update SyncService upsert — new todo creation**

In the new todo creation block (lines 765-771), add `SortOrder` to the initializer — it's always plaintext:

```csharp
var serverTodo = new ServerTodo
{
    Id = todo.Id,
    UserId = userId,
    SortOrder = todo.SortOrder,
    CreatedAt = todo.CreatedAt,
    UpdatedAt = todo.UpdatedAt
};
```

- [ ] **Step 5: Update SyncService pull — include SortOrder in response**

In the pull logic around lines 216-226, add `SortOrder` to the SyncTodo initializer:

```csharp
var st = new SyncTodo
{
    Id = t.Id,
    SortOrder = t.SortOrder,
    Priority = t.Priority,
    Status = t.Status,
    DueDate = t.DueDate,
    LinkedReminderId = t.LinkedReminderId,
    CreatedAt = t.CreatedAt,
    CompletedAt = t.CompletedAt,
    UpdatedAt = t.UpdatedAt
};
```

- [ ] **Step 6: Build server**

Run from `C:\projects\Pia`:
```bash
dotnet build src/Pia.Server/Pia.Server.csproj
```
Expected: Build succeeded

- [ ] **Step 7: Commit server changes**

```bash
cd /c/projects/Pia
git add src/Pia.Server/Models/ServerTodo.cs src/Pia.Server/Sync/SyncService.cs src/Pia.Server/Migrations/
git commit -m "feat(sync): add SortOrder to ServerTodo with migration and sync support"
```

---

## Chunk 2: Panel Visual Redesign & Animation

### Task 7: Add theme resources for panel shadow

**Files:**
- Modify: `src/Pia.Wpf/Resources/Themes/Dark.xaml`
- Modify: `src/Pia.Wpf/Resources/Themes/Light.xaml`

- [ ] **Step 1: Add shadow resources to Dark.xaml**

Add near the other color resources:

```xml
<Color x:Key="PanelShadowColor">#000000</Color>
<sys:Double x:Key="PanelShadowOpacity">0.3</sys:Double>
```

Ensure the `sys` namespace is declared: `xmlns:sys="clr-namespace:System;assembly=mscorlib"` (add if not present).

- [ ] **Step 2: Add shadow resources to Light.xaml**

```xml
<Color x:Key="PanelShadowColor">#000000</Color>
<sys:Double x:Key="PanelShadowOpacity">0.15</sys:Double>
```

Same `sys` namespace as above.

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Resources/Themes/Dark.xaml src/Pia.Wpf/Resources/Themes/Light.xaml
git commit -m "feat(theme): add PanelShadowColor resources for todo panel"
```

---

### Task 8: Redesign TodoPanelControl XAML (Clean Elevated style)

**Files:**
- Modify: `src/Pia.Wpf/Views/TodoPanelControl.xaml:1-202`

This is a full restyle of the panel. Key changes:
- Outer border: rounded left corners, drop shadow, no hard border
- Todo items: TertiaryBackgroundBrush cards with colored left border for priority (replacing dots)
- Input area: 3-column layout with record button between textbox and add button
- Grip icon on todo items (for drag-and-drop affordance)

- [ ] **Step 1: Update panel container border**

Replace the outer `<Border>` (lines 34-39):

Old:
```xml
<Border Width="280"
        Background="{DynamicResource SecondaryBackgroundBrush}"
        BorderBrush="{DynamicResource CardBorderBrush}"
        BorderThickness="1,0,0,0"
        HorizontalAlignment="Right"
        VerticalAlignment="Stretch">
```

New:
```xml
<Border Width="280"
        Background="{DynamicResource SecondaryBackgroundBrush}"
        CornerRadius="12,0,0,12"
        HorizontalAlignment="Right"
        VerticalAlignment="Stretch"
        ClipToBounds="True">
  <Border.Effect>
    <DropShadowEffect ShadowDepth="0"
                      BlurRadius="20"
                      Direction="180"
                      Color="{DynamicResource PanelShadowColor}"
                      Opacity="{DynamicResource PanelShadowOpacity}" />
  </Border.Effect>
```

- [ ] **Step 2: Replace priority dot style with colored left border**

Remove the `PanelPriorityDotStyle` resource (lines 14-31).

Replace the todo item `DataTemplate` inner content. The current template has a `Grid` with CheckBox, Ellipse (dot), and TextBlock. Replace with a nested `Border` approach:

```xml
<DataTemplate DataType="{x:Type models:TodoItem}">
  <Border x:Name="PanelTodoItem"
          Margin="8,0,8,3"
          Background="{DynamicResource TertiaryBackgroundBrush}"
          CornerRadius="8"
          Opacity="0">
    <Border.Triggers>
      <EventTrigger RoutedEvent="Loaded">
        <BeginStoryboard>
          <Storyboard>
            <DoubleAnimation Storyboard.TargetProperty="Opacity"
                             From="0" To="1" Duration="0:0:0.2">
              <DoubleAnimation.EasingFunction>
                <CubicEase EasingMode="EaseOut" />
              </DoubleAnimation.EasingFunction>
            </DoubleAnimation>
          </Storyboard>
        </BeginStoryboard>
      </EventTrigger>
    </Border.Triggers>

    <!-- Priority left border -->
    <Border x:Name="PriorityBorder"
            BorderThickness="3,0,0,0"
            BorderBrush="{DynamicResource TextPlaceholderColorBrush}"
            CornerRadius="8"
            Padding="8,6">
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="Auto" />
          <ColumnDefinition Width="*" />
          <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>

        <CheckBox Grid.Column="0"
                  IsChecked="False"
                  VerticalAlignment="Center"
                  Margin="0,0,8,0"
                  Command="{Binding DataContext.CompleteTodoCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                  CommandParameter="{Binding}" />

        <TextBlock Grid.Column="1"
                   Text="{Binding Title}"
                   FontSize="13"
                   TextTrimming="CharacterEllipsis"
                   VerticalAlignment="Center">
          <TextBlock.Style>
            <Style TargetType="TextBlock">
              <Style.Triggers>
                <DataTrigger Binding="{Binding Notes, Converter={StaticResource StringNotNullOrEmptyToBoolConverter}}" Value="True">
                  <Setter Property="ToolTip" Value="{Binding Notes}" />
                </DataTrigger>
              </Style.Triggers>
            </Style>
          </TextBlock.Style>
        </TextBlock>

        <!-- Drag grip icon (visible on hover) -->
        <ui:SymbolIcon Grid.Column="2"
                       Symbol="ReOrderDotsVertical24"
                       FontSize="14"
                       Foreground="{DynamicResource TextPlaceholderColorBrush}"
                       VerticalAlignment="Center"
                       Opacity="0"
                       x:Name="DragGrip" />
      </Grid>
    </Border>
  </Border>

  <DataTemplate.Triggers>
    <!-- Priority border colors -->
    <DataTrigger Binding="{Binding Priority}" Value="{x:Static models:TodoPriority.High}">
      <Setter TargetName="PriorityBorder" Property="BorderBrush" Value="{DynamicResource TodoPriorityHighBrush}" />
    </DataTrigger>
    <DataTrigger Binding="{Binding Priority}" Value="{x:Static models:TodoPriority.Medium}">
      <Setter TargetName="PriorityBorder" Property="BorderBrush" Value="{DynamicResource TodoPriorityMediumBrush}" />
    </DataTrigger>
    <DataTrigger Binding="{Binding Priority}" Value="{x:Static models:TodoPriority.Low}">
      <Setter TargetName="PriorityBorder" Property="BorderBrush" Value="{DynamicResource TodoPriorityLowBrush}" />
    </DataTrigger>
    <!-- Show drag grip on hover -->
    <Trigger SourceName="PanelTodoItem" Property="IsMouseOver" Value="True">
      <Setter TargetName="DragGrip" Property="Opacity" Value="0.6" />
    </Trigger>
  </DataTemplate.Triggers>
</DataTemplate>
```

Note: `ReOrderDotsVertical` is confirmed to exist in the WPF-UI 4.1.0 DLL. If `ReOrderDotsVertical24` doesn't compile (wrong size suffix), try `ReOrderDotsVertical20` or `ReOrderDotsVertical16`.

- [ ] **Step 3: Update quick-add input area to include record button**

Replace the input Grid (lines 152-180) with a 3-column layout:

```xml
<!-- Quick add input -->
<Grid Grid.Row="3" Margin="12,8,12,8">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="*" />
    <ColumnDefinition Width="Auto" />
    <ColumnDefinition Width="Auto" />
  </Grid.ColumnDefinitions>

  <ui:TextBox Grid.Column="0"
              Text="{Binding NewTodoTitle, UpdateSourceTrigger=PropertyChanged}"
              PlaceholderText="{loc:Str TodoPanel_AddPlaceholder}"
              PlaceholderEnabled="True"
              ClearButtonEnabled="False"
              FontSize="12"
              Margin="0,0,4,0">
    <ui:TextBox.InputBindings>
      <KeyBinding Key="Return"
                  Command="{Binding AddTodoCommand}" />
    </ui:TextBox.InputBindings>
  </ui:TextBox>

  <ui:Button Grid.Column="1"
             Command="{Binding RecordTodoCommand}"
             Appearance="Secondary"
             Width="32"
             Height="32"
             Padding="0"
             Margin="0,0,4,0"
             ToolTip="{loc:Str TodoPanel_Record}">
    <ui:SymbolIcon Symbol="Record24" FontSize="14" />
  </ui:Button>

  <ui:Button Grid.Column="2"
             Command="{Binding AddTodoCommand}"
             Appearance="Primary"
             Width="32"
             Height="32"
             Padding="0"
             ToolTip="{loc:Str TodoPanel_Add}">
    <ui:SymbolIcon Symbol="Add16" FontSize="14" />
  </ui:Button>
</Grid>
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: May have build warnings/errors if ViewModel changes not yet in place (RecordTodoCommand). That's OK — the XAML will compile, binding errors are runtime only.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Views/TodoPanelControl.xaml
git commit -m "feat(todo): redesign panel with Clean Elevated style, priority borders, voice button"
```

---

### Task 9: Add localization key for record button

**Files:**
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx`
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx`

- [ ] **Step 1: Add TodoPanel_Record to ViewStrings.resx**

Add a new `<data>` entry near the other TodoPanel entries:

```xml
<data name="TodoPanel_Record" xml:space="preserve">
  <value>Record voice input</value>
</data>
```

- [ ] **Step 2: Add German translation to ViewStrings.de.resx**

```xml
<data name="TodoPanel_Record" xml:space="preserve">
  <value>Spracheingabe aufnehmen</value>
</data>
```

- [ ] **Step 3: Add French translation to ViewStrings.fr.resx**

```xml
<data name="TodoPanel_Record" xml:space="preserve">
  <value>Enregistrer la saisie vocale</value>
</data>
```

- [ ] **Step 4: Build (regenerates Designer.cs)**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Resources/Strings/ViewStrings.resx src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx
git commit -m "feat(i18n): add TodoPanel_Record localization key (EN/DE/FR)"
```

---

### Task 10: Implement panel expand/collapse animation in host views

**Files:**
- Modify: `src/Pia.Wpf/Views/AssistantView.xaml` (lines 443-448)
- Modify: `src/Pia.Wpf/Views/OptimizeView.xaml` (lines 328-331)
- Modify: `src/Pia.Wpf/Views/ResearchView.xaml` (lines 398-401)

The same change applies to all 3 views. Replace the simple `BooleanToVisibilityConverter` binding with a `Style` that uses `DataTrigger` with `EnterActions`/`ExitActions` to animate width.

- [ ] **Step 1: Update AssistantView.xaml**

Replace (lines 443-448):
```xml
<local:TodoPanelControl x:Name="TodoPanel"
                        HorizontalAlignment="Right"
                        VerticalAlignment="Stretch"
                        Visibility="{Binding DataContext.IsTodoPanelOpen, ElementName=TodoPanel, Converter={StaticResource BooleanToVisibilityConverter}}" />
```

With:
```xml
<local:TodoPanelControl x:Name="TodoPanel"
                        HorizontalAlignment="Right"
                        VerticalAlignment="Stretch"
                        Width="0"
                        Visibility="Collapsed">
  <local:TodoPanelControl.Style>
    <Style TargetType="local:TodoPanelControl">
      <Style.Triggers>
        <DataTrigger Binding="{Binding DataContext.IsTodoPanelOpen, ElementName=TodoPanel}" Value="True">
          <DataTrigger.EnterActions>
            <BeginStoryboard x:Name="OpenPanel">
              <Storyboard>
                <ObjectAnimationUsingKeyFrames Storyboard.TargetProperty="Visibility">
                  <DiscreteObjectKeyFrame KeyTime="0:0:0" Value="{x:Static Visibility.Visible}" />
                </ObjectAnimationUsingKeyFrames>
                <DoubleAnimation Storyboard.TargetProperty="Width"
                                 From="0" To="280" Duration="0:0:0.25">
                  <DoubleAnimation.EasingFunction>
                    <CubicEase EasingMode="EaseOut" />
                  </DoubleAnimation.EasingFunction>
                </DoubleAnimation>
              </Storyboard>
            </BeginStoryboard>
          </DataTrigger.EnterActions>
          <DataTrigger.ExitActions>
            <BeginStoryboard>
              <Storyboard>
                <DoubleAnimation Storyboard.TargetProperty="Width"
                                 From="280" To="0" Duration="0:0:0.2">
                  <DoubleAnimation.EasingFunction>
                    <CubicEase EasingMode="EaseIn" />
                  </DoubleAnimation.EasingFunction>
                </DoubleAnimation>
                <ObjectAnimationUsingKeyFrames Storyboard.TargetProperty="Visibility">
                  <DiscreteObjectKeyFrame KeyTime="0:0:0.2" Value="{x:Static Visibility.Collapsed}" />
                </ObjectAnimationUsingKeyFrames>
              </Storyboard>
            </BeginStoryboard>
          </DataTrigger.ExitActions>
        </DataTrigger>
      </Style.Triggers>
    </Style>
  </local:TodoPanelControl.Style>
</local:TodoPanelControl>
```

- [ ] **Step 2: Apply identical change to OptimizeView.xaml**

Same pattern, but with `ElementName=OptimizeTodoPanel` and `x:Name="OptimizeTodoPanel"`.

- [ ] **Step 3: Apply identical change to ResearchView.xaml**

Same pattern, but with `ElementName=ResearchTodoPanel` and `x:Name="ResearchTodoPanel"`.

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Views/AssistantView.xaml src/Pia.Wpf/Views/OptimizeView.xaml src/Pia.Wpf/Views/ResearchView.xaml
git commit -m "feat(todo): animate panel expand/collapse in all 3 mode views"
```

---

## Chunk 3: Voice Input, Drag-and-Drop & Completion Animation

### Task 11: Add voice input and reorder commands to TodoViewModel

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/TodoViewModel.cs`
  - Constructor (line 98): add IVoiceInputService DI
  - Add RecordTodoCommand property and execute method
  - Update GetInsertIndex to use SortOrder (append to end)
  - Add reorder support methods

- [ ] **Step 1: Add IVoiceInputService dependency**

Add to the private fields (near the other service fields):

```csharp
private readonly IVoiceInputService _voiceInputService;
```

Add parameter to constructor:

```csharp
public TodoViewModel(
    ILogger<TodoViewModel> logger,
    ITodoService todoService,
    IDialogService dialogService,
    Wpf.Ui.ISnackbarService snackbarService,
    Navigation.INavigationService navigationService,
    ISettingsService settingsService,
    ILocalizationService localizationService,
    IVoiceInputService voiceInputService)
```

In constructor body:

```csharp
_voiceInputService = voiceInputService;
```

Add `using` if needed:

```csharp
using Pia.Services.Interfaces;
```

- [ ] **Step 2: Add RecordTodoCommand**

In the command declarations area of the constructor:

```csharp
RecordTodoCommand = new AsyncRelayCommand(ExecuteRecordTodoAsync);
```

Add the property:

```csharp
public IAsyncRelayCommand RecordTodoCommand { get; }
```

Add the execute method:

```csharp
private async Task ExecuteRecordTodoAsync()
{
    var transcription = await _voiceInputService.CaptureVoiceInputAsync();
    if (!string.IsNullOrWhiteSpace(transcription))
    {
        NewTodoTitle = string.IsNullOrWhiteSpace(NewTodoTitle)
            ? transcription
            : $"{NewTodoTitle.TrimEnd()} {transcription}";
        AddTodoCommand.NotifyCanExecuteChanged();
    }
}
```

- [ ] **Step 3: Update GetInsertIndex to append (SortOrder-based)**

Replace the current `GetInsertIndex` method (lines 424-435):

```csharp
/// <summary>
/// New todos are appended to the end of the list (SortOrder-based).
/// </summary>
private int GetInsertIndex(TodoItem item)
{
    return PendingTodos.Count;
}
```

- [ ] **Step 4: Add ReorderTodos method for drag-and-drop**

```csharp
public async Task ReorderTodosAsync(int oldIndex, int newIndex)
{
    if (oldIndex == newIndex || oldIndex < 0 || newIndex < 0
        || oldIndex >= PendingTodos.Count || newIndex >= PendingTodos.Count)
        return;

    PendingTodos.Move(oldIndex, newIndex);

    // Recalculate sequential sort order
    var updates = new List<(Guid Id, int SortOrder)>();
    for (var i = 0; i < PendingTodos.Count; i++)
    {
        PendingTodos[i].SortOrder = i;
        updates.Add((PendingTodos[i].Id, i));
    }

    try
    {
        await _todoService.UpdateSortOrderAsync(updates);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to persist reorder");
        // Revert the move in the UI
        PendingTodos.Move(newIndex, oldIndex);
    }
}
```

Note: `PendingTodos` must be `ObservableCollection<TodoItem>` (which it already is) for `.Move()` to work.

- [ ] **Step 5: Build and verify**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/ViewModels/TodoViewModel.cs
git commit -m "feat(todo): add voice input command and drag-drop reorder to TodoViewModel"
```

---

### Task 12: Create DragDropReorderBehavior

**Files:**
- Create: `src/Pia.Wpf/Behaviors/DragDropReorderBehavior.cs`

This is an attached behavior that enables drag-and-drop reordering on an `ItemsControl`. It handles mouse events, creates a drag adorner, and calls back to the ViewModel's `ReorderTodosAsync`.

- [ ] **Step 1: Create the Behaviors directory and file**

```bash
mkdir -p src/Pia.Wpf/Behaviors
```

- [ ] **Step 2: Write DragDropReorderBehavior**

Create `src/Pia.Wpf/Behaviors/DragDropReorderBehavior.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pia.Behaviors;

/// <summary>
/// Attached behavior that enables drag-and-drop reordering on an ItemsControl.
/// Calls a Func&lt;int, int, Task&gt; callback when items are reordered.
/// </summary>
public static class DragDropReorderBehavior
{
    private static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(150);

    // Attached property: IsEnabled
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(DragDropReorderBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    // Attached property: ReorderCallback (Func<int, int, Task>)
    public static readonly DependencyProperty ReorderCallbackProperty =
        DependencyProperty.RegisterAttached("ReorderCallback", typeof(Func<int, int, Task>), typeof(DragDropReorderBehavior));

    public static Func<int, int, Task>? GetReorderCallback(DependencyObject obj) => (Func<int, int, Task>?)obj.GetValue(ReorderCallbackProperty);
    public static void SetReorderCallback(DependencyObject obj, Func<int, int, Task>? value) => obj.SetValue(ReorderCallbackProperty, value);

    // State fields (per-control, stored via attached properties would be complex — use static for single-instance panel)
    private static Point _startPoint;
    private static int _dragIndex = -1;
    private static bool _isDragging;
    private static DispatcherTimer? _holdTimer;
    private static UIElement? _pressedElement;
    private static DragAdorner? _adorner;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl itemsControl) return;

        if ((bool)e.NewValue)
        {
            itemsControl.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
            itemsControl.PreviewMouseMove += OnPreviewMouseMove;
            itemsControl.PreviewMouseLeftButtonUp += OnPreviewMouseUp;
            itemsControl.AllowDrop = true;
            itemsControl.Drop += OnDrop;
            itemsControl.DragOver += OnDragOver;
        }
        else
        {
            itemsControl.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
            itemsControl.PreviewMouseMove -= OnPreviewMouseMove;
            itemsControl.PreviewMouseLeftButtonUp -= OnPreviewMouseUp;
            itemsControl.Drop -= OnDrop;
            itemsControl.DragOver -= OnDragOver;
        }
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl itemsControl) return;

        // Don't start drag if clicking on a CheckBox
        if (e.OriginalSource is DependencyObject source && FindAncestor<CheckBox>(source) is not null)
            return;

        var container = FindItemContainer(itemsControl, e.OriginalSource as DependencyObject);
        if (container is null) return;

        _startPoint = e.GetPosition(itemsControl);
        _dragIndex = itemsControl.ItemContainerGenerator.IndexFromContainer(container);
        _pressedElement = container;

        // Start hold timer
        _holdTimer = new DispatcherTimer { Interval = HoldThreshold };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            // Hold threshold reached — ready to drag on next move
        };
        _holdTimer.Start();
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragIndex < 0 || sender is not ItemsControl itemsControl) return;
        if (_holdTimer is { IsEnabled: true }) return; // Still waiting for hold threshold

        var currentPos = e.GetPosition(itemsControl);
        var diff = _startPoint - currentPos;

        if (e.LeftButton == MouseButtonState.Pressed && !_isDragging
            && (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            _isDragging = true;

            // Visual feedback: reduce opacity
            if (_pressedElement is not null)
                _pressedElement.Opacity = 0.4;

            var data = new DataObject("DragIndex", _dragIndex);
            DragDrop.DoDragDrop(itemsControl, data, DragDropEffects.Move);

            // Cleanup after drag completes
            if (_pressedElement is not null)
                _pressedElement.Opacity = 1.0;
            ResetState();
        }
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl itemsControl) return;
        if (!e.Data.GetDataPresent("DragIndex")) return;

        var oldIndex = (int)e.Data.GetData("DragIndex")!;
        var dropContainer = FindItemContainer(itemsControl, e.OriginalSource as DependencyObject);
        if (dropContainer is null) return;

        var newIndex = itemsControl.ItemContainerGenerator.IndexFromContainer(dropContainer);
        if (newIndex < 0 || oldIndex == newIndex) return;

        var callback = GetReorderCallback(itemsControl);
        callback?.Invoke(oldIndex, newIndex);
    }

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _holdTimer?.Stop();
        if (!_isDragging) ResetState();
    }

    private static void ResetState()
    {
        _dragIndex = -1;
        _isDragging = false;
        _pressedElement = null;
        _holdTimer?.Stop();
        _holdTimer = null;
    }

    private static FrameworkElement? FindItemContainer(ItemsControl itemsControl, DependencyObject? source)
    {
        while (source is not null && source != itemsControl)
        {
            if (source is FrameworkElement fe && itemsControl.ItemContainerGenerator.IndexFromContainer(fe) >= 0)
                return fe;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj is not null)
        {
            if (obj is T target) return target;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/Behaviors/DragDropReorderBehavior.cs
git commit -m "feat(todo): add DragDropReorderBehavior attached behavior for reordering"
```

---

### Task 13: Wire drag-and-drop behavior in TodoPanelControl XAML

**Files:**
- Modify: `src/Pia.Wpf/Views/TodoPanelControl.xaml`
- Modify: `src/Pia.Wpf/Views/TodoPanelControl.xaml.cs`

- [ ] **Step 1: Add namespace to TodoPanelControl.xaml**

Add to the `<UserControl>` declarations:

```xml
xmlns:behaviors="clr-namespace:Pia.Behaviors"
```

- [ ] **Step 2: Attach behavior to ItemsControl**

On the `ItemsControl` that shows `PendingTodos`, add:

```xml
<ItemsControl ItemsSource="{Binding PendingTodos}"
              behaviors:DragDropReorderBehavior.IsEnabled="True">
```

- [ ] **Step 3: Wire ReorderCallback in code-behind**

In `TodoPanelControl.xaml.cs`, after `DataContext = vm;` in the `OnLoaded` method, add:

```csharp
// Wire drag-and-drop reorder callback
if (FindName("PendingTodosList") is ItemsControl todoList && vm is not null)
{
    Behaviors.DragDropReorderBehavior.SetReorderCallback(todoList,
        async (oldIndex, newIndex) => await vm.ReorderTodosAsync(oldIndex, newIndex));
}
```

Give the `ItemsControl` in XAML an `x:Name="PendingTodosList"` so we can find it.

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Views/TodoPanelControl.xaml src/Pia.Wpf/Views/TodoPanelControl.xaml.cs
git commit -m "feat(todo): wire drag-and-drop reorder behavior in todo panel"
```

---

### Task 14: Implement completion animation

**Files:**
- Modify: `src/Pia.Wpf/Views/TodoPanelControl.xaml` (todo item DataTemplate)
- Modify: `src/Pia.Wpf/Views/TodoPanelControl.xaml.cs` (animation-to-command wiring)

The completion animation sequence: strikethrough draws across title -> fade out -> row collapses -> command fires.

- [ ] **Step 1: Remove direct CheckBox Command binding**

In the todo item DataTemplate, change the CheckBox to no longer directly call `CompleteTodoCommand`. Instead, use a `Checked` event:

Old:
```xml
<CheckBox Grid.Column="0"
          IsChecked="False"
          VerticalAlignment="Center"
          Margin="0,0,8,0"
          Command="{Binding DataContext.CompleteTodoCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
          CommandParameter="{Binding}" />
```

New:
```xml
<CheckBox Grid.Column="0"
          IsChecked="False"
          VerticalAlignment="Center"
          Margin="0,0,8,0"
          Checked="OnTodoCheckBoxChecked"
          Tag="{Binding}" />
```

- [ ] **Step 2: Add strikethrough Line to the title area**

Wrap the title TextBlock in a Grid and add a Line overlay:

```xml
<Grid Grid.Column="1">
  <TextBlock x:Name="TodoTitle"
             Text="{Binding Title}"
             FontSize="13"
             TextTrimming="CharacterEllipsis"
             VerticalAlignment="Center">
    <TextBlock.Style>
      <Style TargetType="TextBlock">
        <Style.Triggers>
          <DataTrigger Binding="{Binding Notes, Converter={StaticResource StringNotNullOrEmptyToBoolConverter}}" Value="True">
            <Setter Property="ToolTip" Value="{Binding Notes}" />
          </DataTrigger>
        </Style.Triggers>
      </Style>
    </TextBlock.Style>
  </TextBlock>
  <Line x:Name="StrikethroughLine"
        Y1="0.5" Y2="0.5"
        X1="0" X2="0"
        Stroke="{DynamicResource TextPlaceholderColorBrush}"
        StrokeThickness="1.5"
        VerticalAlignment="Center"
        Stretch="None"
        Visibility="Collapsed" />
</Grid>
```

- [ ] **Step 3: Implement completion animation in code-behind**

Add to `TodoPanelControl.xaml.cs`:

```csharp
using System.Windows.Media.Animation;
using System.Windows.Shapes;

private async void OnTodoCheckBoxChecked(object sender, RoutedEventArgs e)
{
    if (sender is not CheckBox checkBox || checkBox.Tag is not TodoItem todo)
        return;

    // Find the parent border (the todo item card)
    var itemBorder = FindAncestor<Border>(checkBox);
    if (itemBorder is null) return;

    // Find the strikethrough line and title
    var strikethrough = FindChild<Line>(itemBorder, "StrikethroughLine");
    var titleBlock = FindChild<TextBlock>(itemBorder, "TodoTitle");

    if (strikethrough is not null && titleBlock is not null)
    {
        // Make strikethrough visible and animate it
        strikethrough.Visibility = Visibility.Visible;
        var titleWidth = titleBlock.ActualWidth;

        var strikeAnim = new DoubleAnimation(0, titleWidth, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        strikethrough.BeginAnimation(Line.X2Property, strikeAnim);

        // Wait for strikethrough to finish
        await Task.Delay(200);
    }

    // Fade out
    var fadeAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300))
    {
        BeginTime = TimeSpan.FromMilliseconds(150)
    };

    // Row collapse (animate MaxHeight)
    var currentHeight = itemBorder.ActualHeight;
    var collapseAnim = new DoubleAnimation(currentHeight, 0, TimeSpan.FromMilliseconds(250))
    {
        BeginTime = TimeSpan.FromMilliseconds(150),
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
    };

    var marginAnim = new ThicknessAnimation(itemBorder.Margin, new Thickness(0), TimeSpan.FromMilliseconds(250))
    {
        BeginTime = TimeSpan.FromMilliseconds(150),
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
    };

    var tcs = new TaskCompletionSource();
    collapseAnim.Completed += (_, _) => tcs.SetResult();

    itemBorder.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
    itemBorder.BeginAnimation(FrameworkElement.MaxHeightProperty, collapseAnim);
    itemBorder.BeginAnimation(FrameworkElement.MarginProperty, marginAnim);

    await tcs.Task;

    // Execute the complete command
    if (DataContext is TodoViewModel vm)
    {
        var pendingCountBefore = vm.PendingTodos.Count;
        await vm.CompleteTodoCommand.ExecuteAsync(todo);

        // Check if the completion actually succeeded by seeing if the item was removed
        // (AsyncRelayCommand catches exceptions internally, so we can't rely on try/catch)
        if (vm.PendingTodos.Contains(todo) || vm.PendingTodos.Count == pendingCountBefore)
        {
            // Revert animation on failure
            itemBorder.BeginAnimation(UIElement.OpacityProperty, null);
            itemBorder.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            itemBorder.BeginAnimation(FrameworkElement.MarginProperty, null);
            itemBorder.Opacity = 1;
            itemBorder.Margin = new Thickness(8, 0, 8, 3);
            if (strikethrough is not null)
                strikethrough.Visibility = Visibility.Collapsed;
            checkBox.IsChecked = false;
        }
    }
}

private static T? FindAncestor<T>(DependencyObject? obj) where T : DependencyObject
{
    while (obj is not null)
    {
        obj = VisualTreeHelper.GetParent(obj);
        if (obj is T target) return target;
    }
    return null;
}

private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
{
    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
    {
        var child = VisualTreeHelper.GetChild(parent, i);
        if (child is T fe && fe.Name == name) return fe;
        var result = FindChild<T>(child, name);
        if (result is not null) return result;
    }
    return null;
}
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Views/TodoPanelControl.xaml src/Pia.Wpf/Views/TodoPanelControl.xaml.cs
git commit -m "feat(todo): add completion animation (strikethrough + fade + collapse)"
```

---

## Chunk 4: Integration & Final Verification

### Task 15: Register IVoiceInputService for TodoViewModel DI

**Files:**
- Check: `src/Pia.Wpf/App.xaml.cs` or wherever DI services are registered

`TodoViewModel` now requires `IVoiceInputService` in its constructor. Verify that:
1. `IVoiceInputService` is already registered in the DI container (it should be, since other ViewModels use it)
2. `TodoViewModel` is registered correctly

- [ ] **Step 1: Check DI registration**

Search for `IVoiceInputService` registration and `TodoViewModel` registration in the DI container setup. If `IVoiceInputService` is already registered as a singleton/scoped service (used by AssistantViewModel), no change needed. If `TodoViewModel` constructor change causes a DI resolution failure, add the missing registration.

Run: `dotnet build src/Pia.Wpf/Pia.Wpf.csproj && dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj`
Expected: App launches, todo panel opens in all 3 modes

- [ ] **Step 2: Commit if any registration changes needed**

```bash
git add src/Pia.Wpf/App.xaml.cs
git commit -m "fix(di): register IVoiceInputService for TodoViewModel"
```

---

### Task 16: Full manual integration test

- [ ] **Step 1: Build entire solution**

Run: `dotnet build`
Expected: Build succeeded (0 errors)

- [ ] **Step 2: Run existing tests**

Run: `dotnet test`
Expected: All tests pass

- [ ] **Step 3: Manual verification checklist**

Launch the app and verify each feature:

1. **Panel redesign**: Open todo panel in each mode (Optimize, Assistant, Research). Verify:
   - Rounded left corners, drop shadow
   - Todo items have colored left border (red = high, orange = medium, blue = low)
   - Drag grip icon appears on hover
   - Record button visible between text input and add button
   - Both light and dark themes look correct

2. **Panel animation**: Toggle the panel open/close. Verify:
   - Panel expands smoothly from 0 to 280px width
   - Panel collapses smoothly
   - No visual glitches or content overflow

3. **Voice input**: Click record button. Verify:
   - Recording dialog appears (Whisper)
   - Transcribed text appears in the input field
   - Text is not auto-submitted

4. **Drag-and-drop**: Add 3+ todos, then:
   - Press and hold on a todo item body, drag to new position
   - Verify items reorder
   - Close and reopen panel — order persists
   - Restart app — order persists

5. **Completion animation**: Check a todo item. Verify:
   - Strikethrough line draws across title
   - Item fades out
   - Row collapses smoothly
   - Item moves to completed section

- [ ] **Step 4: Final commit if any fixes needed**

```bash
git add -A
git commit -m "fix(todo): integration fixes for panel UX enhancements"
```
