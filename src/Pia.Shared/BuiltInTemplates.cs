using Pia.Shared.Models;

namespace Pia.Shared;

public static class BuiltInTemplates
{
    public static IReadOnlyList<BuiltInTemplate> All { get; } =
    [
        new(
            "00000001-0000-0000-0000-000000000001",
            "Business Email",
            "Transform this text into a professional business email. Requirements:\n- Use formal, courteous language with proper salutations and closings\n- Keep the message clear, concise, and action-oriented\n- Preserve the original intent and all key information\n- Do not add information that wasn't in the original",
            "Transform casual text into professional business correspondence"),

        new(
            "00000001-0000-0000-0000-000000000002",
            "Community Article",
            "Transform this text into an engaging community article or blog post. Requirements:\n- Use a friendly, approachable tone while remaining professional\n- Structure with clear paragraphs and logical flow\n- Make it engaging and easy to read\n- Preserve all key points and the original intent",
            "Create engaging content for community platforms and blogs"),

        new(
            "00000001-0000-0000-0000-000000000003",
            "Message to Friend",
            "Transform this text into a casual message to a friend. Requirements:\n- Use warm, conversational language as if talking to a close friend\n- Keep it natural and relaxed\n- Preserve the core message and intent",
            "Convert formal text into casual friendly messages"),

        new(
            "00000001-0000-0000-0000-000000000004",
            "Grammar & Spelling Fix",
            "Fix only grammar, spelling, and punctuation errors in this text. Requirements:\n- Make minimal changes - preserve the original wording and style as much as possible\n- Do not rephrase, restructure, or \"improve\" the writing style\n- Do not change the tone or formality level\n- Keep all original meaning and intent intact\n- If the text has no errors, return it unchanged",
            "Fix grammar and spelling errors while preserving the original style and wording"),

        new(
            "00000001-0000-0000-0000-000000000005",
            "Clarity & Grammar",
            "Improve this text for clarity and correctness. Requirements:\n- Fix all grammar, spelling, and punctuation errors\n- Improve sentence structure for better readability\n- Break up overly long sentences if needed\n- Use clear, straightforward language\n- Preserve the original meaning, tone, and intent\n- Keep approximately the same length",
            "Fix errors and improve readability while preserving the original meaning"),

        new(
            "00000001-0000-0000-0000-000000000006",
            "C# Code Prompt",
            "Transform this text into a clear, well-structured prompt for generating C#/.NET code. Requirements:\n- Extract and organize the requirements clearly\n- Specify expected inputs, outputs, and behavior\n- Include any constraints or edge cases mentioned\n- Use C# and .NET terminology where appropriate\n- Structure as: Context \u2192 Requirements \u2192 Constraints \u2192 Expected Behavior\n- Mention relevant .NET APIs, patterns, or conventions if applicable\n- Follow C# naming conventions (PascalCase for methods/properties, camelCase for locals)\n- Make it actionable and unambiguous for code generation",
            "Convert requirements into a well-structured prompt for C#/.NET code generation")
    ];
}
