//! Repair the metric-ID collision where an earlier cut of
//! `m20260613_000002_member_ai_values` seeded the AI member-values metric at
//! `…0043` — the slot already owned by `Member PRs Merged`
//! (`m20260605_000001`). Its `ON DUPLICATE KEY UPDATE` overwrote `…0043`'s
//! `query_ref` with the AI query, so the team heatmap's PRs column (which reads
//! `…0043` for `prs_merged`) returned AI rows and rendered blank.
//!
//! `m20260613_000002` now seeds AI member-values at `…0049`. This migration
//! deterministically converges any environment that ran the pre-fix cut:
//!   - restore `…0043` to the `Member PRs Merged` query (verbatim from
//!     `m20260605_000001`);
//!   - ensure `…0049` carries the AI member-values query (reusing
//!     `m20260613_000002::ai_member_values_query` so the two never drift).
//!
//! Both writes are idempotent `ON DUPLICATE KEY UPDATE`, so on a fresh
//! deployment — where `…0043` is already `Member PRs Merged` and `…0049` was
//! just seeded by `m20260613_000002` — this is a no-op.

use sea_orm_migration::prelude::*;

use crate::migration::m20260613_000002_member_ai_values::ai_member_values_query;

#[derive(DeriveMigrationName)]
pub struct Migration;

const ZERO_TENANT: &str = "00000000000000000000000000000000";
const MEMBER_PRS_HEX: &str = "00000000000000000001000000000043";
const MEMBER_VALUES_AI_HEX: &str = "00000000000000000001000000000049";

/// `Member PRs Merged` query, copied verbatim from `m20260605_000001`.
const MEMBER_PRS_QR: &str = "SELECT person_id, sum(prs_merged) AS prs_merged FROM (SELECT person_key AS person_id, week AS metric_date, prs_merged FROM silver.mtr_git_person_weekly) GROUP BY person_id";

#[async_trait::async_trait]
impl MigrationTrait for Migration {
    async fn up(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        let db = manager.get_connection();
        // Restore …0043 → Member PRs Merged (undo the collision clobber).
        db.execute_unprepared(&format!(
            "INSERT INTO metrics (id, insight_tenant_id, name, description, query_ref, is_enabled) \
             VALUES (UNHEX('{MEMBER_PRS_HEX}'), UNHEX('{ZERO_TENANT}'), 'Member PRs Merged', \
             'Per-person PRs merged for a roster (person_id IN), period-bounded, from silver.mtr_git_person_weekly.', \
             '{qr}', 1) \
             ON DUPLICATE KEY UPDATE name=VALUES(name), description=VALUES(description), query_ref=VALUES(query_ref), is_enabled=1",
            qr = MEMBER_PRS_QR.replace('\'', "''"),
        ))
        .await?;
        // Ensure …0049 → AI member values (for envs where the pre-fix
        // m20260613_000002 already ran under its old id and won't re-run).
        db.execute_unprepared(&format!(
            "INSERT INTO metrics (id, insight_tenant_id, name, description, query_ref, is_enabled) \
             VALUES (UNHEX('{MEMBER_VALUES_AI_HEX}'), UNHEX('{ZERO_TENANT}'), 'Team Member Values — AI', \
             'Per-person AI metric values for a roster (person_id IN). Long rows (person_id, metric_key, value); no cohort. Distributable AI keys only (active-counter flags and NULL placeholders excluded).', \
             '{qr}', 1) \
             ON DUPLICATE KEY UPDATE name=VALUES(name), description=VALUES(description), query_ref=VALUES(query_ref), is_enabled=1",
            qr = ai_member_values_query().replace('\'', "''"),
        ))
        .await?;
        Ok(())
    }

    async fn down(&self, manager: &SchemaManager) -> Result<(), DbErr> {
        // Leave …0043 as Member PRs Merged; only drop the …0049 AI seed.
        let db = manager.get_connection();
        db.execute_unprepared(&format!(
            "DELETE FROM metrics WHERE id = UNHEX('{MEMBER_VALUES_AI_HEX}')"
        ))
        .await?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn restores_member_prs_at_0043() {
        assert!(MEMBER_PRS_QR.contains("FROM silver.mtr_git_person_weekly"));
        assert!(MEMBER_PRS_QR.contains("sum(prs_merged)"));
        assert_eq!(MEMBER_PRS_HEX, "00000000000000000001000000000043");
    }

    #[test]
    fn ai_values_target_is_0049_not_0043() {
        assert_eq!(MEMBER_VALUES_AI_HEX, "00000000000000000001000000000049");
        // The AI query is the per-person member-values query, not a cohort one.
        let qr = ai_member_values_query();
        assert!(qr.contains("insight.ai_bullet_rows"));
        assert!(!qr.contains("prs_merged"), "must not be the PRs query");
    }
}
