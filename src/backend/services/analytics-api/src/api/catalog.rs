//! `POST /catalog/get_metrics` HTTP handler (Refs #524).
//!
//! Implements `cpt-metric-cat-component-catalog-reader`'s HTTP surface per
//! DESIGN §3.3 "Catalog Read":
//!
//! - **Auth**: bearer-token-only at the gateway (out of scope here — Q1 ack);
//!   the request-context fields `role_slug` / `team_id` are accepted from the
//!   JSON body only. `tenant_id` is NEVER taken from the body; it is resolved
//!   server-side by `tenant_middleware` (Refs #522) which has already populated
//!   `SecurityContext.insight_tenant_id` by the time we run.
//! - **Content-Type**: `application/json` required — enforced by Axum's
//!   `Json<T>` extractor (`MissingJsonContentType` → 415). Closes the cross-site
//!   form-post CSRF path per DESIGN §3.3.
//! - **Body shape**: `GetMetricsRequest` with `deny_unknown_fields`; a hostile
//!   `tenant_id` smuggled into the body is rejected by serde and surfaces as a
//!   canonical 400 `invalid_argument` here.
//!
//! ## Why we map `JsonRejection` ourselves
//!
//! Axum's built-in `Json<T>` extractor handles both content-type validation
//! and body deserialization — we don't reimplement either. The one thing we
//! need to do is convert `JsonRejection` to the canonical RFC 9457
//! `application/problem+json` envelope mandated by DNA `REST/API.md §7` and
//! DESIGN §3.3, because Axum's default rejection responses use a plain-text
//! body. [`json_rejection_to_response`] performs that mapping.

use std::sync::Arc;

use axum::Json;
use axum::extract::{Extension, State, rejection::JsonRejection};
use axum::http::{StatusCode, header};
use axum::response::{IntoResponse, Response};
use modkit_canonical_errors::{CanonicalError, Problem};
use serde_json::json;

use super::AppState;
use super::error::MetricCatalogError;
use crate::auth::SecurityContext;
use crate::domain::catalog::response::GetMetricsRequest;

/// `POST /catalog/get_metrics` handler.
///
/// # Errors
///
/// - `400 invalid_argument` — malformed body, unknown body fields (incl.
///   `tenant_id`), or other deserialization failures.
/// - `415 unsupported_media_type` — Content-Type is missing or not
///   `application/json`.
/// - `500 internal` — resolver / DB failure (Redis blips are absorbed by the
///   reader's degrade-gracefully behavior).
pub async fn get_metrics(
    State(state): State<Arc<AppState>>,
    Extension(ctx): Extension<SecurityContext>,
    json: Result<Json<GetMetricsRequest>, JsonRejection>,
) -> Response {
    let req = match json {
        Ok(Json(r)) => r,
        Err(rej) => return json_rejection_to_response(&rej),
    };

    let response = match state
        .catalog_reader
        .read(
            ctx.insight_tenant_id,
            req.role_slug.as_deref(),
            req.team_id.as_deref(),
        )
        .await
    {
        Ok(r) => r,
        Err(e) => {
            tracing::error!(error = %e, "catalog: resolver failed");
            return CanonicalError::internal("failed to resolve catalog")
                .create()
                .into_response();
        }
    };

    Json(response).into_response()
}

/// Map Axum's `JsonRejection` variants onto the canonical RFC 9457 envelope.
///
/// | Variant | HTTP | GTS category |
/// |---|---|---|
/// | `MissingJsonContentType` | 415 | `unsupported_media_type` (catalog-local; see [`unsupported_media_type_response`]) |
/// | `JsonSyntaxError` | 400 | `invalid_argument` (field: `body`) |
/// | `JsonDataError` | 400 | `invalid_argument` (the serde-path field if known, else `body`) — catches `deny_unknown_fields` (e.g. a smuggled `tenant_id`) and missing-required-field errors |
/// | `BytesRejection` | 400 | `invalid_argument` (field: `body`) |
///
/// DESIGN §3.3's category table pins `invalid_argument` at 400; we use that
/// even for `JsonDataError` (semantic body-validation) rather than the 422
/// DNA `STATUS_CODES.md` would suggest, so the catalog speaks a single
/// status code for every body-shape rejection (matches the rest of
/// analytics-api).
fn json_rejection_to_response(rej: &JsonRejection) -> Response {
    match rej {
        JsonRejection::MissingJsonContentType(_) => unsupported_media_type_response(),
        JsonRejection::JsonSyntaxError(e) => {
            tracing::debug!(error = %e, "catalog: JSON syntax error");
            invalid_body_response("body", "request body must be valid JSON")
        }
        JsonRejection::JsonDataError(e) => {
            // `JsonDataError` covers `deny_unknown_fields` (e.g. body-supplied
            // `tenant_id`), missing required fields, and type mismatches. The
            // `Display` impl includes a serde path that points at the offending
            // field — useful for clients debugging request shape. Logged at
            // debug only; the canonical envelope carries the same diagnostic.
            let detail = e.to_string();
            tracing::debug!(error = %detail, "catalog: JSON data error");
            invalid_body_response("body", "request body did not match the expected schema")
        }
        JsonRejection::BytesRejection(e) => {
            tracing::debug!(error = %e, "catalog: request body could not be read");
            invalid_body_response("body", "request body could not be read")
        }
        // `JsonRejection` is `#[non_exhaustive]` — future variants surface as a
        // generic 400 invalid_argument so a new Axum version doesn't degrade
        // to a non-canonical default rejection shape.
        _ => invalid_body_response("body", "request body rejected by extractor"),
    }
}

