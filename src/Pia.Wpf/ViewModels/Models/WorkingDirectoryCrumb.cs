namespace Pia.ViewModels.Models;

/// <summary>A breadcrumb segment in the working-directory picker.</summary>
/// <param name="Index">0 = root; 1..n = the nth path segment (used by <c>JumpToCrumbCommand</c>).</param>
/// <param name="Name">The segment folder name (empty for the root crumb).</param>
/// <param name="IsRoot">True for the synthetic root crumb (rendered with the home glyph).</param>
public readonly record struct WorkingDirectoryCrumb(int Index, string Name, bool IsRoot);
