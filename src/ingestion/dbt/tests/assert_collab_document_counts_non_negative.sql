-- Business-rule data test (#1321 silver-layer integrity).
-- Activity counts can never be negative; a negative value means a broken
-- transform or bad source row and would corrupt any metric that sums them.
-- dbt fails the build if this returns any rows. NULL (not-ingested, e.g.
-- visited_page_count on OneDrive) is intentionally NOT flagged — NULL < 0 is
-- NULL, not a violation; honest NULLs are handled separately.
SELECT
    unique_key,
    viewed_or_edited_count,
    synced_count,
    shared_internally_count,
    shared_externally_count,
    visited_page_count
FROM {{ ref('class_collab_document_activity') }}
WHERE viewed_or_edited_count   < 0
   OR synced_count             < 0
   OR shared_internally_count  < 0
   OR shared_externally_count  < 0
   OR visited_page_count       < 0
