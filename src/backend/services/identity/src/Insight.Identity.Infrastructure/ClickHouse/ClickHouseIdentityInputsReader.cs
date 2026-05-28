using System.Runtime.CompilerServices;
using Insight.Identity.Domain.Services;
using Octonica.ClickHouseClient;

namespace Insight.Identity.Infrastructure.ClickHouse;

/// <summary>
/// ClickHouse-backed <see cref="IIdentityInputsReader"/>. Streams rows
/// via the async DataReader so a large tenant does not buffer the full
/// result in memory — the seeder accumulates the working dictionaries
/// as rows arrive.
/// </summary>
public sealed class ClickHouseIdentityInputsReader : IIdentityInputsReader
{
    // The ORDER BY mirrors seed-persons-from-identity-input.py:
    // _synced_at DESC within (tenant, source_type, source_id,
    // source_account_id) so the FIRST row per account is the latest
    // observation — the seed algorithm uses this to pick an account's
    // current email and to detect a closed account (latest row is a
    // DELETE). DELETE rows are streamed too; they flag the account as
    // closed but never produce a persons observation.
    // identity_inputs stores insight_tenant_id / insight_source_id as
    // Nullable(String) (dashed UUID text), so the tenant filter is a
    // plain string compare. The `_str` aliases avoid shadowing the
    // source columns referenced in WHERE (a same-name SELECT alias
    // would otherwise be substituted into the WHERE clause).
    private const string StreamSql = """
        SELECT
            toString(insight_tenant_id) AS tenant_id_str,
            insight_source_type,
            toString(insight_source_id) AS source_id_str,
            source_account_id,
            value_type,
            value,
            _synced_at,
            operation_type
        FROM identity.identity_inputs
        WHERE insight_tenant_id = {tenant:String}
          AND operation_type    IN ('UPSERT', 'DELETE')
          AND value             IS NOT NULL
          AND value             != ''
        ORDER BY
            insight_source_type,
            insight_source_id,
            source_account_id,
            _synced_at DESC,
            value_type,
            value
        """;

    private readonly ClickHouseConnectionFactory _factory;

    public ClickHouseIdentityInputsReader(ClickHouseConnectionFactory factory)
    {
        _factory = factory;
    }

    public async IAsyncEnumerable<IdentityInputRow> StreamAsync(
        Guid tenantId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var conn = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand(StreamSql);
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new IdentityInputRow(
                InsightTenantId:   Guid.Parse(reader.GetString(0)),
                InsightSourceType: reader.GetString(1),
                InsightSourceId:   Guid.Parse(reader.GetString(2)),
                SourceAccountId:   reader.GetString(3),
                ValueType:         reader.GetString(4),
                Value:             reader.GetString(5),
                SyncedAt:          reader.GetDateTime(6),
                IsDelete:          string.Equals(reader.GetString(7), "DELETE", StringComparison.Ordinal));
        }
    }
}
