namespace Pia.Models;

public enum HistoryDateBucket
{
    /// <summary>Not a date at all: starred chats group above every date bucket, hence the negative value
    /// the group ordering sorts on.</summary>
    Favorites = -1,
    Today = 0,
    Yesterday = 1,
    ThisWeek = 2,
    EarlierThisMonth = 3,
    Older = 4,
}
