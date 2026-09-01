using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared;

namespace Pia.Services;

/// <summary>
/// Builds the persona-driven system prompt and resolves the tool set for an
/// assistant turn. The prompt-shaping logic (identity block, output format,
/// tool-selection tree, @-command hints, privacy-token and web-search sections)
/// lives here as a single responsibility, extracted from AssistantViewModel.
/// The pure helpers exercised by unit tests are exposed as <c>public static</c>.
/// </summary>
public sealed class AssistantPromptComposer : IAssistantPromptComposer
{
    private readonly ILocalizationService _localizationService;
    private readonly IPluginService _pluginService;

    public AssistantPromptComposer(ILocalizationService localizationService, IPluginService pluginService)
    {
        _localizationService = localizationService;
        _pluginService = pluginService;
    }

    public AssistantTurnSetup PrepareTurn(Persona persona, AiProvider provider, IReadOnlyList<AtCommand> atCommands, bool tokenizationEnabled, bool suggestAgentModeEligible = false)
    {
        // Tool gating (contract §5) — see ShouldUseTools.
        var supportsTools = ShouldUseTools(provider.SupportsToolCalling, persona.ToolScope);
        var webSearchActive = IsWebSearchActive(provider);

        string fullSystemPrompt;
        IList<AITool>? tools;

        if (supportsTools)
        {
            var hasAtCommands = atCommands.Count > 0;
            fullSystemPrompt = BuildSystemPrompt(persona, tokenizationEnabled, skipToolSelectionTree: hasAtCommands, webSearchActive: webSearchActive)
                + BuildAtCommandHint(atCommands);

            var allTools = _pluginService.GetAllTools();
            if (hasAtCommands)
            {
                // @-command turns narrow the toolset to the tagged domain — leave suggest_agent_mode out
                // so those turns stay byte-stable (G1).
                var allowed = GetAllowedToolNames(atCommands);
                tools = [.. allTools.Where(t => allowed.Contains(t.Name))];
            }
            else
            {
                var list = new List<AITool>(allTools);
                // R7: inject the suggestion tool only for an eligible interactive Chat turn on a tool-capable
                // provider. supportsTools here already carries ToolScope!=None ∧ provider.SupportsToolCalling.
                if (suggestAgentModeEligible)
                    list.Add(BuildSuggestAgentModeTool());
                tools = list;
            }
        }
        else
        {
            fullSystemPrompt = BuildSystemPromptNoTools(persona, webSearchActive: webSearchActive);
            tools = null;
        }

        // Carried on the setup rather than re-resolved downstream: RunExchangeAsync and the step path both
        // already receive the setup, so this is the one place the persona is known on every turn path.
        return new AssistantTurnSetup(fullSystemPrompt, tools, supportsTools, webSearchActive, persona.Id, persona.ModelType);
    }

    private static string GetLanguageName(TargetLanguage language) => language switch
    {
        TargetLanguage.DE => "German",
        TargetLanguage.FR => "French",
        _ => "English"
    };

    private string BuildLanguageInstruction()
    {
        var languageName = GetLanguageName(_localizationService.CurrentLanguage);
        return $"Always respond to the user in '{languageName}' unless the user asks you to switch.";
    }

    // Tool gating (contract §5): tools are used only when the provider supports them AND the persona
    // permits them. ToolScope.None → no tools; ReadOnly is treated as Full in v1.
    public static bool ShouldUseTools(bool providerSupportsToolCalling, PersonaToolScope scope) =>
        providerSupportsToolCalling && scope != PersonaToolScope.None;

    // Shape lives in Pia.Shared because the server's admin preview renders the same block; CurrentCulture
    // so the weekday reads in the end user's language.
    public static string BuildIdentityBlock(Persona activePersona) =>
        PersonaPromptShape.BuildIdentityBlock(
            activePersona.SystemPrompt,
            activePersona.Guardrails,
            DateTime.Now,
            CultureInfo.CurrentCulture);

    // Output-format guidance the substrate falls back to when the active persona doesn't define its
    // own (personas created/synced before the field existed, or left blank). Kept byte-identical to
    // BuiltInPersonas.PiaOutputFormat — pinned by a test — so the Pia personas render the historical
    // formatting block even via the fallback path.
    public const string DefaultOutputFormat =
        """
        - Keep replies short. Default to 1–3 sentences; expand only when the user explicitly asks for detail, steps, or code.
        - Write plain prose. Do not use headings or italics. Avoid bold; reserve **bold** only for safety-critical warnings (e.g. confirming a destructive action).
        - Use bullet lists only for 3+ discrete items. Use code blocks only for code, commands, or file paths.
        - Do not restate the user's question and do not summarize what you just said at the end of a reply.
        - Use the plain verb (is, has, used, wrote) over a formal synonym or "serves as", "represents", "features".
        - State facts directly: no editorial tails like ", highlighting its importance".
        - Name the source or drop the claim; never "experts say" or "studies suggest".
        - Cut any sentence that would still be true if the subject were something else.
        """;

