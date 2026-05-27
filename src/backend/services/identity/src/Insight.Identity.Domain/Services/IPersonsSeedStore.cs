namespace Insight.Identity.Domain.Services;

/// <summary>
/// Persistence port for the <c>persons-seed</c> operation. Splits into
/// the resolver-feeding reads (current bindings, latest emails), the
/// apply write (bulk observation insert), and the two derived-cache
/// rebuilds. All operations are tenant-scoped.
/// </summary>
public interface IPersonsSeedStore
{
    /// <summary>
    /// Current <c>source_account_id → person_id</c> bindings in the
    /// tenant (latest <c>value_type='id'</c> per account). Feeds the
    /// known-account branch of <see cref="PersonAssignmentResolver"/>.
    /// </summary>
    Task<IReadOnlyDictionary<SourceAccountKey, Guid>> GetKnownAccountBindingsAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Current normalised-email → person_id map in the tenant (latest
    /// email observation per email). Feeds the email-link branch of
    /// <see cref="PersonAssignmentResolver"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, Guid>> GetLatestEmailToPersonAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// INSERT IGNORE every observation row (idempotent re-seed).
    /// Returns the number of rows actually inserted (duplicates
    /// swallowed by the unique key are not counted).
    /// </summary>
    Task<int> BulkInsertObservationsAsync(
        IReadOnlyList<PersonObservationRow> rows,
        CancellationToken cancellationToken);

    /// <summary>
    /// Rebuild the tenant's <c>account_person_map</c> from
    /// <c>persons</c> — tenant-scoped DELETE + INSERT inside one
    /// transaction.
    /// </summary>
    Task RebuildAccountPersonMapAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Rebuild the tenant's <c>org_chart</c> from <c>persons</c> —
    /// tenant-scoped DELETE + INSERT inside one transaction. Returns
    /// the number of edge rows written.
    /// </summary>
    Task<int> RebuildOrgChartAsync(Guid tenantId, CancellationToken cancellationToken);
}
