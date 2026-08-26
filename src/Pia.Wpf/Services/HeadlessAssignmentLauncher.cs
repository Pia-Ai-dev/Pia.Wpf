using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;

namespace Pia.Services;

/// <inheritdoc cref="IHeadlessAssignmentLauncher"/>
public sealed class HeadlessAssignmentLauncher : IHeadlessAssignmentLauncher
{
    private readonly IAssignmentSurfaceCache _surface;
    private readonly IAssignmentConsentStore _consent;
    private readonly IAssignmentRunOrchestrator _orchestrator;
    private readonly ILogger<HeadlessAssignmentLauncher> _logger;

    public HeadlessAssignmentLauncher(
        IAssignmentSurfaceCache surface,
        IAssignmentConsentStore consent,
        IAssignmentRunOrchestrator orchestrator,
        ILogger<HeadlessAssignmentLauncher> logger)
    {
        _surface = surface;
        _consent = consent;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task<AssignmentStartStatus> StartAsync(
        AssignmentSkill skill, string prompt, string grantedBy, CancellationToken ct = default)
    {
        if (!_surface.Surface.Available)
        {
            _logger.LogInformation("Not starting an unattended assignment: the surface is not available.");
            return AssignmentStartStatus.Refused;
        }

        // Ahead of the mint, unlike the orchestrator's identical cap: a consent line for a send that cannot
        // happen is noise in the audit file.
        var text = prompt.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > AssignmentInput.MaxPromptChars)
        {
            _logger.LogInformation("Not starting an unattended assignment: the prompt is empty or over the cap.");
            return AssignmentStartStatus.TooLarge;
        }

        // THE EMPTY LIST IS THE RULE, in the one place it can be seen: nobody ticked a record, so nothing
        // local is opened and the orchestrator's read loop has nothing to do.
        var receipt = await _consent.RecordAsync(skill.Name, skill.Mode, [], grantedBy, text.Length, ct);
        var outcome = await _orchestrator.StartAsync(new AssignmentRequest(skill.Name, text, []), receipt, ct);

        _logger.LogInformation(
            "An unattended assignment on '{Skill}' granted by {GrantedBy} ended as {Status}.",
            skill.Name, grantedBy, outcome.Status);
        _logger.SensitiveDebug("Unattended assignment prompt: {Prompt}", text);

        return outcome.Status;
    }
}