    // Tool-interaction safety rule appended in the tools path regardless of the active persona's
    // output format, so a custom/creative persona can't accidentally drop it.
    private const string DeclinedActionRule =
        "- When a user declines a proposed action, do NOT retry the same operation. Instead, acknowledge the decline and ask the user what they would like to do differently or if they want to adjust the details.";

    // The body of the "## Output Format" section: the persona's own guidance, or the substrate default.
    public static string ResolveOutputFormat(Persona activePersona) =>
        string.IsNullOrWhiteSpace(activePersona.OutputFormat)
            ? DefaultOutputFormat
            : activePersona.OutputFormat.Trim();

    private string BuildSystemPrompt(Persona activePersona, bool tokenizationEnabled, bool skipToolSelectionTree = false, bool webSearchActive = false)
    {
        var pluginPrompts = _pluginService.GetCombinedSystemPromptAdditions();
        var pluginSection = string.IsNullOrWhiteSpace(pluginPrompts)
            ? string.Empty
            : $"## Plugins\n\n{pluginPrompts}\n\n";
        var tokenSection = tokenizationEnabled
            ? "\n## Privacy Tokens\n\nWhen memory or contact data is returned, personal details (names, emails, phones, addresses, dates) are replaced with privacy tokens like [Person_1], [Email_1], etc. Use these tokens naturally in your responses — they will be resolved back to real values before the user sees your message. Never explain or call attention to the tokens. Treat [Person_1] as if it were the person's actual name.\n"
            : string.Empty;
        var webSearchSection = BuildWebSearchSection(webSearchActive);

        // Named for the model, which cannot see the tool list's absences: without it a web question sends
        // the model hunting through search_chats/recall/search_files for a search it already has.
        var webSearchBranch = webSearchActive
            ? "Web search is already enabled for this conversation — see the Web Search section. It needs no tool call, so do not reach for search_chats, recall or search_files instead."
            : "You cannot browse. Say so plainly, then answer from what you know while flagging that it may be out of date.";

        var toolSelectionSection = skipToolSelectionTree
            ? string.Empty
            : $"""
              ## Tool Selection

              Follow this decision tree strictly:

              1. Does the request mention a specific TIME, DATE, or SCHEDULE for notification?
                 - YES → Use Reminder tools. NOT a reminder: "Remember I like coffee" (no time = memory).
                 - NO → Continue to step 2.
              2. Does the request involve a TASK, ACTION ITEM, or something to DO?
                 - YES → Use Todo tools. NOT a todo: "Remember my WiFi password" (information = memory).
                 - NO → Continue to step 3.
              3. Does the request involve STORING, RECALLING, or UPDATING personal information?
                 - YES → Use Memory tools. To store/update, call remember(type, subject, content) — it automatically finds-or-creates the right record (you do NOT need to recall first); use forget to remove one. To look something up, run the knowledge loop rather than answering from the recall snippet alone: recall to find relevant pages → read_topic(reference) on a topic hit (tier=topic) for its full synthesis and the sources it cites → read_source(reference) for the primary text when the topic's summary is insufficient → browse_index to orient when recall misses. NOT a memory: "Remind me at 3 PM to call Bob" (has time = reminder). NOT a memory: "what did we decide in that chat last week" (a past conversation = step 5).
                 - NO → Continue to step 4.
              4. Does the request involve reading, searching, or editing CODE or FILES in the configured folder?
                 - YES → Use the file tools: search_files to locate files or text, read_file to inspect content (request a windowed slice with offset/limit for large files), and write_file to apply edits (the user approves a diff before any change is written).
                 - NO → Continue to step 5.
              5. Does the request refer to something from an EARLIER conversation ("we talked about",
                 "what did I tell you about X", "that chat where we…")?
                 - YES → Use the chat-history tools: search_chats to find the conversation (omit query to
                   list recent ones), then read_chat(chat_id) to read it. NOT chat history: "remember that I
                   like coffee" (a fact to store = memory).
                 - NO → Continue to step 6.
              6. Does the request need CURRENT information from the web (news, prices, "what is new",
                 anything past your training cutoff)?
                 - YES → {webSearchBranch}
                 - NO → Respond conversationally without tools.

              """;

        return $"""
            ## Identity

            {BuildIdentityBlock(activePersona)}

            ## Language

            {BuildLanguageInstruction()}

            {pluginSection}{toolSelectionSection}## Output Format

            {ResolveOutputFormat(activePersona)}
            {DeclinedActionRule}
            {tokenSection}{webSearchSection}
            """;
    }

