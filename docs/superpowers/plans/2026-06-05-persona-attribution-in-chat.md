# Persona Attribution in Chat Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show which persona generated each assistant message — the persona's glyph as the top-left avatar, and the persona name in the message footer as `Tokens · Persona · Model`.

**Architecture:** Snapshot the persona (id/name/emoji) onto each `AssistantMessage` at send time. Persist the snapshot through the existing VM→DTO mapper, the local SQLite message table (new columns), and the E2EE `SyncMapper` (which serializes whole message objects — no change needed). Render via a new reusable `PiaPersonaAvatar` control and a `PersonaName` property on `PiaAnswerToolbar`. Legacy messages with no snapshot fall back to the Pia icon and the unchanged `Tokens · Model` footer.

**Tech Stack:** C# / .NET 10, WPF (MVVM, CommunityToolkit.Mvvm `[ObservableProperty]`), Microsoft.Data.Sqlite, xunit.v3 (Microsoft.Testing.Platform via `global.json`, plain `Xunit.Assert`).

**Spec:** `docs/superpowers/specs/2026-06-05-persona-attribution-in-chat-design.md`

**Conventions for every step:**
- Build: `dotnet build` (from `C:\projects\Pia.Wpf`).
- Test: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj` (this repo runs the whole test project — MTP filtering is unreliable here, so run the project and read the summary).
- Tests use plain `Xunit.Assert` (no FluentAssertions). Internals of `Pia.Wpf` are visible to `Pia.Wpf.Tests` (`InternalsVisibleTo`).
- Follow CLAUDE.md: 4-space C# indent, 2-space XAML, `_camelCase` fields, MVVM, privacy-first logging (no sensitive payloads in release logs — persona name is a user-named item, so never `LogInformation` it; it is fine in the DTO/UI).

---

## Chunk 1: Model, DTO, mapping, stamp

This chunk is pure C# (no WPF), fully unit-tested.

### Task 1: `PersonaAttribution` snapshot + `AssistantMessage` wiring

**Files:**
- Modify: `src/Pia.Wpf/Models/ChatMessageExtras.cs`
- Modify: `src/Pia.Wpf/Models/AssistantMessage.cs`
- Test: `tests/Pia.Wpf.Tests/Models/AssistantMessageAttributionTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/Pia.Wpf.Tests/Models/AssistantMessageAttributionTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Shared;
using Xunit;

namespace Pia.Tests.Models;

public class AssistantMessageAttributionTests
{
    [Fact]
    public void NoPersona_FallsBackToPiaIcon()
    {
        var msg = new AssistantMessage(ChatRole.Assistant, "hi");

        Assert.False(msg.HasPersona);
        Assert.Equal(BuiltInPersonas.PiaPersonalId, msg.PersonaGlyphId);
        Assert.Null(msg.PersonaGlyphEmoji);
    }

    [Fact]
    public void WithPersona_ExposesGlyphIdAndEmoji()
    {
        var id = Guid.NewGuid();
        var msg = new AssistantMessage(ChatRole.Assistant, "hi")
        {
            Persona = new PersonaAttribution(id, "Marketing Writer", "✍️"),
        };

        Assert.True(msg.HasPersona);
        Assert.Equal(id, msg.PersonaGlyphId);
        Assert.Equal("✍️", msg.PersonaGlyphEmoji);
    }

