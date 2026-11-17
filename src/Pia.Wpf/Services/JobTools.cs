using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Pia.Services;

/// <summary>The tools a conversation turn gets while its chat has an unfinished job.</summary>
public static class JobTools
{
    public const string StepDetails = "job_step_details";
    public const string UpdatePlan = "job_update_plan";
    public const string Note = "job_note";
    public const string Pause = "job_pause";
    public const string RestartStep = "job_restart_step";
    public const string Continue = "job_continue";
    public const string Answer = "job_answer";
    public const string Stop = "job_stop";

    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        StepDetails, UpdatePlan, Note, Pause, RestartStep, Continue, Answer, Stop,
    };

    private const string NoStart =
        " It never starts a plan that waits for approval and never answers a tool approval; the user does both on the job block.";

    public sealed class PlanEdit
    {
        [Description("edit, insert, remove or move")]
        public string Action { get; set; } = string.Empty;

        [Description("The step's id from the job block, e.g. 3f2a9c1b. For insert, leave empty.")]
        public string? Step { get; set; }

        [Description("For insert or move: the id of the step to place it after, or \"start\". Empty means the end.")]
        public string? After { get; set; }

        [Description("For edit or insert: the step's new title.")]
        public string? Title { get; set; }

        [Description("For edit or insert: what the step should do, in one or two sentences.")]
        public string? Details { get; set; }
    }

    public static IReadOnlyList<AITool> Build() =>
    [
        AIFunctionFactory.Create(
            ([Description("The step's id from the job block.")] string step) => "ok",
            StepDetails,
            "Read one step of the user's running job: its reply and the tool calls it made. Use it for questions the job block does not answer."),
        AIFunctionFactory.Create(
            ([Description("The changes, applied in order.")] PlanEdit[] changes) => "ok",
            UpdatePlan,
            "Change steps of the running job that have not started: edit, insert, remove or reorder them. A running or finished step cannot change. The job picks the change up before its next step." + NoStart),
        AIFunctionFactory.Create(
            ([Description("The instruction, in the user's words.")] string note) => "ok",
            Note,
            "Give the running job a steering note that every remaining step follows." + NoStart),
        AIFunctionFactory.Create(
            ([Description("true: let the running step finish, then pause. false: pause now; the running step is run again on continue.")] bool afterThisStep) => "ok",
            Pause,
            "Pause the running job." + NoStart),
        AIFunctionFactory.Create(
            ([Description("What the step should do differently.")] string note) => "ok",
            RestartStep,
            "Cancel the job's running step and run it again with a note." + NoStart),
        AIFunctionFactory.Create(
            () => "ok",
            Continue,
            "Resume the job when it is paused, stopped at its budget, or stopped by an interruption such as a connection or credit problem." + NoStart),
        AIFunctionFactory.Create(
            ([Description("The user's answer, in their words.")] string answer) => "ok",
            Answer,
            "Answer the question the job asked the user, and let the job go on." + NoStart),
        AIFunctionFactory.Create(
            () => "ok",
            Stop,
            "Cancel the job for good. Only when the user clearly asks to stop or cancel it."),
    ];

    public const string PromptAddition =
        """


        ## The user's running job
        This chat has a job working in the background. Its current state is appended to the user's latest message as a job block. The job does not hear this conversation; it changes only through the job_* tools. Use them when the user asks for a change, then say in one sentence what you did. Answer questions about the job from the job block, or read a step with job_step_details. The job_* tools never start a plan that waits for approval and never answer a tool approval: if the user says "go ahead" for one of those, tell them to use Start or the choices on the job block.
        """;
}