    internal static (string CategoryLabel, string QueryTool, IReadOnlyList<string> ToolNames) GetAtCommandToolMapping(Pia.Models.AtCommandDomain domain) => domain switch
    {
        Pia.Models.AtCommandDomain.Memory => (
            "memory entry",
            "recall",
            (IReadOnlyList<string>)["recall", "browse_index", "read_topic", "read_source", "remember", "forget"]),
        Pia.Models.AtCommandDomain.Todo => (
            "todo",
            "query_todos",
            (IReadOnlyList<string>)["query_todos", "create_todo", "complete_todo", "update_todo", "delete_todo"]),
        Pia.Models.AtCommandDomain.Reminder => (
            "reminder",
            "query_reminders",
            (IReadOnlyList<string>)["query_reminders", "create_reminder", "update_reminder", "delete_reminder"]),
        Pia.Models.AtCommandDomain.Routine => (
            "scheduled research job",
            "query_scheduled_research",
            (IReadOnlyList<string>)["query_scheduled_research", "create_scheduled_research", "update_scheduled_research", "delete_scheduled_research", "list_routine_blueprints", "create_routine_from_blueprint"]),
        Pia.Models.AtCommandDomain.Files => (
            "file",
            "read_file",
            (IReadOnlyList<string>)["list_files", "read_file", "write_file", "delete_file", "search_files"]),
        Pia.Models.AtCommandDomain.Assignment => (
            "background assignment",
            "query_assignments",
            (IReadOnlyList<string>)["query_assignments", "get_assignment", "start_assignment"]),
        _ => throw new ArgumentOutOfRangeException(nameof(domain), domain,
            $"No tool mapping registered for at-command domain {domain}. Add a row to GetAtCommandToolMapping.")
    };