/// 400 `invalid_argument` envelope for body-deserialization failures. Uses the
/// `MetricCatalogError` resource type so consumers see the catalog GTS
/// namespace per DESIGN §3.3.
fn invalid_body_response(field: &'static str, description: &'static str) -> Response {
    MetricCatalogError::invalid_argument()
        .with_field_violation(field, description, "INVALID")
        .create()
        .into_response()
}

/// 415 response with a catalog-local `unsupported_media_type` GTS category.
///
/// `modkit-canonical-errors` v0.7.3 has no `unsupported_media_type` variant
/// (`CanonicalError::status_code` maps every category to a fixed HTTP status,
/// and 415 isn't in the set). DNA `REST/STATUS_CODES.md` §15 still requires
/// 415 with `Content-Type` problem semantics, so we construct the `Problem`
/// directly:
///
/// - `type` URI follows the canonical envelope shape
///   (`gts://gts.cf.core.errors.err.v1~cf.core.err.unsupported_media_type.v1~`)
///   — matches the conventions in [`CanonicalError::gts_type`] so consumers
///   that branch on `type` see a category whose name agrees with the HTTP
///   `status`.
/// - `context.resource_type` keeps the catalog GTS namespace
///   (`gts.cf.insight.metric_catalog.metric.v1~`) so the error is recognizable
///   as coming from this surface.
/// - `context.precondition_violations[]` carries the specific constraint that
///   failed, mirroring `failed_precondition`'s context shape — clients that
///   already render `precondition_violations` keep working.
///
/// When `cf-modkit-canonical-errors` grows an `UnsupportedMediaType` variant,
/// swap this for the standard builder.
fn unsupported_media_type_response() -> Response {
    let problem = Problem {
        problem_type: "gts://gts.cf.core.errors.err.v1~cf.core.err.unsupported_media_type.v1~"
            .to_owned(),
        title: "Unsupported Media Type".to_owned(),
        status: StatusCode::UNSUPPORTED_MEDIA_TYPE.as_u16(),
        detail: "Content-Type: application/json required".to_owned(),
        instance: None,
        trace_id: None,
        context: json!({
            "resource_type": "gts.cf.insight.metric_catalog.metric.v1~",
            "precondition_violations": [
                {
                    "type": "content_type",
                    "subject": "Content-Type",
                    "description": "request must use Content-Type: application/json"
                }
            ]
        }),
    };
    let body = serde_json::to_vec(&problem).unwrap_or_else(|e| {
        // Serializing a `Problem` with a `json!` context cannot fail in
        // practice; fall back to a minimal envelope so we never serve a
        // garbled 415.
        tracing::error!(error = %e, "catalog: failed to serialize 415 Problem; using fallback");
        br#"{"type":"gts://gts.cf.core.errors.err.v1~cf.core.err.unsupported_media_type.v1~","title":"Unsupported Media Type","status":415,"detail":"Content-Type: application/json required","context":{}}"#.to_vec()
    });
    (
        StatusCode::UNSUPPORTED_MEDIA_TYPE,
        [(header::CONTENT_TYPE, "application/problem+json")],
        body,
    )
        .into_response()
}

#[cfg(test)]
mod tests {
    //! Wire-shape coverage for the handler. End-to-end through-router coverage
    //! with a live MariaDB + Redis lives in `domain/catalog/live_tests.rs` and
    //! `infra/cache/live_tests.rs`.

