namespace Pia.Models.Flow;

/// <summary>
/// The single target severity vocabulary every Flow source maps onto.
/// Ordered ascending by noticeability; higher values peek more assertively (see design §4).
/// </summary>
public enum FlowSeverity
{
    Info,
    Success,
    Warning,
    Error,
    ActionRequired,
}
