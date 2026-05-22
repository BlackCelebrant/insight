namespace Insight.Identity.Infrastructure.MariaDb;

/// <summary>
/// SQL for the `visibility` table. Step 1 of #346 ships only the seed
/// query that fetches a viewer's active grants — the recursive CTE that
/// joins these with `org_chart` to compute the full visible set lands
/// with the `VisibilityService` (step 3 in the breakdown).
/// </summary>
internal static class SqlVisibility
{
    public const string ActiveGrantsByViewer = """
        SELECT visibility_id, insight_tenant_id, viewer_person_id, viewed_person_id,
               valid_from, valid_to, author_person_id, reason, created_at
        FROM visibility
        WHERE insight_tenant_id = @tenant_id
          AND viewer_person_id  = @viewer_person_id
          AND valid_to IS NULL
        """;
}
