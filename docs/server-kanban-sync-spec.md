# Server Spec: Kanban Column Sync

## 1. New DB Table: `KanbanColumns`

| Column           | Type    | Constraints                          |
|------------------|---------|--------------------------------------|
| Id               | TEXT    | PRIMARY KEY                          |
| UserId           | TEXT    | NOT NULL, FK to Users(Id)            |
| Name             | TEXT    | NOT NULL                             |
| SortOrder        | INTEGER | NOT NULL DEFAULT 0                   |
| IsDefaultView    | INTEGER | NOT NULL DEFAULT 0                   |
| IsClosedColumn   | INTEGER | NOT NULL DEFAULT 0                   |
| CreatedAt        | TEXT    | NOT NULL                             |
| UpdatedAt        | TEXT    | NOT NULL                             |
| EncryptedPayload | TEXT    |                                      |
| WrappedDek       | TEXT    |                                      |

**Indexes:**
- Index on `UserId`
- Composite unique constraint on `(UserId, Id)`

## 2. SyncTodo Table Change

Add a nullable `ColumnId` column (TEXT) to the `SyncTodos` table. This is backward compatible -- existing rows will have NULL. No foreign key constraint is required; the client enforces referential integrity.

## 3. Push Endpoint (`POST /api/sync/push`)

The `SyncPushRequest` body now includes a `KanbanColumns` field of type `SyncEntityChanges<SyncKanbanColumn>`:

```json
{
  "KanbanColumns": {
    "Upserted": [ { "Id": "...", "Name": "...", ... } ],
    "Deleted": [ "guid-1", "guid-2" ]
  }
}
```

Processing:
- **Upserts**: `INSERT OR REPLACE` into `KanbanColumns` scoped by `UserId` + `Id`.
- **Deletes**: `DELETE FROM KanbanColumns WHERE UserId = @uid AND Id = @id`.

## 4. Pull Endpoint (`GET /api/sync/pull?since={timestamp}`)

The `SyncPullResponse` now includes a `KanbanColumns` field of type `SyncEntityChanges<SyncKanbanColumn>`.

- **Upserted**: Return all `KanbanColumns` for the authenticated user where `UpdatedAt >= @since`.
- **Deleted**: Return IDs of kanban columns deleted since `@since` (use soft-delete tracking or a `DeletedEntities` table, consistent with existing entity deletion handling).

## 5. SyncTodo Changes

The `SyncTodo` DTO now includes a nullable `ColumnId` (Guid?) field. Store and return this value as-is. No server-side resolution or validation is needed.

## 6. E2EE Handling

The server treats `EncryptedPayload` and `WrappedDek` as opaque Base64 strings:

- When `IsE2EEEncrypted` is true on the push request, `Name` will be null and the encrypted data will be in `EncryptedPayload` / `WrappedDek`.
- The server must store and return these fields without attempting to parse or validate the plaintext fields.
- Both fields are nullable; they are non-null only when E2EE is active.

## 7. No Server-Side Validation

The following constraints are enforced **client-side only** -- the server should not reject requests based on these rules:

- Columns list must not be empty (at least one column must exist).
- Only one column may have `IsDefaultView = 1`.
- Deletion of columns with assigned todos is prevented by the client.

The server should accept any valid upsert/delete operation without business-rule validation.
