using Microsoft.Extensions.AI;

namespace Pia.Models;

public abstract record ChatStreamItem;

public sealed record TextDelta(string Text) : ChatStreamItem;

public sealed record Finished(UsageDetails? Usage, string Model) : ChatStreamItem;
