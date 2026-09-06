namespace CryptoSmithX.WebApp.Admin.ViewComponents;

/// <summary>One entry in the grouped sidebar. A soon item is a placeholder for a page that does
/// not exist yet — rendered dimmed and not clickable.</summary>
public sealed record NavItem(string Label, string Href, string? Badge = null, bool Soon = false);

public sealed record NavGroup(string Label, IReadOnlyList<NavItem> Items);

public sealed record SideNavModel(IReadOnlyList<NavGroup> Groups);
