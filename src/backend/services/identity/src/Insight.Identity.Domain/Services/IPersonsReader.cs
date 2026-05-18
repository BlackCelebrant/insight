namespace Insight.Identity.Domain.Services;

/// <summary>
/// Repository abstraction the lookup service depends on. The infrastructure
/// project supplies a MariaDB-backed implementation; tests can stub the
/// interface directly.
/// </summary>
public interface IPersonsReader
{
    /// <summary>
    /// Resolve a single <c>person_id</c> from a lookup email. Returns
    /// <c>null</c> when no current observation in the tenant has
    /// <c>value_type='email'</c> = <paramref name="email"/>. The
    /// comparison is case-insensitive thanks to the
    /// <c>utf8mb4_unicode_ci</c> collation on <c>persons.value_id</c>
    /// (ADR-0011 / schema-fix PR).
    /// </summary>
    Task<Guid?> ResolvePersonIdByEmailAsync(
        Guid tenantId,
        string email,
        CancellationToken cancellationToken);

    /// <summary>
    /// Latest-per-source observations for a single <c>person_id</c> within
    /// the tenant. Empty list when the person has no observations.
    /// </summary>
    Task<IReadOnlyList<PersonObservation>> GetLatestObservationsAsync(
        Guid tenantId,
        Guid personId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Direct subordinates: <c>person_id</c>s whose latest
    /// <c>parent_person_id</c> observation across sources equals
    /// <paramref name="parentPersonId"/>. Reserved for Phase 2; Phase 1
    /// callers ignore the result.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetDirectSubordinateIdsAsync(
        Guid tenantId,
        Guid parentPersonId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Phase 1 of cyberfabric/cyber-insight#348: current parent edges
    /// for <paramref name="childPersonId"/> across all source instances
    /// within the tenant. Reads <c>person_parent_map</c> rows with
    /// <c>valid_to IS NULL</c>; an empty list means the person has no
    /// recorded parent in any source. Phase-1 invariant: at most one
    /// CURRENT parent per (tenant, source_type, source_id, child), so
    /// the list size equals the number of source instances that have
    /// a current parent observation for this person.
    /// </summary>
    Task<IReadOnlyList<PersonParentEdge>> GetCurrentParentsAsync(
        Guid tenantId,
        Guid childPersonId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Phase 1 of cyberfabric/cyber-insight#348: current direct-children
    /// edges where <paramref name="parentPersonId"/> is the parent.
    /// Reads <c>person_parent_map</c> rows with <c>valid_to IS NULL</c>;
    /// an empty list means no one currently reports to this person in
    /// any source. The future Phase-2 subordinates expansion and the
    /// Phase-3 <c>/v1/subchart/{person_id}?depth=N</c> recursive walk
    /// both build on top of this query.
    /// </summary>
    Task<IReadOnlyList<PersonParentEdge>> GetCurrentChildrenAsync(
        Guid tenantId,
        Guid parentPersonId,
        CancellationToken cancellationToken);
}

/// <summary>
/// One parent->child edge from <c>person_parent_map</c>, scoped to a
/// single source instance. Phase 1 of cyberfabric/cyber-insight#348.
/// The same person may appear as a <c>ChildPersonId</c> in multiple
/// edges, one per source instance where the source emitted a parent
/// observation for them; the edge granularity is therefore
/// (tenant, source_type, source_id, child).
/// </summary>
public sealed record PersonParentEdge(
    string InsightSourceType,
    Guid InsightSourceId,
    Guid ChildPersonId,
    Guid ParentPersonId,
    DateTime ValidFrom);
