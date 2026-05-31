# 01 — Pia Server implementation plan

**Repo:** `/Users/marcoaltmann/Documents/GitHub/Pia` · **Stack:** C# / ASP.NET Core 10 / EF Core /
PostgreSQL (or SQLite via `Database:Provider`). **Entry:** `src/Pia.Server/Program.cs`.

The server is **entity-agnostic about persona semantics** — it stores and returns persona rows
(plaintext fields or an opaque E2EE blob) scoped per user. It needs no knowledge of built-ins,
`ToolScope`, prompts, or composition. Built-in personas are never pushed, so they never reach the
server.

## 0. Prerequisite — the shared DTO

`SyncPersona` and the `SyncPushRequest`/`SyncPullResponse` additions live in `Pia.Shared`, which the
server consumes through the **submodule** `lib/Pia.Wpf` (currently pinned at `v1.3.203`,
`ProjectReference …\lib\Pia.Wpf\src\Pia.Shared\Pia.Shared.csproj`).

➡ **Do the Pia.Wpf `Pia.Shared` changes first (see `02-pia-wpf.md` §1), tag a release, then bump
the submodule:**

```bash
cd /Users/marcoaltmann/Documents/GitHub/Pia/lib/Pia.Wpf
git fetch && git checkout <new-tag-or-commit>
cd /Users/marcoaltmann/Documents/GitHub/Pia
git add lib/Pia.Wpf
```

Until the submodule is bumped, the server won't see `SyncPersona`.

## 1. Files to create / modify

| Action | File | What |
|--------|------|------|
| Create | `src/Pia.Server/Models/ServerPersona.cs` | EF entity (per-user, soft-delete, sync columns). |
| Modify | `src/Pia.Server/Models/PiaUser.cs` | Add `List<ServerPersona> Personas` navigation. |
| Modify | `src/Pia.Server/Data/PiaDbContext.cs` | Add `DbSet<ServerPersona>` + `OnModelCreating` config. |
| Create | `src/Pia.Server/Migrations/<ts>_AddPersonas.cs` | `dotnet ef migrations add AddPersonas`. |
| Modify | `src/Pia.Server/Sync/SyncService.cs` | Pull projection + push upsert/delete/conflict for personas. |
| Modify | `src/Pia.Server/Sync/SyncEndpoints.cs` | Validation limits, ETag contribution, debug/reset endpoints. |
| Modify | `src/Pia.Server/Services/QuotaService.cs` | `newPersonas` param + `PersonaCount` usage + cap. |

## 2. EF entity — `ServerPersona.cs`

Mirror `ServerTemplate` (composite PK `(Id, UserId)`, soft-delete via `DeletedAt`, `SyncedAt`
cursor, opaque E2EE columns). Use `UpdatedAt` as the modified-timestamp (matches the DTO).

```csharp
namespace Pia.Server.Models;

public class ServerPersona
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    // Plaintext content (null when E2EE)
    public string? Name { get; set; }
    public string? Tagline { get; set; }
    public string? SystemPrompt { get; set; }
    public string? Guardrails { get; set; }
    public string? Expertise { get; set; }          // JSON-serialized string[] (or text)

    // Structural / config (always plaintext)
    public string? Archetype { get; set; }
    public string? Emoji { get; set; }
    public string? AccentColor { get; set; }
    public int ToolScope { get; set; }
    public Guid? PreferredProviderId { get; set; }
    public int? ReasoningEffort { get; set; }
    public int SchemaVersion { get; set; }

    // Timestamps / sync
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }         // soft-delete tombstone
    public DateTime SyncedAt { get; set; }           // pull cursor

    // E2EE (opaque)
    public string? EncryptedPayload { get; set; }
    public string? WrappedDek { get; set; }

    public PiaUser User { get; set; } = null!;
}
```

> `Expertise` is a `string[]` on the wire but EF stores it simplest as a JSON string column
> (the server never queries it). Serialize on push, deserialize on pull projection.