    private static IReadOnlySet<string> GetAllowedToolNames(IReadOnlyList<Pia.Models.AtCommand> commands)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in commands)
        {
            foreach (var name in GetAtCommandToolMapping(cmd.Domain).ToolNames)
                allowed.Add(name);
        }
        return allowed;
    }

    internal static string BuildAtCommandHint(IReadOnlyList<Pia.Models.AtCommand> commands)
    {
        if (commands.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## User Tool Hints decision");
        sb.AppendLine();
        sb.AppendLine("The user explicitly tagged this request with @-commands. These tags identify the item category and target — they are not ambiguous. Only the tools listed below will be loaded for this turn. Do NOT ask the user to clarify which kind of item they mean. Treat the rest of the user's message as the intended action on the tagged item.");
        sb.AppendLine();
        foreach (var cmd in commands)
        {
            var (categoryLabel, queryTool, toolNames) = GetAtCommandToolMapping(cmd.Domain);
            var toolFamily = $"{categoryLabel} tools ({string.Join(", ", toolNames)})";

            // Files are addressed by their relative path directly — there is no ID-resolution
            // step, so the generic "call {queryTool} first to obtain its ID" wording is wrong.
            if (cmd.Domain == Pia.Models.AtCommandDomain.Files)
            {
                if (cmd.ItemTitle is not null)
                    sb.AppendLine($"- The user's request targets the file at relative path \"{cmd.ItemTitle}\". Operate on that exact path directly with the {toolFamily} — no lookup step is needed (read_file before editing, then write_file).");
                else
                    sb.AppendLine($"- The user's request is about files in the assistant files folder — use the {toolFamily}.");
                continue;
            }

            if (cmd.ItemTitle is not null)
                sb.AppendLine($"- The user's request targets a {categoryLabel} titled \"{cmd.ItemTitle}\". Call {queryTool} first to obtain its ID, then perform the action described in the rest of the user's message (e.g. delete, update, complete). Available {toolFamily}.");
            else
                sb.AppendLine($"- The user's request is about {categoryLabel}s — use the {toolFamily}.");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Renders <c>@Files</c> previews for direct injection into the AI-visible user message, so a
    /// model that won't call <c>read_file</c> on its own still sees the tagged file(s). Each file
    /// becomes an <c>&lt;attached_file&gt;</c> element (XML-style, to avoid colliding with Markdown
    /// code fences that may appear in the content). A truncated file's note points the model at
    /// <c>read_file</c> for the rest — but only when <paramref name="toolsAvailable"/>, since on a
    /// no-tools turn that advice would be wrong. Returns <see cref="string.Empty"/> when there is
    /// nothing to inject.
    /// </summary>
    public static string BuildFileContextBlock(IReadOnlyList<FilePromptPreview> previews, bool toolsAvailable)
    {
        if (previews.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("The user attached the following file(s) with the @Files command. Use them as the primary context for this request.");

        foreach (var p in previews)
        {
            sb.Append("\n\n");

            if (!p.Found)
            {
                sb.Append($"<attached_file path=\"{EscapeAttr(p.RequestedPath)}\" error=\"{EscapeAttr(p.Error ?? "could not be read")}\"");
                if (toolsAvailable)
                    sb.Append(" note=\"Use list_files or search_files to locate it.\"");
                sb.Append(" />");
                continue;
            }

            sb.Append($"<attached_file path=\"{EscapeAttr(p.RequestedPath)}\" total_lines=\"{p.TotalLines}\"");
            if (p.Truncated)
            {
                sb.Append($" shown_lines=\"{p.ShownLines}\"");
                var note = toolsAvailable
                    ? $"Showing the first {p.ShownLines} of {p.TotalLines} lines; use read_file with offset/limit to read the rest."
                    : $"Showing the first {p.ShownLines} of {p.TotalLines} lines.";
                sb.Append($" note=\"{EscapeAttr(note)}\"");
            }
            sb.Append(">\n");
            sb.Append(p.Text);
            sb.Append("\n</attached_file>");
        }

        return sb.ToString();
    }

    /// <summary>
    /// No "read the rest" note on a truncated file: a dropped file has no path the model could re-open.
    /// </summary>
    public static string BuildAttachedFileBlock(IReadOnlyList<PendingFileAttachment> files)
    {
        if (files.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("The user attached the following file(s) to this message. Use them as context for the request.");

        foreach (var f in files)
        {
            sb.Append("\n\n");
            sb.Append($"<attached_file name=\"{EscapeAttr(f.FileName)}\" type=\"{f.Kind.ToString().ToLowerInvariant()}\"");
            if (f.Truncated)
            {
                var note = $"Showing the first {f.Text.Length} of {f.OriginalCharCount} characters.";
                sb.Append($" truncated=\"true\" note=\"{EscapeAttr(note)}\"");
            }
            sb.Append(">\n");
            sb.Append(f.Text);
            sb.Append("\n</attached_file>");
        }

        return sb.ToString();
    }

    private static string EscapeAttr(string value) =>
        value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");

    /// <summary>
    /// How web search reaches the model differs per provider — injected into this prompt (OpenRouter's
    /// plugin), a built-in tool the provider adds (OpenAI's web_search_preview), or server-side (Pia
    /// Cloud) — so this says where results arrive from without naming a mechanism.
    /// </summary>
    private static string BuildWebSearchSection(bool webSearchActive) =>
        webSearchActive
            ? "\n## Web Search\n\nWeb search is enabled for this conversation. Results reach you either already present in this prompt or through a built-in search tool your provider adds. There is no web-search tool in your tool list, so do not go looking for one, and never substitute chat-history, vault or file search for it. If no results reached you, say that plainly instead of answering from memory.\n\nWhen citing web sources, use only standard markdown links of the form [Title](https://example.com). Never use reference-style brackets like [text][url]. Keep citations sparse — one link per distinct source.\n"
            : string.Empty;

    private string BuildSystemPromptNoTools(Persona activePersona, bool webSearchActive = false)
    {
        var webSearchSection = BuildWebSearchSection(webSearchActive);
        return $"""
            ## Identity

            {BuildIdentityBlock(activePersona)}

            ## Language

            {BuildLanguageInstruction()}

            ## Output Format

            {ResolveOutputFormat(activePersona)}
            {webSearchSection}
            """;
    }

    internal static bool IsWebSearchActive(AiProvider provider)
        => provider.EnableWebSearch || provider.ProviderType == AiProviderType.PiaCloud;

    // suggest_agent_mode (R7): a no-op tool the model calls to offer switching the user from Chat to
    // Agent mode. It is intercepted pre-route in ChatSession.HandleToolCall (recording the reason and
    // returning a short ack); the returned "ok" here is never reached. Goal is NOT a parameter — it is
    // derived from the turn's user text at interception. Name pinned by the interception + tests.
    internal static AITool BuildSuggestAgentModeTool() =>
        AIFunctionFactory.Create(
            ([Description("One short sentence: why the user's request would be better handled as a multi-step Agent run.")] string reason) => "ok",
            "suggest_agent_mode",
            "Offer to switch the user from Chat to Agent mode when their request is a multi-step task that benefits from planning. Call at most once.");
}