    use axum::body::to_bytes;

    use super::*;

    #[tokio::test]
    async fn unsupported_media_type_response_has_problem_json_content_type()
    -> Result<(), Box<dyn std::error::Error>> {
        let resp = unsupported_media_type_response();
        assert_eq!(resp.status(), StatusCode::UNSUPPORTED_MEDIA_TYPE);
        assert_eq!(
            resp.headers()
                .get(header::CONTENT_TYPE)
                .and_then(|v| v.to_str().ok()),
            Some("application/problem+json"),
            "wire shape MUST be RFC 9457 Problem Details"
        );
        let bytes = to_bytes(resp.into_body(), 16 * 1024).await?;
        let body: serde_json::Value = serde_json::from_slice(&bytes)?;
        assert_eq!(body["status"], 415);
        assert_eq!(
            body["type"], "gts://gts.cf.core.errors.err.v1~cf.core.err.unsupported_media_type.v1~",
            "type URI MUST agree with the HTTP status (not failed_precondition)"
        );
        assert_eq!(body["title"], "Unsupported Media Type");
        assert_eq!(
            body["context"]["resource_type"],
            "gts.cf.insight.metric_catalog.metric.v1~"
        );
        let violations = body["context"]["precondition_violations"]
            .as_array()
            .ok_or("precondition_violations must be an array")?;
        assert_eq!(violations.len(), 1);
        assert_eq!(violations[0]["type"], "content_type");
        assert_eq!(violations[0]["subject"], "Content-Type");
        Ok(())
    }

    #[tokio::test]
    async fn invalid_body_response_is_400_invalid_argument()
    -> Result<(), Box<dyn std::error::Error>> {
        let resp = invalid_body_response("body", "bad body");
        assert_eq!(resp.status(), 400);
        assert_eq!(
            resp.headers()
                .get(header::CONTENT_TYPE)
                .and_then(|v| v.to_str().ok()),
            Some("application/problem+json")
        );
        let bytes = to_bytes(resp.into_body(), 16 * 1024).await?;
        let body: serde_json::Value = serde_json::from_slice(&bytes)?;
        assert_eq!(
            body["type"],
            "gts://gts.cf.core.errors.err.v1~cf.core.err.invalid_argument.v1~"
        );
        assert_eq!(body["status"], 400);
        assert_eq!(
            body["context"]["resource_type"],
            "gts.cf.insight.metric_catalog.metric.v1~"
        );
        let violations = body["context"]["field_violations"]
            .as_array()
            .ok_or("field_violations must be an array")?;
        assert_eq!(violations.len(), 1);
        assert_eq!(violations[0]["field"], "body");
        assert_eq!(violations[0]["reason"], "INVALID");
        Ok(())
    }

    // ── End-to-end JsonRejection coverage ──────────────────────────────
    //
    // These tests drive the canonical-envelope mapper through Axum's real
    // `Json<T>` extractor by manufacturing requests against a minimal route
    // that mirrors the production handler's body-extraction signature.
    // They prove the contract Axum + serde + our mapper deliver on the wire,
    // not just the helpers in isolation.

    use axum::Router;
    use axum::body::Body;
    use axum::http::{Request, header::CONTENT_TYPE};
    use axum::routing::post;
    use tower::ServiceExt;

    /// Test-only echo route — drives `JsonRejection` through the same mapper
    /// production uses. We can't mount `get_metrics` itself without standing
    /// up the full `AppState`, but the body-extraction layer is what we want
    /// to verify here.
    async fn echo_or_reject(json: Result<Json<GetMetricsRequest>, JsonRejection>) -> Response {
        match json {
            Ok(Json(_)) => StatusCode::OK.into_response(),
            Err(rej) => json_rejection_to_response(&rej),
        }
    }

    fn test_router() -> Router {
        Router::new().route("/echo", post(echo_or_reject))
    }

    async fn body_json(resp: Response) -> Result<serde_json::Value, Box<dyn std::error::Error>> {
        let bytes = to_bytes(resp.into_body(), 64 * 1024).await?;
        Ok(serde_json::from_slice(&bytes)?)
    }