## 3. DbContext

In `PiaDbContext.cs`, alongside the other `DbSet`s:

```csharp
public DbSet<ServerPersona> Personas => Set<ServerPersona>();
```

In `OnModelCreating` (mirror the `ServerTemplate` block at ~lines 101–109):

```csharp
modelBuilder.Entity<ServerPersona>(entity =>
{
    entity.ToTable("personas");
    entity.HasKey(e => new { e.Id, e.UserId });
    entity.Property(e => e.Name).HasMaxLength(255);
    entity.Property(e => e.Tagline).HasMaxLength(280);
    entity.HasOne(e => e.User).WithMany(u => u.Personas)
          .HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
    entity.HasIndex(e => new { e.UserId, e.SyncedAt }); // pull query
});
```

In `PiaUser.cs`: `public List<ServerPersona> Personas { get; set; } = [];`

## 4. Migration

```bash
cd /Users/marcoaltmann/Documents/GitHub/Pia/src/Pia.Server
dotnet ef migrations add AddPersonas
dotnet ef database update   # or rely on startup auto-migrate
```

Verify the generated table has the composite PK, the FK with cascade delete, the
`(UserId, SyncedAt)` index, and `timestamp with time zone` columns for the dates. The migration is
**purely additive** → safe for existing databases and older clients.

## 5. Sync — Pull (`SyncService.PullAsync`)

Mirror the Template pull (~lines 75–108). Add after the existing entity pulls:

```csharp
var changedPersonas = await _context.Personas
    .Where(p => p.UserId == userId && p.SyncedAt > since)
    .ToListAsync();

response.Personas.Upserted = changedPersonas
    .Where(p => p.DeletedAt == null)
    .Select(p =>
    {
        var sp = new SyncPersona
        {
            Id = p.Id,
            Archetype = p.Archetype,
            Emoji = p.Emoji,
            AccentColor = p.AccentColor,
            ToolScope = p.ToolScope,
            PreferredProviderId = p.PreferredProviderId,
            ReasoningEffort = p.ReasoningEffort,
            SchemaVersion = p.SchemaVersion,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
        };
        if (isE2EE)
        {
            sp.EncryptedPayload = p.EncryptedPayload;
            sp.WrappedDek = p.WrappedDek;
        }
        else
        {
            sp.Name = p.Name;
            sp.Tagline = p.Tagline;
            sp.SystemPrompt = p.SystemPrompt;
            sp.Guardrails = p.Guardrails;
            sp.Expertise = DeserializeExpertise(p.Expertise);
        }
        return sp;
    }).ToList();

response.Personas.Deleted = changedPersonas
    .Where(p => p.DeletedAt != null)
    .Select(p => p.Id).ToList();
```

Note structural fields (`Archetype`, `Emoji`, …, `SchemaVersion`) are returned regardless of E2EE,
per contract §3.

## 6. Sync — Push (`SyncService.PushAsync`)

Mirror the Template push (~lines 728–827): pre-load existing IDs, quota-check new inserts,
upsert with last-write-wins conflict detection, soft-delete. Sketch:

```csharp
// Pre-load (alongside other entities)
var existingPersonaIds = (await _context.Personas
    .Where(p => p.UserId == userId).Select(p => p.Id).ToListAsync()).ToHashSet();
var newPersonaInserts = request.Personas.Upserted.Count(p => !existingPersonaIds.Contains(p.Id));
// → pass newPersonas: newPersonaInserts into _quotaService.CheckQuotaAsync(...)

var personaIdsToUpdate = request.Personas.Upserted
    .Where(p => existingPersonaIds.Contains(p.Id)).Select(p => p.Id).ToList();
var existingPersonas = personaIdsToUpdate.Count > 0
    ? await _context.Personas.Where(p => p.UserId == userId && personaIdsToUpdate.Contains(p.Id))
        .ToDictionaryAsync(p => p.Id)
    : new();

foreach (var persona in request.Personas.Upserted)
{
    if (existingPersonas.TryGetValue(persona.Id, out var existing))
    {
        if (existing.UpdatedAt > request.LastSyncTimestamp) // conflict
        {
            var conflict = _conflictResolver.Resolve("personas", persona.Id,
                existing.UpdatedAt, persona.UpdatedAt, /* serverVersion */ ProjectToDto(existing, isE2EE));
            response.Conflicts.Add(conflict);
            if (conflict.Resolution == "server_wins") continue;
        }
        ApplyPersona(existing, persona, request.IsE2EEEncrypted);
        existing.UpdatedAt = persona.UpdatedAt;
        existing.DeletedAt = null;          // undelete on re-push
        existing.SyncedAt  = syncedAt;
    }
    else
    {
        var sp = new ServerPersona { Id = persona.Id, UserId = userId,
            CreatedAt = persona.CreatedAt, UpdatedAt = persona.UpdatedAt, SyncedAt = syncedAt };
        ApplyPersona(sp, persona, request.IsE2EEEncrypted);
        _context.Personas.Add(sp);
    }
}

if (request.Personas.Deleted.Count > 0)
{
    var toDelete = await _context.Personas
        .Where(p => p.UserId == userId && request.Personas.Deleted.Contains(p.Id)).ToListAsync();
    foreach (var p in toDelete) { p.DeletedAt = DateTime.UtcNow; p.SyncedAt = syncedAt; }
}
```

`ApplyPersona` sets structural fields always; under E2EE it nulls the textual fields and stores
`EncryptedPayload`/`WrappedDek`, otherwise stores the textual fields and nulls the blob (mirror the
Template merge at ~lines 753–770). Serialize `Expertise` to JSON for the column.

## 7. Validation (`SyncEndpoints.cs`)

Add limits (mirror the template constants ~lines 13–21) and validate per persona on push:

- E2EE off: `Name` required ≤ 255; `Tagline` ≤ 280; `SystemPrompt` required ≤ 20000;
  `Guardrails` ≤ 5000; `ToolScope` ∈ {0,1,2}; `Expertise` ≤ 16 items.
- E2EE on: reuse `ValidateE2EEEntity(errors, "Persona", id, EncryptedPayload, WrappedDek)`
  (presence + ≤ 1.4 MB).

Add the persona counts to the **ETag** content string (~lines 54–72) so pulls invalidate correctly:

```csharp
$"-{response.Personas.Upserted.Count}-{response.Personas.Deleted.Count}-{MaxTicks(response.Personas.Upserted, p => p.UpdatedAt)}"
```

Add personas to the **debug-state** endpoint (~lines 217–255) and the **reset** endpoint
(`ExecuteDeleteAsync` on `db.Personas`) for parity with other entities.

## 8. Quota (`QuotaService.cs`)

- Add `int newPersonas = 0` parameter to `CheckQuotaAsync`; check
  `usage.PersonaCount + newPersonas > settings.Personas`.
- Add `PersonaCount` to `StorageUsage` and count `Personas.Count(p => p.UserId == userId && p.DeletedAt == null)` in `GetUsageAsync`.
- Add a `Personas` quota to the group-settings quota model with a sensible default (e.g. **50**).

## 9. Auth / scoping (no change needed, just conform)

Personas are scoped by the JWT `sub` claim like every other entity; the composite `(Id, UserId)`
PK + cascade delete handles per-user isolation and account deletion. Sync endpoints already sit
behind the `"sync"` rate-limit policy and the `Sync` license feature — personas inherit both.

## 10. Tests

- Push insert → pull returns it; push update with newer `UpdatedAt` wins; older `UpdatedAt` →
  `server_wins` conflict; delete → tombstone returned on pull (`Deleted` list).
- E2EE round-trip: blob stored & returned, textual columns null.
- Quota: inserting beyond the cap returns the quota violation (409).
- ETag: identical pull state → `304 Not Modified`.
