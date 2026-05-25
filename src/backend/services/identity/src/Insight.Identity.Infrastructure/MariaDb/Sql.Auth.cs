namespace Insight.Identity.Infrastructure.MariaDb;

/// <summary>
/// SQL for caller resolution at request time (#346 follow-up). Both
/// queries skip the tenant filter on purpose — caller resolution
/// does not need a tenant. Tenant scoping can be added later once the
/// api-gateway BFF is in place.
/// </summary>
internal static class SqlAuth
{
    /// <summary>
    /// Returns the <c>person_id</c> bound to a source-native account
    /// id (active row only). Used to map a JWT <c>oid</c> or <c>sub</c>
    /// claim to a person. Uses <c>idx_by_account</c>.
    /// </summary>
    public const string ResolvePersonIdByAccountId = """
        SELECT person_id
        FROM account_person_map
        WHERE source_account_id = @account_id
          AND valid_to IS NULL
        LIMIT 1
        """;

    /// <summary>
    /// Returns the distinct <c>person_id</c>s whose latest email
    /// observation equals <c>@value</c> across all tenants. Same query
    /// as <see cref="SqlProfiles.ResolvePersonIdsByEmail"/> but without
    /// the tenant filter. Case-insensitive thanks to the
    /// <c>utf8mb4_unicode_ci</c> collation on <c>value_id</c>
    /// (ADR-0011).
    /// </summary>
    public const string ResolvePersonIdsByEmailAcrossTenants = """
        WITH ranked AS (
            SELECT
                person_id,
                value_id,
                ROW_NUMBER() OVER (
                    PARTITION BY insight_tenant_id, person_id, insight_source_type, insight_source_id, value_type
                    ORDER BY created_at DESC, id DESC
                ) AS rn
            FROM persons
            WHERE value_type = 'email'
        )
        SELECT DISTINCT person_id
        FROM ranked
        WHERE rn = 1
          AND value_id = @value
        """;
}
