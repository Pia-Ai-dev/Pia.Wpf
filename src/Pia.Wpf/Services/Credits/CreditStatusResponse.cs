namespace Pia.Services.Credits;

public sealed record CreditStatusResponse(
    bool Limited,
    bool? Suspended,
    CreditWindowDto? Hourly,
    CreditWindowDto? Daily,
    CreditWindowDto? Weekly,
    CreditPoolDto? Pool,
    CreditTopUpDto? TopUp,
    CreditGroupCapDto? GroupCap);

public sealed record CreditWindowDto(long Limit, long Used, DateTime? ResetsAt);

public sealed record CreditPoolDto(long Total, long Used, long Remaining, DateTime ResetsAt);

public sealed record CreditTopUpDto(long Granted, long Consumed, long Remaining);

public sealed record CreditGroupCapDto(CreditWindowDto? Daily, CreditWindowDto? Weekly);
