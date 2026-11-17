namespace Pia.Services.Interfaces;

public sealed record ProposedPlan(string Goal, IReadOnlyList<Pia.Services.AgentPlanner.PlanStepArg> Steps);

/// <summary>The outcome the session hands back to the model; <paramref name="EndsTurn"/> stops the tool loop.</summary>
public sealed record ProposePlanOutcome(string Result, bool EndsTurn);
