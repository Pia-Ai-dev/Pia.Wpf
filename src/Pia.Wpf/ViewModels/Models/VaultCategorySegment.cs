namespace Pia.ViewModels.Models;

/// <summary>
/// One slice of the Vault Overview composition, mirroring one left-panel group. <paramref name="Type"/>
/// is the group key that also drives the swatch color (via <c>VaultCategoryColorConverter</c>): a
/// canonical type (e.g. <c>personal_profile</c>) → theme brush, or a topic category (e.g. <c>person</c>)
/// → a cycled palette color. <paramref name="DisplayName"/> is the culture-independent heading from
/// <c>VaultIndexService.CanonicalGroups</c> (or <c>TopicCategories</c> for an exploded topic category,
/// e.g. "People"), with the item <paramref name="Count"/> and the <paramref name="Fraction"/> of the
/// displayable vault it represents (in [0,1]).
/// </summary>
public record VaultCategorySegment(string Type, string DisplayName, int Count, double Fraction);
