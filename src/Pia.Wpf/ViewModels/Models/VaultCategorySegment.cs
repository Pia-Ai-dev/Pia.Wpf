namespace Pia.ViewModels.Models;

/// <summary>
/// One slice of the Vault Overview composition: a canonical <paramref name="Type"/> (e.g.
/// <c>personal_profile</c>), its culture-independent <paramref name="DisplayName"/> from
/// <c>VaultIndexService.CanonicalGroups</c>, the item <paramref name="Count"/>, and the
/// <paramref name="Fraction"/> of the displayable vault it represents (in [0,1]).
/// </summary>
public record VaultCategorySegment(string Type, string DisplayName, int Count, double Fraction);