    [Fact]
    public void From_MapsPersonaFields()
    {
        var persona = new Persona { Name = "Coder", SystemPrompt = "x", Emoji = "💻" };

        var attr = PersonaAttribution.From(persona);

        Assert.Equal(persona.Id, attr.Id);
        Assert.Equal("Coder", attr.Name);
        Assert.Equal("💻", attr.Emoji);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
Expected: FAIL to **compile** (`PersonaAttribution`, `Persona`/`HasPersona`/`PersonaGlyphId`/`PersonaGlyphEmoji` don't exist yet).

- [ ] **Step 3: Add the `PersonaAttribution` record**

In `src/Pia.Wpf/Models/ChatMessageExtras.cs`, add (the file already declares `namespace Pia.Models;`):

```csharp
/// <summary>
/// Immutable snapshot of the persona that produced an assistant message, taken at send time
/// so renames/deletes of the live persona never change historical attribution.
/// </summary>
public sealed record PersonaAttribution(Guid Id, string Name, string? Emoji)
{
    public static PersonaAttribution From(Persona persona) =>
        new(persona.Id, persona.Name, persona.Emoji);
}
```

- [ ] **Step 4: Wire it into `AssistantMessage`**

In `src/Pia.Wpf/Models/AssistantMessage.cs`:

Add `using Pia.Shared;` to the usings (for `BuiltInPersonas`).

Add the observable property next to the other `[ObservableProperty]` fields (near `_meta`/`_stats`):

```csharp
[ObservableProperty]
private PersonaAttribution? _persona;
```

Add the computed helpers next to `HasAttachment`/`IsUser`:

```csharp
public bool HasPersona => Persona is not null;

/// <summary>Glyph id for the avatar: the snapshot's persona, or the Pia icon for legacy messages.</summary>
public Guid PersonaGlyphId => Persona?.Id ?? BuiltInPersonas.PiaPersonalId;

public string? PersonaGlyphEmoji => Persona?.Emoji;
```

Add the change-notification partial next to `OnContentChanged`/`OnAttachmentChanged`:

```csharp
partial void OnPersonaChanged(PersonaAttribution? value)
{
    OnPropertyChanged(nameof(HasPersona));
    OnPropertyChanged(nameof(PersonaGlyphId));
    OnPropertyChanged(nameof(PersonaGlyphEmoji));
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
Expected: PASS (the 3 new tests, plus existing tests still green).

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Models/ChatMessageExtras.cs src/Pia.Wpf/Models/AssistantMessage.cs tests/Pia.Wpf.Tests/Models/AssistantMessageAttributionTests.cs
git commit -m "feat(personas): add per-message persona attribution snapshot"
```

---

### Task 2: Persist the snapshot on the DTO

**Files:**
- Modify: `src/Pia.Shared/Models/SyncAssistantChat.cs`
- Test: `tests/Pia.Wpf.Tests/Services/SyncMapperAssistantChatTests.cs` (extend)

The `SyncMapper` serializes whole `SyncAssistantChatMessage` objects (by-reference in plaintext, JSON via `EncryptRecord` in E2EE), so adding a public property is enough for it to round-trip. We add a regression test to prove it.

- [ ] **Step 1: Write the failing test**

In `tests/Pia.Wpf.Tests/Services/SyncMapperAssistantChatTests.cs`, in `SampleChat()` add a persona snapshot to the assistant message (the second message object), after `ModelName = "gpt-5",`:

```csharp
                    Persona = new SyncMessagePersona
                    {
                        Id = Guid.Parse("0000000A-0000-0000-0000-000000000004"),
                        Name = "Marketing Writer",
                        Emoji = "✍️",
                    },
```

Then add a new assertion to `AssistantChat_RoundTrips_E2EE` after the existing `Tokens` assertion (this is the strongest test — it proves the field survives encrypt→decrypt JSON):

```csharp
        Assert.Equal(original.Messages[1].Persona!.Name, back.Messages[1].Persona!.Name);
        Assert.Equal(original.Messages[1].Persona!.Id, back.Messages[1].Persona!.Id);
        Assert.Equal(original.Messages[1].Persona!.Emoji, back.Messages[1].Persona!.Emoji);
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
Expected: FAIL to compile (`SyncMessagePersona` / `Persona` don't exist on the DTO).

- [ ] **Step 3: Add the DTO type and property**

In `src/Pia.Shared/Models/SyncAssistantChat.cs`, inside `class SyncAssistantChatMessage`, add a property next to `ModelName` (before the `[JsonExtensionData]` member):

```csharp
    /// <summary>
    /// Persona that produced this (assistant) message; null for user messages and for
    /// messages saved before persona attribution existed. Old clients round-trip this
    /// via <see cref="ExtensionData"/>.
    /// </summary>
    public SyncMessagePersona? Persona { get; set; }
```

Add the new type at the end of the file (same namespace `Pia.Shared.Models`):

```csharp
/// <summary>Snapshot of the persona that produced an assistant message (see PersonaAttribution client-side).</summary>
public sealed class SyncMessagePersona
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Emoji { get; set; }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
Expected: PASS (both round-trip tests, including the new persona assertions).

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Shared/Models/SyncAssistantChat.cs tests/Pia.Wpf.Tests/Services/SyncMapperAssistantChatTests.cs
git commit -m "feat(personas): persist persona snapshot on SyncAssistantChatMessage"
```

---

### Task 3: Extract a shared VM↔DTO mapper (DRY) and map the snapshot

`AssistantViewModel.MapToDto`/`MapFromDto` and `AssistantHistoryViewModel.MapFromDto` are duplicated. Extract one shared mapper so the persona mapping cannot drift, and unit-test it directly.

**Files:**
- Create: `src/Pia.Wpf/ViewModels/AssistantMessageMapper.cs`
- Modify: `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` (remove local `MapToDto`/`MapFromDto`, call the shared mapper)
- Modify: `src/Pia.Wpf/ViewModels/AssistantHistoryViewModel.cs` (remove local `MapFromDto`, call the shared mapper)
- Test: `tests/Pia.Wpf.Tests/ViewModels/AssistantMessageMapperTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/Pia.Wpf.Tests/ViewModels/AssistantMessageMapperTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Shared.Models;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public class AssistantMessageMapperTests
{
    [Fact]
    public void RoundTrip_PreservesPersona()
    {
        var src = new AssistantMessage(ChatRole.Assistant, "answer")
        {
            Stats = new AnswerStats(142, "gpt-5"),
            Persona = new PersonaAttribution(Guid.NewGuid(), "Marketing Writer", "✍️"),
        };

        var dto = AssistantMessageMapper.ToDto(src);
        Assert.Equal("Marketing Writer", dto.Persona!.Name);

        var back = AssistantMessageMapper.FromDto(dto);
        Assert.True(back.HasPersona);
        Assert.Equal(src.Persona.Id, back.Persona!.Id);
        Assert.Equal("Marketing Writer", back.Persona.Name);
        Assert.Equal("✍️", back.Persona.Emoji);
    }

    [Fact]
    public void FromDto_LegacyMessage_NoPersona_FallsBack()
    {
        var dto = new SyncAssistantChatMessage
        {
            Id = Guid.NewGuid(),
            Role = "assistant",
            Content = "legacy",
            Timestamp = DateTime.UtcNow,
            Persona = null,
        };

        var back = AssistantMessageMapper.FromDto(dto);

        Assert.False(back.HasPersona);
        Assert.Equal(Pia.Shared.BuiltInPersonas.PiaPersonalId, back.PersonaGlyphId);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
Expected: FAIL to compile (`AssistantMessageMapper` does not exist).

- [ ] **Step 3: Create the shared mapper**

Create `src/Pia.Wpf/ViewModels/AssistantMessageMapper.cs`:

```csharp
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Shared.Models;

namespace Pia.ViewModels;

/// <summary>
/// Maps between the in-memory <see cref="AssistantMessage"/> and its persistence/sync DTO
/// <see cref="SyncAssistantChatMessage"/>. Single source of truth shared by AssistantViewModel
/// (save + resume) and AssistantHistoryViewModel (inspector preview).
/// </summary>
internal static class AssistantMessageMapper
{
    public static SyncAssistantChatMessage ToDto(AssistantMessage m) => new()
    {
        Id = m.Id,
        Role = m.IsUser ? "user" : "assistant",
        Content = m.Content,
        ThinkingContent = string.IsNullOrEmpty(m.ThinkingContent) ? null : m.ThinkingContent,
        Timestamp = m.Timestamp.ToUniversalTime(),
        Tokens = m.Stats?.Tokens,
        ModelName = m.Stats?.Model,
        Persona = m.Persona is { } p
            ? new SyncMessagePersona { Id = p.Id, Name = p.Name, Emoji = p.Emoji }
            : null,
    };

    public static AssistantMessage FromDto(SyncAssistantChatMessage dto)
    {
        var role = dto.Role == "user" ? ChatRole.User : ChatRole.Assistant;
        var message = new AssistantMessage(dto.Id, role, dto.Content, dto.Timestamp.ToLocalTime());
        if (!string.IsNullOrEmpty(dto.ThinkingContent))
            message.ThinkingContent = dto.ThinkingContent;
        if (dto.Tokens is { } tokens && !string.IsNullOrEmpty(dto.ModelName))
            message.Stats = new AnswerStats(tokens, dto.ModelName);
        if (dto.Persona is { } p)
            message.Persona = new PersonaAttribution(p.Id, p.Name, p.Emoji);
        return message;
    }
}
```

- [ ] **Step 4: Point `AssistantViewModel` at the shared mapper**

In `src/Pia.Wpf/ViewModels/AssistantViewModel.cs`:
- Change the save call (~line 689) from `Messages = [.. Messages.Select(MapToDto)],` to `Messages = [.. Messages.Select(AssistantMessageMapper.ToDto)],`.
- Change the resume mapping (~line 452, inside the resume loop) from `MapFromDto(msg)` to `AssistantMessageMapper.FromDto(msg)`. (Search for `MapFromDto(` to find the call site.)
- Delete the now-unused private `static SyncAssistantChatMessage MapToDto(...)` (~line 842) and private `static AssistantMessage MapFromDto(...)` (~line 1168) methods.

- [ ] **Step 5: Point `AssistantHistoryViewModel` at the shared mapper**

In `src/Pia.Wpf/ViewModels/AssistantHistoryViewModel.cs`:
- Change the call (~line 452) `SelectedChatMessages.Add(MapFromDto(msg));` to `SelectedChatMessages.Add(AssistantMessageMapper.FromDto(msg));`.
- Delete the now-unused private `static AssistantMessage MapFromDto(...)` (~line 467).

- [ ] **Step 6: Run build + tests**

Run: `dotnet build` then `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
Expected: build succeeds (no unused-method warnings treated as errors), new mapper tests PASS, existing tests still green.

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/ViewModels/AssistantMessageMapper.cs src/Pia.Wpf/ViewModels/AssistantViewModel.cs src/Pia.Wpf/ViewModels/AssistantHistoryViewModel.cs tests/Pia.Wpf.Tests/ViewModels/AssistantMessageMapperTests.cs
git commit -m "refactor(personas): share AssistantMessage<->DTO mapper, map persona snapshot"
```

---

### Task 4: Stamp the persona at send time

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/AssistantViewModel.cs` (in `ExecuteSendMessage`)

`ExecuteSendMessage` already resolves the per-turn persona (`var persona = await _personaService.ResolveActiveAsync(...)`, ~line 468). The assistant message was created just above (~line 458). Stamp it. (No new unit test: this is a one-line assignment using the `PersonaAttribution.From` factory already covered in Task 1; the codebase tests pure helpers, not the full `ExecuteSendMessage`. Verified manually in Chunk 3 run.)

- [ ] **Step 1: Add the stamp**

In `src/Pia.Wpf/ViewModels/AssistantViewModel.cs`, immediately after the persona is resolved in `ExecuteSendMessage` (right below the `var persona = await _personaService.ResolveActiveAsync(...)` line, ~line 468):

```csharp
            assistantMessage.Persona = PersonaAttribution.From(persona);
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/ViewModels/AssistantViewModel.cs
git commit -m "feat(personas): stamp resolved persona onto assistant message on send"
```

---

## Chunk 2: Local SQLite persistence

The local store writes/reads message columns explicitly, so the snapshot needs new columns plus a migration for existing databases.

### Task 5: Persona columns in the message table

**Files:**
- Modify: `src/Pia.Wpf/Infrastructure/SqliteContext.cs` (table DDL + `MigrateSchema`)
- Modify: `src/Pia.Wpf/Services/AssistantChatService.cs` (INSERT + SELECT)
- Test: `tests/Pia.Wpf.Tests/Unit/AssistantChatServiceTests.cs` (extend)

- [ ] **Step 1: Write the failing test**

In `tests/Pia.Wpf.Tests/Unit/AssistantChatServiceTests.cs`, add a test that saves a chat whose assistant message carries a persona and reads it back:

```csharp
    [Fact]
    public async Task SaveAndGet_RoundTripsPersonaSnapshot()
    {
        var chat = MakeChat(title: "Persona test", body: "user question");
        var personaId = Guid.NewGuid();
        chat.Messages.Add(new SyncAssistantChatMessage
        {
            Id = Guid.NewGuid(),
            Role = "assistant",
            Content = "assistant answer",
            Timestamp = DateTime.UtcNow,
            Tokens = 10,
            ModelName = "gpt-5",
            Persona = new SyncMessagePersona { Id = personaId, Name = "Marketing Writer", Emoji = "✍️" },
        });

        await _service.SaveAsync(chat);
        _createdIds.Add(chat.Id);

        var loaded = await _service.GetAsync(chat.Id);
        Assert.NotNull(loaded);
        var assistant = loaded!.Messages.Single(m => m.Role == "assistant");
        Assert.NotNull(assistant.Persona);
        Assert.Equal(personaId, assistant.Persona!.Id);
        Assert.Equal("Marketing Writer", assistant.Persona.Name);
        Assert.Equal("✍️", assistant.Persona.Emoji);

        var user = loaded.Messages.Single(m => m.Role == "user");
        Assert.Null(user.Persona);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
Expected: FAIL — `assistant.Persona` is null (columns not written/read yet).

- [ ] **Step 3: Add columns to the table DDL**

In `src/Pia.Wpf/Infrastructure/SqliteContext.cs`, in the `CREATE TABLE IF NOT EXISTS AssistantChatMessages (...)` block (~line 227), add three columns after `ModelName       TEXT,`:

```sql
                PersonaId       TEXT,
                PersonaName     TEXT,
                PersonaEmoji    TEXT,
```

(Keep them before the `FOREIGN KEY (ChatId) ...` line.)

- [ ] **Step 4: Add the migration for existing databases**

In `src/Pia.Wpf/Infrastructure/SqliteContext.cs`, inside `MigrateSchema()` (anywhere among the other column-add blocks, mirroring the `ScheduledJobs` pattern at ~line 420), add:

```csharp
        // Persona attribution snapshot on assistant messages.
        var hasPersonaId = false;
        var hasPersonaName = false;
        var hasPersonaEmoji = false;
        using (var p = _connection!.CreateCommand())
        {
            p.CommandText = "PRAGMA table_info(AssistantChatMessages)";
            using var r = p.ExecuteReader();
            while (r.Read())
            {
                var col = r.GetString(1);
                if (col == "PersonaId") hasPersonaId = true;
                else if (col == "PersonaName") hasPersonaName = true;
                else if (col == "PersonaEmoji") hasPersonaEmoji = true;
            }
        }
        if (!hasPersonaId)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AssistantChatMessages ADD COLUMN PersonaId TEXT";
            addCol.ExecuteNonQuery();
        }
        if (!hasPersonaName)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AssistantChatMessages ADD COLUMN PersonaName TEXT";
            addCol.ExecuteNonQuery();
        }
        if (!hasPersonaEmoji)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AssistantChatMessages ADD COLUMN PersonaEmoji TEXT";
            addCol.ExecuteNonQuery();
        }
```

- [ ] **Step 5: Write the columns on INSERT**

In `src/Pia.Wpf/Services/AssistantChatService.cs`, in `SaveCoreAsync` (~line 77), extend the INSERT column list and VALUES, then add parameters:

Change the command text to:

```csharp
            insertMessage.CommandText = """
                INSERT INTO AssistantChatMessages
                    (Id, ChatId, Ordinal, Role, Content, ThinkingContent, Timestamp, Tokens, ModelName, PersonaId, PersonaName, PersonaEmoji)
                VALUES
                    (@Id, @ChatId, @Ordinal, @Role, @Content, @ThinkingContent, @Timestamp, @Tokens, @ModelName, @PersonaId, @PersonaName, @PersonaEmoji)
                """;
```

After the `@ModelName` parameter line (~line 91), add:

```csharp
            insertMessage.Parameters.AddWithValue("@PersonaId", (object?)msg.Persona?.Id.ToString() ?? DBNull.Value);
            insertMessage.Parameters.AddWithValue("@PersonaName", (object?)msg.Persona?.Name ?? DBNull.Value);
            insertMessage.Parameters.AddWithValue("@PersonaEmoji", (object?)msg.Persona?.Emoji ?? DBNull.Value);
```

- [ ] **Step 6: Read the columns on SELECT**

In `src/Pia.Wpf/Services/AssistantChatService.cs`, in `GetMessagesAsync` (~line 340), extend the SELECT and the reader:

Change the command text to:

```csharp
        command.CommandText = """
            SELECT Id, Role, Content, ThinkingContent, Timestamp, Tokens, ModelName, PersonaId, PersonaName, PersonaEmoji
            FROM AssistantChatMessages
            WHERE ChatId = @ChatId
            ORDER BY Ordinal ASC
            """;
```

Change the `messages.Add(new SyncAssistantChatMessage { ... })` to include the persona, after `ModelName = ...`:

```csharp
                ModelName = reader.IsDBNull(6) ? null : reader.GetString(6),
                Persona = reader.IsDBNull(7)
                    ? null
                    : new SyncMessagePersona
                    {
                        Id = Guid.Parse(reader.GetString(7)),
                        Name = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                        Emoji = reader.IsDBNull(9) ? null : reader.GetString(9),
                    },
```

Add `using Pia.Shared.Models;` to the file if `SyncMessagePersona` is not already in scope (the file already uses `SyncAssistantChatMessage`, so the namespace is likely imported — verify before adding).

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
Expected: PASS (round-trip test green; existing `AssistantChatServiceTests` still green — the new columns are additive).

- [ ] **Step 8: Commit**

```bash
git add src/Pia.Wpf/Infrastructure/SqliteContext.cs src/Pia.Wpf/Services/AssistantChatService.cs tests/Pia.Wpf.Tests/Unit/AssistantChatServiceTests.cs
git commit -m "feat(personas): persist persona snapshot columns in local chat store"
```

---

## Chunk 3: UI — avatar glyph + footer name

WPF rendering. The footer composition logic is extracted to a pure static class so it is unit-testable without WPF; the visual controls are verified by building and running the app.

### Task 6: `FooterSummaryFormatter` (pure, testable)

**Files:**
- Create: `src/Pia.Wpf/Controls/Chat/FooterSummaryFormatter.cs`
- Test: `tests/Pia.Wpf.Tests/Controls/FooterSummaryFormatterTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/Pia.Wpf.Tests/Controls/FooterSummaryFormatterTests.cs`:

```csharp
using Pia.Controls.Chat;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Controls;

public class FooterSummaryFormatterTests
{
    [Fact]
    public void StatsAndPersona_ShowsTokensPersonaModel()
    {
        var text = FooterSummaryFormatter.Compose(new AnswerStats(1234, "gpt-4o"), "Marketing Writer");
        Assert.Equal("1,234 Tokens · Marketing Writer · gpt-4o", text);
    }

    [Fact]
    public void StatsOnly_Unchanged()
    {
        var text = FooterSummaryFormatter.Compose(new AnswerStats(1234, "gpt-4o"), null);
        Assert.Equal("1,234 Tokens · gpt-4o", text);
    }

    [Fact]
    public void PersonaOnly_NoStats_ShowsName()
    {
        var text = FooterSummaryFormatter.Compose(null, "Marketing Writer");
        Assert.Equal("Marketing Writer", text);
    }

    [Fact]
    public void Neither_IsEmpty()
    {
        Assert.Equal(string.Empty, FooterSummaryFormatter.Compose(null, null));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
Expected: FAIL to compile (`FooterSummaryFormatter` missing).

- [ ] **Step 3: Implement the formatter**

Create `src/Pia.Wpf/Controls/Chat/FooterSummaryFormatter.cs`:

```csharp
using Pia.Models;

namespace Pia.Controls.Chat;

/// <summary>
/// Builds the assistant-message footer text: token count, persona name, model — joined by " · ",
/// omitting any part that is absent. Pure so it can be unit-tested without WPF.
/// </summary>
internal static class FooterSummaryFormatter
{
    public static string Compose(AnswerStats? stats, string? personaName)
    {
        var parts = new List<string>(3);
        if (stats is not null)
            parts.Add($"{stats.Tokens:N0} Tokens");
        if (!string.IsNullOrWhiteSpace(personaName))
            parts.Add(personaName);
        if (stats is not null && !string.IsNullOrWhiteSpace(stats.Model))
            parts.Add(stats.Model);
        return string.Join(" · ", parts);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
Expected: PASS (4 new tests).

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Controls/Chat/FooterSummaryFormatter.cs tests/Pia.Wpf.Tests/Controls/FooterSummaryFormatterTests.cs
git commit -m "feat(personas): footer text formatter (tokens · persona · model)"
```

---

### Task 7: Add `PersonaName` to `PiaAnswerToolbar` and use the formatter

**Files:**
- Modify: `src/Pia.Wpf/Controls/Chat/PiaAnswerToolbar.xaml.cs`
- Modify: `src/Pia.Wpf/Controls/Chat/PiaAnswerToolbar.xaml`
- Modify: `src/Pia.Wpf/Controls/Chat/PiaAssistantMessage.xaml`

- [ ] **Step 1: Add the `PersonaName` DP and recompute the footer**

In `src/Pia.Wpf/Controls/Chat/PiaAnswerToolbar.xaml.cs`:

Add the DP registration next to `StatsProperty`:

```csharp
    public static readonly DependencyProperty PersonaNameProperty =
        DependencyProperty.Register(nameof(PersonaName), typeof(string), typeof(PiaAnswerToolbar),
            new PropertyMetadata(null, OnPersonaNameChanged));
```

Rename the read-only `StatsSummary` member to `FooterSummary` (clearer now that it includes the persona). Replace the `StatsSummaryKey`/`StatsSummaryProperty`/`StatsSummary` members with:

```csharp
    private static readonly DependencyPropertyKey FooterSummaryKey =
        DependencyProperty.RegisterReadOnly(nameof(FooterSummary), typeof(string), typeof(PiaAnswerToolbar),
            new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty FooterSummaryProperty = FooterSummaryKey.DependencyProperty;
```

Add the CLR accessor for `PersonaName` next to `Stats`:

```csharp
    public string? PersonaName
    {
        get => (string?)GetValue(PersonaNameProperty);
        set => SetValue(PersonaNameProperty, value);
    }
```

Replace the `StatsSummary` accessor with:

```csharp
    public string FooterSummary => (string)GetValue(FooterSummaryProperty);
```

Replace `OnStatsChanged` and add `OnPersonaNameChanged`, both delegating to a shared recompute:

```csharp
    private static void OnStatsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PiaAnswerToolbar)d).RecomputeFooter();

    private static void OnPersonaNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PiaAnswerToolbar)d).RecomputeFooter();

    private void RecomputeFooter() =>
        SetValue(FooterSummaryKey, FooterSummaryFormatter.Compose(Stats, PersonaName));
```

- [ ] **Step 2: Update the toolbar XAML binding**

In `src/Pia.Wpf/Controls/Chat/PiaAnswerToolbar.xaml` (~line 234), change the summary `TextBlock` binding from `Text="{Binding StatsSummary, ElementName=Root}"` to `Text="{Binding FooterSummary, ElementName=Root}"`.

- [ ] **Step 3: Pass the persona name into the toolbar**

In `src/Pia.Wpf/Controls/Chat/PiaAssistantMessage.xaml`, on the `<chat:PiaAnswerToolbar ...>` element (~line 58), add an attribute alongside `Stats="{Binding Stats}"`:

```xml
                           PersonaName="{Binding Persona.Name}"
```

(When `Persona` is null the binding resolves to null and the footer is unchanged.)

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: succeeds (no remaining references to `StatsSummary`).

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Controls/Chat/PiaAnswerToolbar.xaml.cs src/Pia.Wpf/Controls/Chat/PiaAnswerToolbar.xaml src/Pia.Wpf/Controls/Chat/PiaAssistantMessage.xaml
git commit -m "feat(personas): show persona name in the answer footer"
```

---

### Task 8: `PiaPersonaAvatar` control + wire into both message views

**Files:**
- Create: `src/Pia.Wpf/Controls/Chat/PiaPersonaAvatar.xaml`
- Create: `src/Pia.Wpf/Controls/Chat/PiaPersonaAvatar.xaml.cs`
- Modify: `src/Pia.Wpf/Views/AssistantView.xaml`
- Modify: `src/Pia.Wpf/Controls/AssistantHistory/PiaAssistantChatInspector.xaml`

- [ ] **Step 1: Create the control XAML**

Create `src/Pia.Wpf/Controls/Chat/PiaPersonaAvatar.xaml` (ports the visual from `PiaAvatarStyle` in `Resources/Theme/PiaStyles.xaml`, wrapping a `PersonaGlyph`):

```xml
<UserControl x:Class="Pia.Controls.Chat.PiaPersonaAvatar"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:controls="clr-namespace:Pia.Controls"
             x:Name="Root"
             Width="28" Height="28"
             Focusable="False">
  <Border CornerRadius="6"
          Background="{DynamicResource BgCanvasBrush}">
    <Border.Effect>
      <DropShadowEffect Color="#0F1729" Opacity="0.18" BlurRadius="6" ShadowDepth="1" />
    </Border.Effect>
    <controls:PersonaGlyph PersonaId="{Binding PersonaId, ElementName=Root}"
                           Emoji="{Binding Emoji, ElementName=Root}"
                           GlyphSize="20"
                           Margin="2" />
  </Border>
</UserControl>
```

- [ ] **Step 2: Create the control code-behind**

Create `src/Pia.Wpf/Controls/Chat/PiaPersonaAvatar.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Chat;

/// <summary>
/// The assistant-chat avatar box (rounded, shadowed) showing a persona's glyph — the Pia app icon
/// for the built-in Pia personas, the persona's emoji otherwise. Used in the live chat and the
/// history inspector.
/// </summary>
public partial class PiaPersonaAvatar : UserControl
{
    public PiaPersonaAvatar() => InitializeComponent();

    public static readonly DependencyProperty PersonaIdProperty = DependencyProperty.Register(
        nameof(PersonaId), typeof(Guid), typeof(PiaPersonaAvatar), new PropertyMetadata(Guid.Empty));

    public static readonly DependencyProperty EmojiProperty = DependencyProperty.Register(
        nameof(Emoji), typeof(string), typeof(PiaPersonaAvatar), new PropertyMetadata(string.Empty));

    public Guid PersonaId
    {
        get => (Guid)GetValue(PersonaIdProperty);
        set => SetValue(PersonaIdProperty, value);
    }

    public string Emoji
    {
        get => (string)GetValue(EmojiProperty);
        set => SetValue(EmojiProperty, value);
    }
}
```

- [ ] **Step 3: Use it in the live chat view**

In `src/Pia.Wpf/Views/AssistantView.xaml` (~lines 202-205), replace the avatar `ContentControl`:

```xml
                <ContentControl Grid.Column="0"
                                Style="{StaticResource PiaAvatarStyle}"
                                Margin="0,2,12,0"
                                VerticalAlignment="Top" />
```

with:

```xml
                <chat:PiaPersonaAvatar Grid.Column="0"
                                       PersonaId="{Binding PersonaGlyphId}"
                                       Emoji="{Binding PersonaGlyphEmoji}"
                                       Margin="0,2,12,0"
                                       VerticalAlignment="Top" />
```

(`AssistantView.xaml` already declares `xmlns:chat="clr-namespace:Pia.Controls.Chat"`.)

- [ ] **Step 4: Use it in the history inspector**

In `src/Pia.Wpf/Controls/AssistantHistory/PiaAssistantChatInspector.xaml` (~lines 127-130), replace the avatar `ContentControl`:

```xml
                  <ContentControl Grid.Column="0"
                                  Style="{StaticResource PiaAvatarStyle}"
                                  Margin="0,2,12,0"
                                  VerticalAlignment="Top"/>
```

with:

```xml
                  <chat:PiaPersonaAvatar Grid.Column="0"
                                         PersonaId="{Binding PersonaGlyphId}"
                                         Emoji="{Binding PersonaGlyphEmoji}"
                                         Margin="0,2,12,0"
                                         VerticalAlignment="Top"/>
```

(The inspector already declares the `chat:` namespace. If a build error reports `PiaPersonaAvatar` not found, confirm the `xmlns:chat="clr-namespace:Pia.Controls.Chat"` declaration is present at the top of the file and add it if missing.)

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: succeeds. (`PiaAvatarStyle` is left in `PiaStyles.xaml` — it may still be referenced elsewhere; do not delete it.)

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Controls/Chat/PiaPersonaAvatar.xaml src/Pia.Wpf/Controls/Chat/PiaPersonaAvatar.xaml.cs src/Pia.Wpf/Views/AssistantView.xaml src/Pia.Wpf/Controls/AssistantHistory/PiaAssistantChatInspector.xaml
git commit -m "feat(personas): persona glyph avatar in chat and history inspector"
```

---

### Task 9: Full verification

**Files:** none (verification only).

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj`
Expected: all tests PASS.

- [ ] **Step 2: Run the app and verify behavior** (use the `verify` or `run` skill)

Run: `dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj`
Verify, in the Assistant view:
1. With a built-in Pia persona selected, send a message → avatar shows the Pia icon; footer reads `<n> Tokens · Pia · Personal · <model>` (or `Pia · Business`).
2. Switch to a custom persona (e.g. Marketing Writer) → its emoji shows as the avatar; footer reads `<n> Tokens · Marketing Writer · <model>`.
3. Switch personas mid-conversation → each reply keeps the persona that produced it.
4. Reload the chat from history (and open it in the history inspector) → avatars and footer names are preserved.
5. Open a chat created **before** this change (if one exists) → falls back to the Pia icon and `<n> Tokens · <model>` with no persona name (no crash).

- [ ] **Step 3: Final commit (if any verification fixes were needed)**

```bash
git add -A
git commit -m "fix(personas): address manual verification findings"
```

---

## Notes for the implementer

- **DRY:** Task 3 deliberately removes three duplicated mapper methods. Don't reintroduce per-VM mapping.
- **No SyncMapper change:** `SyncMapper.ToSyncAssistantChat`/`FromSyncAssistantChat` serialize whole message objects, so the new DTO property round-trips through E2EE automatically (proven by the Task 2 test). Do not hand-copy the field there.
- **Privacy logging (CLAUDE.md):** the persona *name* is a user-named item. It is fine in the DTO, SQLite, and UI, but must not be written to logs via `LogInformation`/etc. The stamp in Task 4 adds no logging.
- **Legacy safety:** every read path treats a missing persona as null → Pia-icon avatar + unchanged footer. No backfill/migration of old rows.
- **`PiaAvatarStyle`:** left intact in `PiaStyles.xaml`; it may still be referenced by other surfaces. Only the two message templates switch to `PiaPersonaAvatar`.
