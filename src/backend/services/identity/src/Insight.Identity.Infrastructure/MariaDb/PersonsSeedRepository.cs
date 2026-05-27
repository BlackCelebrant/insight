using Insight.Identity.Domain.Services;
using MySqlConnector;

namespace Insight.Identity.Infrastructure.MariaDb;

/// <summary>
/// MariaDB-backed <see cref="IPersonsSeedStore"/>. Reads feed the C#
/// resolver; <see cref="BulkInsertObservationsAsync"/> applies the
/// resolved observations; the two rebuilds refresh the derived caches
/// tenant-scoped inside a transaction.
/// </summary>
public sealed class PersonsSeedRepository : IPersonsSeedStore
{
    private readonly MariaDbConnectionFactory _factory;

    public PersonsSeedRepository(MariaDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyDictionary<SourceAccountKey, Guid>> GetKnownAccountBindingsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        await using var conn = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(SqlPersonsSeed.KnownAccountBindings, conn);
        cmd.Parameters.AddWithValue("@tenant_id", tenantId.ToByteArray(bigEndian: true));

        var result = new Dictionary<SourceAccountKey, Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new SourceAccountKey(
                reader.GetString("insight_source_type"),
                new Guid((byte[])reader["insight_source_id"], bigEndian: true),
                reader.GetString("source_account_id"));
            result[key] = new Guid((byte[])reader["person_id"], bigEndian: true);
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, Guid>> GetLatestEmailToPersonAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        await using var conn = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new MySqlCommand(SqlPersonsSeed.LatestEmailToPerson, conn);
        cmd.Parameters.AddWithValue("@tenant_id", tenantId.ToByteArray(bigEndian: true));

        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString("email")] = new Guid((byte[])reader["person_id"], bigEndian: true);
        }
        return result;
    }

    public async Task<int> BulkInsertObservationsAsync(
        IReadOnlyList<PersonObservationRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return 0;
        }

        await using var conn = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // One reused command inside the transaction. Per-row execute on a
        // single session is fast enough for a background operation;
        // multi-row VALUES batching is a future optimisation if seed
        // wallclock becomes a concern.
        await using var cmd = new MySqlCommand(SqlPersonsSeed.InsertObservation, conn, tx);
        var pValueType = cmd.Parameters.Add("@value_type", MySqlDbType.VarChar);
        var pSourceType = cmd.Parameters.Add("@source_type", MySqlDbType.VarChar);
        var pSourceId = cmd.Parameters.Add("@source_id", MySqlDbType.Binary);
        var pTenantId = cmd.Parameters.Add("@tenant_id", MySqlDbType.Binary);
        var pValueId = cmd.Parameters.Add("@value_id", MySqlDbType.VarChar);
        var pValueFullText = cmd.Parameters.Add("@value_full_text", MySqlDbType.VarChar);
        var pValue = cmd.Parameters.Add("@value", MySqlDbType.Text);
        var pPersonId = cmd.Parameters.Add("@person_id", MySqlDbType.Binary);
        var pAuthor = cmd.Parameters.Add("@author_person_id", MySqlDbType.Binary);
        var pReason = cmd.Parameters.Add("@reason", MySqlDbType.VarChar);
        var pCreatedAt = cmd.Parameters.Add("@created_at", MySqlDbType.DateTime);
        await cmd.PrepareAsync(cancellationToken).ConfigureAwait(false);

        var inserted = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pValueType.Value = row.ValueType;
            pSourceType.Value = row.InsightSourceType;
            pSourceId.Value = row.InsightSourceId.ToByteArray(bigEndian: true);
            pTenantId.Value = row.InsightTenantId.ToByteArray(bigEndian: true);
            pValueId.Value = (object?)row.ValueId ?? DBNull.Value;
            pValueFullText.Value = (object?)row.ValueFullText ?? DBNull.Value;
            pValue.Value = (object?)row.Value ?? DBNull.Value;
            pPersonId.Value = row.PersonId.ToByteArray(bigEndian: true);
            pAuthor.Value = row.AuthorPersonId.ToByteArray(bigEndian: true);
            pReason.Value = row.Reason;
            pCreatedAt.Value = row.CreatedAt;
            inserted += await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    public async Task RebuildAccountPersonMapAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var conn = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var del = new MySqlCommand(SqlPersonsSeed.DeleteAccountPersonMapForTenant, conn, tx))
        {
            del.Parameters.AddWithValue("@tenant_id", tenantId.ToByteArray(bigEndian: true));
            await del.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var ins = new MySqlCommand(SqlPersonsSeed.InsertAccountPersonMapForTenant, conn, tx))
        {
            ins.Parameters.AddWithValue("@tenant_id", tenantId.ToByteArray(bigEndian: true));
            await ins.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RebuildOrgChartAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var conn = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var del = new MySqlCommand(SqlPersonsSeed.DeleteOrgChartForTenant, conn, tx))
        {
            del.Parameters.AddWithValue("@tenant_id", tenantId.ToByteArray(bigEndian: true));
            await del.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        int edges;
        await using (var ins = new MySqlCommand(SqlPersonsSeed.InsertOrgChartForTenant, conn, tx))
        {
            ins.Parameters.AddWithValue("@tenant_id", tenantId.ToByteArray(bigEndian: true));
            edges = await ins.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return edges;
    }
}
