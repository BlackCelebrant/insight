using FluentAssertions;
using Insight.Identity.Domain.Services;
using Insight.Identity.Infrastructure.MariaDb;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Xunit;

namespace Insight.Identity.Tests.Integration;

/// <summary>
/// #346 step 1 — verifies the three new tables exist, the `admin` role
/// seed is in place, and the read-side ports return the expected rows
/// for inserted SCD2-style data.
/// </summary>
[Collection(MariaDbCollection.Name)]
public sealed class VisibilityRolesSchemaTests : IAsyncLifetime
{
    private static readonly Guid TenantId       = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid AlicePersonId  = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BobPersonId    = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AuthorPersonId = Guid.Empty;

    private readonly MariaDbFixture _fixture;
    private VisibilityRepository? _visibility;
    private RolesRepository? _roles;

    public VisibilityRolesSchemaTests(MariaDbFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync().ConfigureAwait(false);
        var factory = new MariaDbConnectionFactory(
            new OptionsWrapper<MariaDbOptions>(new MariaDbOptions { ConnectionString = _fixture.ConnectionString }));
        _visibility = new VisibilityRepository(factory);
        _roles = new RolesRepository(factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── roles ───────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_role_is_seeded_with_pinned_uuid()
    {
        var role = await _roles!.GetByNameAsync(Roles.AdminName, CancellationToken.None);
        role.Should().NotBeNull();
        role!.RoleId.Should().Be(Roles.Admin);
        role.Name.Should().Be("admin");
    }

    [Fact]
    public async Task ListAll_returns_at_least_the_admin_row()
    {
        var all = await _roles!.ListAllAsync(CancellationToken.None);
        all.Should().Contain(r => r.RoleId == Roles.Admin && r.Name == "admin");
    }

    // ── person_roles ────────────────────────────────────────────────

    [Fact]
    public async Task HasActiveRole_returns_false_when_no_assignment()
    {
        var has = await _roles!.HasActiveRoleAsync(TenantId, AlicePersonId, Roles.Admin, CancellationToken.None);
        has.Should().BeFalse();
    }

    [Fact]
    public async Task HasActiveRole_returns_true_after_insert_and_false_after_soft_delete()
    {
        var personRoleId = Guid.NewGuid();
        await InsertPersonRoleAsync(personRoleId, AlicePersonId, Roles.Admin, validTo: null).ConfigureAwait(false);

        var hasActive = await _roles!.HasActiveRoleAsync(TenantId, AlicePersonId, Roles.Admin, CancellationToken.None);
        hasActive.Should().BeTrue();

        // Soft-delete: set valid_to. The probe must now return false.
        await SoftDeletePersonRoleAsync(personRoleId).ConfigureAwait(false);
        var hasAfterRevoke = await _roles.HasActiveRoleAsync(TenantId, AlicePersonId, Roles.Admin, CancellationToken.None);
        hasAfterRevoke.Should().BeFalse();
    }

    [Fact]
    public async Task GetActiveByPerson_returns_only_currently_active_rows()
    {
        // Active row for Alice.
        var aliceActiveId = Guid.NewGuid();
        await InsertPersonRoleAsync(aliceActiveId, AlicePersonId, Roles.Admin, validTo: null).ConfigureAwait(false);
        // Revoked row for Alice — same role, but with valid_to set.
        var aliceRevokedId = Guid.NewGuid();
        await InsertPersonRoleAsync(aliceRevokedId, AlicePersonId, Roles.Admin, validTo: DateTime.UtcNow.AddMinutes(-5)).ConfigureAwait(false);
        // Different person — must not appear.
        await InsertPersonRoleAsync(Guid.NewGuid(), BobPersonId, Roles.Admin, validTo: null).ConfigureAwait(false);

        var active = await _roles!.GetActiveByPersonAsync(TenantId, AlicePersonId, CancellationToken.None);
        active.Should().ContainSingle().Which.PersonRoleId.Should().Be(aliceActiveId);
    }

    // ── visibility ──────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveGrantsByViewer_returns_empty_when_no_grants()
    {
        var grants = await _visibility!.GetActiveGrantsByViewerAsync(TenantId, AlicePersonId, CancellationToken.None);
        grants.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveGrantsByViewer_returns_whole_tree_grant_with_null_viewed()
    {
        var grantId = Guid.NewGuid();
        await InsertVisibilityAsync(grantId, AlicePersonId, viewedPersonId: null, validTo: null).ConfigureAwait(false);

        var grants = await _visibility!.GetActiveGrantsByViewerAsync(TenantId, AlicePersonId, CancellationToken.None);
        grants.Should().ContainSingle();
        grants[0].VisibilityId.Should().Be(grantId);
        grants[0].ViewerPersonId.Should().Be(AlicePersonId);
        grants[0].ViewedPersonId.Should().BeNull();
        grants[0].ValidTo.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveGrantsByViewer_excludes_revoked_grants()
    {
        var activeId = Guid.NewGuid();
        var revokedId = Guid.NewGuid();
        await InsertVisibilityAsync(activeId, AlicePersonId, viewedPersonId: BobPersonId, validTo: null).ConfigureAwait(false);
        await InsertVisibilityAsync(revokedId, AlicePersonId, viewedPersonId: BobPersonId, validTo: DateTime.UtcNow.AddMinutes(-1)).ConfigureAwait(false);

        var grants = await _visibility!.GetActiveGrantsByViewerAsync(TenantId, AlicePersonId, CancellationToken.None);
        grants.Should().ContainSingle().Which.VisibilityId.Should().Be(activeId);
    }

    [Fact]
    public async Task GetActiveGrantsByViewer_is_tenant_scoped()
    {
        var otherTenant = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        await InsertVisibilityAsync(Guid.NewGuid(), AlicePersonId, viewedPersonId: BobPersonId, validTo: null, tenantId: otherTenant).ConfigureAwait(false);

        var grants = await _visibility!.GetActiveGrantsByViewerAsync(TenantId, AlicePersonId, CancellationToken.None);
        grants.Should().BeEmpty();
    }

    // ── Seed helpers ────────────────────────────────────────────────

    private async Task InsertVisibilityAsync(
        Guid visibilityId,
        Guid viewerPersonId,
        Guid? viewedPersonId,
        DateTime? validTo,
        Guid? tenantId = null)
    {
        await using var conn = new MySqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        const string sql = """
            INSERT INTO visibility
                (visibility_id, insight_tenant_id, viewer_person_id, viewed_person_id,
                 valid_from, valid_to, author_person_id, reason)
            VALUES (@id, @tenant, @viewer, @viewed, UTC_TIMESTAMP(6), @valid_to, @author, '')
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", visibilityId.ToByteArray(bigEndian: true));
        cmd.Parameters.AddWithValue("@tenant", (tenantId ?? TenantId).ToByteArray(bigEndian: true));
        cmd.Parameters.AddWithValue("@viewer", viewerPersonId.ToByteArray(bigEndian: true));
        cmd.Parameters.AddWithValue("@viewed", viewedPersonId is { } v ? v.ToByteArray(bigEndian: true) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@valid_to", validTo is { } t ? t : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@author", AuthorPersonId.ToByteArray(bigEndian: true));
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task InsertPersonRoleAsync(
        Guid personRoleId,
        Guid personId,
        Guid roleId,
        DateTime? validTo)
    {
        await using var conn = new MySqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        const string sql = """
            INSERT INTO person_roles
                (person_role_id, insight_tenant_id, person_id, role_id,
                 valid_from, valid_to, author_person_id, reason)
            VALUES (@id, @tenant, @person, @role, UTC_TIMESTAMP(6), @valid_to, @author, '')
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", personRoleId.ToByteArray(bigEndian: true));
        cmd.Parameters.AddWithValue("@tenant", TenantId.ToByteArray(bigEndian: true));
        cmd.Parameters.AddWithValue("@person", personId.ToByteArray(bigEndian: true));
        cmd.Parameters.AddWithValue("@role", roleId.ToByteArray(bigEndian: true));
        cmd.Parameters.AddWithValue("@valid_to", validTo is { } t ? t : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@author", AuthorPersonId.ToByteArray(bigEndian: true));
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task SoftDeletePersonRoleAsync(Guid personRoleId)
    {
        await using var conn = new MySqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        const string sql = """
            UPDATE person_roles
            SET valid_to = UTC_TIMESTAMP(6)
            WHERE person_role_id = @id
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", personRoleId.ToByteArray(bigEndian: true));
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
