using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class MemoryToolHandler : IMemoryToolHandler
{
    private readonly IMemoryService _memoryService;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<MemoryToolHandler> _logger;

    public MemoryToolHandler(
        IMemoryService memoryService,
        IEmbeddingService embeddingService,
        ILogger<MemoryToolHandler> logger)
    {
        _memoryService = memoryService;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public IList<AITool> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(CreateObjectSchema, "create_object",
                "Create a new memory object. Use this to store new information about the user. " +
                "Always check for existing related objects before creating new ones to avoid duplication. " +
                "Types: personal_profile (single evolving object for user facts), contact_list (collection of contacts), " +
                "preference (individual user preferences), note (freeform knowledge/summaries)."),

            AIFunctionFactory.Create(UpdateObjectSchema, "update_object",
                "Update an existing memory object by ID using a JSON merge patch. " +
                "Use this to modify or extend existing knowledge rather than creating duplicates. " +
                "For personal_profile, merge new facts into the existing object."),

            AIFunctionFactory.Create(AppendToListSchema, "append_to_list",
                "Add a new entry to a list-type memory object (e.g., add a contact to contact_list). " +
                "The entry will be appended to the first array found in the object's data."),

            AIFunctionFactory.Create(ListMemoriesSchema, "list_memories",
                "List all memory objects showing type, label, and ID. " +
                "Use as a lightweight discovery step before fetching full details with query_memory."),

            AIFunctionFactory.Create(QueryMemorySchema, "query_memory",
                "Search the user's memory store using a natural language query. " +
                "Use this to recall information when the user asks about something personal, " +
                "or when you need to check if a memory already exists before creating a new one. " +
                "Returns matching memory objects with full data, ranked by relevance."),

            AIFunctionFactory.Create(DeleteObjectSchema, "delete_object",
                "Remove a memory object by ID. Use this when the user explicitly asks to forget something."),

            AIFunctionFactory.Create(MergeMemoriesSchema, "merge_memories",
                "Merge two or more memory objects into one. Provide the IDs to merge and the consolidated data. " +
                "The first ID becomes the surviving object (updated with merged data), the rest are deleted. " +
                "Use find_duplicates or list_memories first to identify candidates."),

            AIFunctionFactory.Create(FindDuplicatesSchema, "find_duplicates",
                "Find memory objects that may be duplicates or contain overlapping information. " +
                "Uses semantic similarity to identify candidates for merging. " +
                "Returns groups of similar memories with similarity scores.")
        ];
    }

    public async Task<(object? Result, MemoryToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MemoryToolHandler dispatching: {ToolName}", toolCall.Name);
#if DEBUG
        Debug.WriteLine($"[MemoryToolHandler Args] {toolCall.Name}: {JsonSerializer.Serialize(toolCall.Arguments)}");
#endif
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        var (result, pending) = toolCall.Name switch
        {
            "list_memories" => (await HandleListMemories(args), (MemoryToolCall?)null),
            "query_memory" => (await HandleQueryMemory(args, cancellationToken), (MemoryToolCall?)null),
            "find_duplicates" => (await HandleFindDuplicates(args), (MemoryToolCall?)null),
            "create_object" => ((object?)null, await PrepareCreateObject(args)),
            "update_object" => ((object?)null, await PrepareUpdateObject(args)),
            "append_to_list" => ((object?)null, await PrepareAppendToList(args)),
            "delete_object" => ((object?)null, await PrepareDeleteObject(args)),
            "merge_memories" => ((object?)null, await PrepareMergeMemories(args)),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", (MemoryToolCall?)null)
        };

        // Error cases (invalid ID, not found) produce a pending action with no TargetObjectId.
        // Return them as immediate results so no action card is shown to the user.
        if (pending is not null && pending.TargetObjectId is null && toolCall.Name is not "create_object")
        {
            _logger.LogWarning("MemoryToolHandler {ToolName} returning error: {Description}", toolCall.Name, pending.Description);
            return (await pending.Execute(), null);
        }

        _logger.LogDebug("MemoryToolHandler {ToolName} result: hasResult={HasResult}, hasPending={HasPending}",
            toolCall.Name, result is not null, pending is not null);
        return (result, pending);
    }

    public async Task<object?> ExecutePendingActionAsync(MemoryToolCall pendingAction)
    {
        _logger.LogDebug("Executing memory action: {ToolName}, targetId={TargetObjectId}",
            pendingAction.ToolName, pendingAction.TargetObjectId);
        try
        {
            var result = await pendingAction.Execute();
            _logger.LogInformation("Memory action completed: {ToolName}", pendingAction.ToolName);

            // Generate embedding for the affected object if applicable
            if (pendingAction.TargetObjectId.HasValue && _embeddingService.IsModelAvailable)
            {
                try
                {
                    var obj = await _memoryService.GetObjectAsync(pendingAction.TargetObjectId.Value);
                    if (obj is not null)
                    {
                        var textToEmbed = $"{obj.Label} {obj.Data}";
                        var embedding = await _embeddingService.GenerateEmbeddingAsync(textToEmbed);
                        await _memoryService.UpdateEmbeddingAsync(obj.Id, _embeddingService.FloatsToBytes(embedding));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate embedding for memory object {Id}", pendingAction.TargetObjectId);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute memory tool action: {ToolName}", pendingAction.ToolName);
            return $"Error executing {pendingAction.ToolName}: {ex.Message}";
        }
    }

    private async Task<object?> HandleListMemories(IDictionary<string, object?> args)
    {
        var type = GetStringArg(args, "type");
        var typeFilter = string.IsNullOrWhiteSpace(type) ? null : type;

        var summaries = await _memoryService.GetMemorySummariesAsync(typeFilter);

        if (summaries.Count == 0)
            return typeFilter is not null
                ? $"No memory objects found with type '{typeFilter}'."
                : "No memory objects stored yet.";

        var sb = new StringBuilder();
        sb.AppendLine($"{summaries.Count} memory object(s):");

        string? currentType = null;
        foreach (var summary in summaries)
        {
            if (summary.Type != currentType)
            {
                currentType = summary.Type;
                sb.AppendLine();
                sb.AppendLine($"{MemoryObjectTypes.GetDisplayName(currentType)}:");
            }
            sb.AppendLine($"  - {summary.Label} (ID: {summary.Id})");
        }

        return sb.ToString();
    }

    private async Task<object?> HandleQueryMemory(
        IDictionary<string, object?> args,
        CancellationToken cancellationToken)
    {
        var query = GetStringArg(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query parameter is required";

        float[]? queryEmbedding = null;

        // Auto-download embedding model on first use
        if (!_embeddingService.IsModelAvailable)
        {
            try
            {
                _logger.LogInformation("Embedding model not found, downloading automatically...");
                await _embeddingService.DownloadModelAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download embedding model, continuing with text search only");
            }
        }

        if (_embeddingService.IsModelAvailable)
        {
            try
            {
                queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(query, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate query embedding, falling back to text search");
            }
        }

        var results = await _memoryService.HybridSearchAsync(query, queryEmbedding);

        // Touch access time for retrieved objects
        foreach (var result in results)
        {
            await _memoryService.TouchAccessTimeAsync(result.Id);
        }

        // Backfill embeddings for memories that don't have them yet
        if (_embeddingService.IsModelAvailable)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await BackfillEmbeddingsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to backfill embeddings");
                }
            }, CancellationToken.None);
        }

        if (results.Count == 0)
            return "No relevant memories found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} relevant memories:");

        foreach (var result in results)
        {
            sb.AppendLine($"\n[ID: {result.Id}] [{MemoryObjectTypes.GetDisplayName(result.Type)}] {result.Label}:");
            sb.AppendLine(result.Data);
        }

        return sb.ToString();
    }

    private Task<MemoryToolCall> PrepareCreateObject(IDictionary<string, object?> args)
    {
        var type = GetStringArg(args, "type");
        var label = GetStringArg(args, "label");
        var data = GetStringArg(args, "data");

        return Task.FromResult(new MemoryToolCall(
            ToolName: "create_object",
            Description: $"Create new {MemoryObjectTypes.GetDisplayName(type)}: {label}",
            OldValue: null,
            NewValue: JsonHelper.FormatJson(data),
            TargetObjectId: null,
            Execute: async () =>
            {
                var created = await _memoryService.CreateObjectAsync(type, label, data);
                return $"Memory object created successfully with ID: {created.Id}";
            }));
    }

    private async Task<MemoryToolCall> PrepareUpdateObject(IDictionary<string, object?> args)
    {
        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            _logger.LogWarning("update_object called with invalid ID: '{IdValue}'", idStr);
            return new MemoryToolCall("update_object", "Invalid ID format", null, null, null,
                () => Task.FromResult<object?>($"Error: Invalid object ID format. You provided '{idStr}' which is not a valid GUID. Use list_memories or query_memory to get valid IDs."));
        }

        var mergePatch = GetStringArg(args, "data");

        var existing = await _memoryService.GetObjectAsync(id);
        if (existing is null)
            return new MemoryToolCall("update_object", "Object not found", null, null, null,
                () => Task.FromResult<object?>($"Error: Memory object {id} not found"));

        return new MemoryToolCall(
            ToolName: "update_object",
            Description: $"Update {MemoryObjectTypes.GetDisplayName(existing.Type)}: {existing.Label}",
            OldValue: JsonHelper.FormatJson(existing.Data),
            NewValue: FormatMergedJson(existing.Data, mergePatch),
            TargetObjectId: id,
            Execute: async () =>
            {
                await _memoryService.UpdateObjectAsync(id, mergePatch);
                return $"Memory object {id} updated successfully.";
            });
    }

    private async Task<MemoryToolCall> PrepareAppendToList(IDictionary<string, object?> args)
    {
        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            _logger.LogWarning("append_to_list called with invalid ID: '{IdValue}'", idStr);
            return new MemoryToolCall("append_to_list", "Invalid ID format", null, null, null,
                () => Task.FromResult<object?>($"Error: Invalid object ID format. You provided '{idStr}' which is not a valid GUID. Use list_memories or query_memory to get valid IDs."));
        }

        var entry = GetStringArg(args, "entry");

        var existing = await _memoryService.GetObjectAsync(id);
        if (existing is null)
            return new MemoryToolCall("append_to_list", "Object not found", null, null, null,
                () => Task.FromResult<object?>($"Error: Memory object {id} not found"));

        return new MemoryToolCall(
            ToolName: "append_to_list",
            Description: $"Add entry to {MemoryObjectTypes.GetDisplayName(existing.Type)}: {existing.Label}",
            OldValue: JsonHelper.FormatJson(existing.Data),
            NewValue: $"+ {JsonHelper.FormatJson(entry)}",
            TargetObjectId: id,
            Execute: async () =>
            {
                await _memoryService.AppendToListAsync(id, entry);
                return $"Entry appended to memory object {id} successfully.";
            });
    }

    private async Task<MemoryToolCall> PrepareDeleteObject(IDictionary<string, object?> args)
    {
        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            _logger.LogWarning("delete_object called with invalid ID: '{IdValue}'", idStr);
            return new MemoryToolCall("delete_object", "Invalid ID format", null, null, null,
                () => Task.FromResult<object?>($"Error: Invalid object ID format. You provided '{idStr}' which is not a valid GUID. Use list_memories or query_memory to get valid IDs."));
        }

        var existing = await _memoryService.GetObjectAsync(id);
        if (existing is null)
            return new MemoryToolCall("delete_object", "Object not found", null, null, null,
                () => Task.FromResult<object?>($"Error: Memory object {id} not found"));

        return new MemoryToolCall(
            ToolName: "delete_object",
            Description: $"Delete {MemoryObjectTypes.GetDisplayName(existing.Type)}: {existing.Label}",
            OldValue: JsonHelper.FormatJson(existing.Data),
            NewValue: null,
            TargetObjectId: id,
            Execute: async () =>
            {
                await _memoryService.DeleteObjectAsync(id);
                return $"Memory object {id} deleted successfully.";
            });
    }

    private async Task<object?> HandleFindDuplicates(IDictionary<string, object?> args)
    {
        var thresholdStr = GetStringArg(args, "threshold");
        var threshold = 0.7f;
        if (!string.IsNullOrWhiteSpace(thresholdStr) && float.TryParse(thresholdStr, out var parsed))
            threshold = Math.Clamp(parsed, 0f, 1f);

        var allMemories = await _memoryService.GetAllObjectsAsync();
        var withEmbeddings = allMemories
            .Where(m => m.Embedding is not null)
            .ToList();

        if (withEmbeddings.Count < 2)
            return "Not enough memories with embeddings to find duplicates. Try querying memories first to trigger embedding generation.";

        // Compute pairwise cosine similarity
        var groups = new List<List<(MemoryObject Memory, float Score)>>();
        var assigned = new HashSet<Guid>();

        for (int i = 0; i < withEmbeddings.Count; i++)
        {
            if (assigned.Contains(withEmbeddings[i].Id)) continue;

            var embA = _embeddingService.BytesToFloats(withEmbeddings[i].Embedding!);
            var group = new List<(MemoryObject Memory, float Score)>();

            for (int j = i + 1; j < withEmbeddings.Count; j++)
            {
                if (assigned.Contains(withEmbeddings[j].Id)) continue;

                var embB = _embeddingService.BytesToFloats(withEmbeddings[j].Embedding!);
                var similarity = CosineSimilarity(embA, embB);

                if (similarity >= threshold)
                    group.Add((withEmbeddings[j], similarity));
            }

            if (group.Count > 0)
            {
                group.Insert(0, (withEmbeddings[i], 1.0f));
                foreach (var item in group)
                    assigned.Add(item.Memory.Id);
                groups.Add(group);
            }
        }

        if (groups.Count == 0)
            return $"No duplicate candidates found above similarity threshold {threshold:F2}.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {groups.Count} group(s) of similar memories (threshold: {threshold:F2}):");

        for (int g = 0; g < groups.Count; g++)
        {
            sb.AppendLine();
            sb.AppendLine($"Group {g + 1}:");
            foreach (var (memory, score) in groups[g])
            {
                var scoreText = score >= 1.0f ? "anchor" : $"similarity: {score:F2}";
                sb.AppendLine($"  - [{MemoryObjectTypes.GetDisplayName(memory.Type)}] {memory.Label} (ID: {memory.Id}) [{scoreText}]");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Use merge_memories to combine any of these groups.");

        return sb.ToString();
    }

    private async Task<MemoryToolCall> PrepareMergeMemories(IDictionary<string, object?> args)
    {
        var idsStr = GetStringArg(args, "ids");
        var label = GetStringArg(args, "label");
        var data = GetStringArg(args, "data");

        var idStrings = idsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (idStrings.Length < 2)
        {
            return new MemoryToolCall("merge_memories", "At least 2 IDs required", null, null, null,
                () => Task.FromResult<object?>("Error: merge_memories requires at least 2 memory object IDs."));
        }

        var ids = new List<Guid>();
        foreach (var idStr in idStrings)
        {
            if (!Guid.TryParse(idStr, out var id))
            {
                return new MemoryToolCall("merge_memories", "Invalid ID format", null, null, null,
                    () => Task.FromResult<object?>($"Error: '{idStr}' is not a valid GUID. Use list_memories or query_memory to get valid IDs."));
            }
            ids.Add(id);
        }

        var objects = new List<MemoryObject>();
        foreach (var id in ids)
        {
            var obj = await _memoryService.GetObjectAsync(id);
            if (obj is null)
            {
                return new MemoryToolCall("merge_memories", "Object not found", null, null, null,
                    () => Task.FromResult<object?>($"Error: Memory object {id} not found."));
            }
            objects.Add(obj);
        }

        var survivor = objects[0];
        var toDelete = objects.Skip(1).ToList();

        // Build old value showing all source objects
        var oldSb = new StringBuilder();
        foreach (var obj in objects)
        {
            oldSb.AppendLine($"[{MemoryObjectTypes.GetDisplayName(obj.Type)}] {obj.Label}:");
            oldSb.AppendLine(JsonHelper.FormatJson(obj.Data));
            oldSb.AppendLine();
        }

        return new MemoryToolCall(
            ToolName: "merge_memories",
            Description: $"Merge {objects.Count} memories into: {label}",
            OldValue: oldSb.ToString().TrimEnd(),
            NewValue: JsonHelper.FormatJson(data),
            TargetObjectId: survivor.Id,
            Execute: async () =>
            {
                await _memoryService.UpdateObjectDataAsync(survivor.Id, label, data);
                foreach (var obj in toDelete)
                    await _memoryService.DeleteObjectAsync(obj.Id);
                return $"Merged {objects.Count} memories into '{label}' (ID: {survivor.Id}). {toDelete.Count} duplicate(s) deleted.";
            });
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator == 0 ? 0f : dot / denominator;
    }

    [Description("Create a new memory object")]
    private static string CreateObjectSchema(
        [Description("Type of memory object: personal_profile, contact_list, preference, note")] string type,
        [Description("Human-readable label for this memory")] string label,
        [Description("JSON data for the memory object")] string data) => "";

    [Description("Update an existing memory object by ID using JSON merge patch")]
    private static string UpdateObjectSchema(
        [Description("The ID of the memory object to update")] string id,
        [Description("JSON merge patch to apply to the existing data")] string data) => "";

    [Description("Append a new entry to a list-type memory object")]
    private static string AppendToListSchema(
        [Description("The ID of the memory object to append to")] string id,
        [Description("JSON entry to append to the list")] string entry) => "";

    [Description("List all memory objects with their type, label, and ID")]
    private static string ListMemoriesSchema(
        [Description("Optional type filter: personal_profile, contact_list, preference, note")] string? type = null) => "";

    [Description("Search the user's memory store using a natural language query")]
    private static string QueryMemorySchema(
        [Description("Natural language query to search for in memories")] string query) => "";

    [Description("Delete a memory object by ID")]
    private static string DeleteObjectSchema(
        [Description("The ID of the memory object to delete")] string id) => "";

    [Description("Merge two or more memory objects into one consolidated object")]
    private static string MergeMemoriesSchema(
        [Description("Comma-separated IDs of memory objects to merge. First ID becomes the surviving object.")] string ids,
        [Description("Label for the merged memory object")] string label,
        [Description("Consolidated JSON data combining information from all source objects")] string data) => "";

    [Description("Find memory objects that may be duplicates or contain overlapping information")]
    private static string FindDuplicatesSchema(
        [Description("Minimum similarity threshold 0.0-1.0 (default 0.7)")] float? threshold = null) => "";

    private async Task BackfillEmbeddingsAsync()
    {
        var forceRegenerate = await CheckModelVersionChangedAsync();

        var allMemories = await _memoryService.GetAllObjectsAsync();
        foreach (var memory in allMemories)
        {
            if (!forceRegenerate && memory.Embedding is not null) continue;

            try
            {
                var textToEmbed = $"{memory.Label} {memory.Data}";
                var embedding = await _embeddingService.GenerateEmbeddingAsync(textToEmbed);
                await _memoryService.UpdateEmbeddingAsync(memory.Id, _embeddingService.FloatsToBytes(embedding));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to backfill embedding for memory {Id}", memory.Id);
            }
        }
    }

    private static async Task<bool> CheckModelVersionChangedAsync()
    {
        const string currentModel = "paraphrase-multilingual-MiniLM-L12-v2";
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var markerPath = Path.Combine(localAppData, "Pia", "Models", "Embeddings", "model_version.txt");

        try
        {
            if (File.Exists(markerPath))
            {
                var storedModel = await File.ReadAllTextAsync(markerPath);
                if (storedModel.Trim() == currentModel)
                    return false;
            }

            await File.WriteAllTextAsync(markerPath, currentModel);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetStringArg(IDictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var value))
        {
            if (value is JsonElement element)
                return element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText();
            return value?.ToString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string FormatMergedJson(string existingData, string mergePatch)
    {
        try
        {
            var existingNode = JsonNode.Parse(existingData)?.AsObject() ?? new JsonObject();
            var patchNode = JsonNode.Parse(mergePatch)?.AsObject() ?? new JsonObject();

            foreach (var property in patchNode)
            {
                if (property.Value is null)
                {
                    existingNode.Remove(property.Key);
                }
                else
                {
                    existingNode[property.Key] = property.Value.DeepClone();
                }
            }

            return existingNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return mergePatch;
        }
    }
}