    #[tokio::test]
    async fn missing_content_type_returns_415_canonical_envelope()
    -> Result<(), Box<dyn std::error::Error>> {
        // No `Content-Type` header at all → Axum's `Json<T>` rejects with
        // `MissingJsonContentType` → our mapper returns 415 with the
        // canonical `unsupported_media_type` envelope.
        let req = Request::builder()
            .method("POST")
            .uri("/echo")
            .body(Body::from(r"{}"))?;
        let resp = test_router().oneshot(req).await?;
        assert_eq!(resp.status(), StatusCode::UNSUPPORTED_MEDIA_TYPE);
        assert_eq!(
            resp.headers()
                .get(CONTENT_TYPE)
                .and_then(|v| v.to_str().ok()),
            Some("application/problem+json")
        );
        let body = body_json(resp).await?;
        assert_eq!(body["status"], 415);
        assert_eq!(
            body["type"],
            "gts://gts.cf.core.errors.err.v1~cf.core.err.unsupported_media_type.v1~"
        );
        Ok(())
    }

    #[tokio::test]
    async fn wrong_content_type_returns_415_canonical_envelope()
    -> Result<(), Box<dyn std::error::Error>> {
        // `Content-Type: application/x-www-form-urlencoded` (CSRF closure
        // path) → Axum rejects with `MissingJsonContentType` (the extractor
        // requires the JSON-class content type specifically).
        let req = Request::builder()
            .method("POST")
            .uri("/echo")
            .header(CONTENT_TYPE, "application/x-www-form-urlencoded")
            .body(Body::from("role_slug=eng"))?;
        let resp = test_router().oneshot(req).await?;
        assert_eq!(resp.status(), StatusCode::UNSUPPORTED_MEDIA_TYPE);
        let body = body_json(resp).await?;
        assert_eq!(body["status"], 415);
        Ok(())
    }

    #[tokio::test]
    async fn deny_unknown_fields_body_returns_400_invalid_argument()
    -> Result<(), Box<dyn std::error::Error>> {
        // A body that smuggles `tenant_id` → serde fails (`deny_unknown_fields`)
        // → `JsonDataError` → 400 `invalid_argument`. This is the
        // cross-tenant-disclosure defense at the parser layer.
        let req = Request::builder()
            .method("POST")
            .uri("/echo")
            .header(CONTENT_TYPE, "application/json")
            .body(Body::from(
                r#"{"tenant_id":"11111111-1111-1111-1111-111111111111"}"#,
            ))?;
        let resp = test_router().oneshot(req).await?;
        assert_eq!(resp.status(), 400);
        let body = body_json(resp).await?;
        assert_eq!(
            body["type"],
            "gts://gts.cf.core.errors.err.v1~cf.core.err.invalid_argument.v1~"
        );
        assert_eq!(body["status"], 400);
        Ok(())
    }

    #[tokio::test]
    async fn malformed_json_returns_400_invalid_argument() -> Result<(), Box<dyn std::error::Error>>
    {
        // Syntactically broken JSON → `JsonSyntaxError` → 400.
        let req = Request::builder()
            .method("POST")
            .uri("/echo")
            .header(CONTENT_TYPE, "application/json")
            .body(Body::from(r"{not json"))?;
        let resp = test_router().oneshot(req).await?;
        assert_eq!(resp.status(), 400);
        let body = body_json(resp).await?;
        assert_eq!(
            body["type"],
            "gts://gts.cf.core.errors.err.v1~cf.core.err.invalid_argument.v1~"
        );
        Ok(())
    }

    #[tokio::test]
    async fn valid_json_passes_through_to_handler() -> Result<(), Box<dyn std::error::Error>> {
        // Sanity check: a well-formed body is NOT rejected by the mapper —
        // the echo route returns 200. Empty `{}` and a populated body both
        // round-trip cleanly through `deny_unknown_fields` + `serde::default`.
        for body in [r"{}", r#"{"role_slug":"eng","team_id":"alpha"}"#] {
            let req = Request::builder()
                .method("POST")
                .uri("/echo")
                .header(CONTENT_TYPE, "application/json")
                .body(Body::from(body))?;
            let resp = test_router().oneshot(req).await?;
            assert_eq!(resp.status(), 200, "body {body:?} must pass extractor");
        }
        Ok(())
    }

    #[tokio::test]
    async fn charset_parameter_on_content_type_is_accepted()
    -> Result<(), Box<dyn std::error::Error>> {
        // `application/json; charset=utf-8` is what browsers and stdlib HTTP
        // clients commonly send — Axum's `Json<T>` accepts it.
        let req = Request::builder()
            .method("POST")
            .uri("/echo")
            .header(CONTENT_TYPE, "application/json; charset=utf-8")
            .body(Body::from(r"{}"))?;
        let resp = test_router().oneshot(req).await?;
        assert_eq!(resp.status(), 200);
        Ok(())
    }
}
