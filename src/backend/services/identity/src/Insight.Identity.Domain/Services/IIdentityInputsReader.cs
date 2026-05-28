namespace Insight.Identity.Domain.Services;

/// <summary>
/// Read-side port over the analytical
/// <c>identity.identity_inputs</c> table (ClickHouse). The
/// <c>persons-seed</c> operation consumes a tenant-scoped snapshot of
/// observations (both UPSERT and DELETE) and replays them into MariaDB.
/// </summary>
public interface IIdentityInputsReader
{
    /// <summary>
    /// Stream every observation in the tenant, ordered so that the
    /// first observation per
    /// <c>(insight_source_type, insight_source_id, source_account_id)</c>
    /// is the most recent one (descending <c>_synced_at</c>). The seed
    /// algorithm relies on this ordering when picking an account's
    /// current email and deciding whether the account is currently
    /// closed (latest observation is a DELETE).
    /// </summary>
    IAsyncEnumerable<IdentityInputRow> StreamAsync(
        Guid tenantId,
        CancellationToken cancellationToken);
}

/// <summary>
/// One row of <c>identity.identity_inputs</c>. <see cref="SyncedAt"/>
/// is the moment the source recorded the value; the seeder uses it as
/// the persons.<c>created_at</c> so observation history stays
/// chronological inside MariaDB. <see cref="IsDelete"/> is <c>true</c>
/// when <c>operation_type='DELETE'</c> — a deactivation signal the
/// seeder uses to detect a closed account (no persons row is written
/// for the DELETE itself).
/// </summary>
public sealed record IdentityInputRow(
    Guid InsightTenantId,
    string InsightSourceType,
    Guid InsightSourceId,
    string SourceAccountId,
    string ValueType,
    string Value,
    DateTime SyncedAt,
    bool IsDelete);
