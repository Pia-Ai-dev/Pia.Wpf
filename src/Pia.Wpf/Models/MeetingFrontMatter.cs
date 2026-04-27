using System.Collections.Generic;

namespace Pia.Models;

/// <summary>
/// Parsed YAML front-matter from a saved meeting transcript markdown file.
/// </summary>
public sealed record MeetingFrontMatter(string? Date, IReadOnlyList<string> Speakers, string? OriginalFilename);
