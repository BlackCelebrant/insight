namespace Insight.Identity.Domain.Services;

/// <summary>
/// Routes an observation value into exactly one of the three
/// <c>persons</c> value columns (<c>value_id</c> / <c>value_full_text</c>
/// / <c>value</c>) by <c>value_type</c>, mirroring the Python seeder's
/// <c>route_value</c>. Identifier-shaped types go to the indexed
/// <c>value_id</c>; human-readable attributes go to <c>value_full_text</c>;
/// everything else falls through to the <c>TEXT value</c> column. Values
/// longer than their column rejects are dropped (all-null result) rather
/// than truncated — truncation would let two distinct observations
/// collapse onto one key.
/// </summary>
public static class ValueRouting
{
    public const int MaxValueIdLen = 320;        // VARCHAR(320) — RFC 5321/5322 email upper bound
    public const int MaxValueFullTextLen = 512;  // VARCHAR(512) — display_name catch-all
    public const int MaxSourceAccountIdLen = 320; // VARCHAR(320) — same domain as value_id

    private static readonly HashSet<string> ValueIdTypes = new(StringComparer.Ordinal)
    {
        "id", "email", "username", "employee_id",
        "parent_email", "parent_id", "parent_person_id",
    };

    private static readonly HashSet<string> ValueFullTextTypes = new(StringComparer.Ordinal)
    {
        "display_name", "first_name", "last_name",
        "department", "division", "job_title", "status",
    };

    /// <summary>
    /// Returns the routed (valueId, valueFullText, value) triple — one
    /// non-null — or all-null when the value exceeds its column's limit
    /// (caller counts the rejection).
    /// </summary>
    public static (string? ValueId, string? ValueFullText, string? Value) Route(string valueType, string value)
    {
        ArgumentNullException.ThrowIfNull(valueType);
        ArgumentNullException.ThrowIfNull(value);

        if (ValueIdTypes.Contains(valueType))
        {
            return value.Length > MaxValueIdLen ? (null, null, null) : (value, null, null);
        }
        if (ValueFullTextTypes.Contains(valueType))
        {
            return value.Length > MaxValueFullTextLen ? (null, null, null) : (null, value, null);
        }
        // Catch-all: TEXT column, no length cap enforced by the seed.
        return (null, null, value);
    }
}
