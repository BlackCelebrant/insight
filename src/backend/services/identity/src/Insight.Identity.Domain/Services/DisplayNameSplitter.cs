namespace Insight.Identity.Domain.Services;

/// <summary>
/// Best-effort split of a <c>display_name</c> into first/last name when
/// dedicated <c>first_name</c> / <c>last_name</c> observations are missing.
/// Two formats are supported, in order of priority:
/// <list type="number">
///   <item><c>"Last, First"</c> — comma-separated → first=after-comma, last=before-comma.</item>
///   <item><c>"First Last"</c> — space-separated → first=first token, last=remaining tokens.</item>
/// </list>
/// Single-token names yield first=token, last="". Empty/whitespace yields ("", "").
/// </summary>
public static class DisplayNameSplitter
{
    public static (string FirstName, string LastName) Split(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (string.Empty, string.Empty);
        }

        var trimmed = displayName.Trim();

        var commaIndex = trimmed.IndexOf(',');
        if (commaIndex >= 0)
        {
            var last = trimmed[..commaIndex].Trim();
            var first = trimmed[(commaIndex + 1)..].Trim();
            return (first, last);
        }

        var spaceIndex = trimmed.IndexOf(' ');
        if (spaceIndex < 0)
        {
            return (trimmed, string.Empty);
        }

        var firstToken = trimmed[..spaceIndex].Trim();
        var rest = trimmed[(spaceIndex + 1)..].Trim();
        return (firstToken, rest);
    }
}
