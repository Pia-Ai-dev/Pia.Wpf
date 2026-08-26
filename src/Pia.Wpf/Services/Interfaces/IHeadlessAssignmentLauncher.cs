using Pia.Services.Operators;
using Pia.Shared.Operators;

namespace Pia.Services.Interfaces;

/// <summary>Starts an assignment with nobody to affirm it: the item list is always empty and the audit line
/// names the granting run instead of a person.</summary>
public interface IHeadlessAssignmentLauncher
{
    /// <param name="skill">Already resolved against the surface — there is nobody to pick one.</param>
    /// <param name="grantedBy">From <see cref="AssignmentGranter"/>; goes into the audit line.</param>
    Task<AssignmentStartStatus> StartAsync(
        AssignmentSkill skill, string prompt, string grantedBy, CancellationToken ct = default);
}
