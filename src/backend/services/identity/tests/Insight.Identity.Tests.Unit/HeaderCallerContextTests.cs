using FluentAssertions;
using Insight.Identity.Api.Auth;
using Insight.Identity.Domain;
using Insight.Identity.Domain.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Insight.Identity.Tests.Unit;

public sealed class HeaderCallerContextTests
{
    private static readonly Guid CallerId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task Returns_parsed_guid_when_header_present()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[HeaderCallerContext.HeaderName] = CallerId.ToString();

        var resolved = await NewSut().ResolveAsync(context, CancellationToken.None);

        resolved.Should().Be(CallerId);
    }

    [Fact]
    public async Task Returns_null_when_header_missing_and_no_jwt_claims()
    {
        var context = new DefaultHttpContext();

        var resolved = await NewSut().ResolveAsync(context, CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("33333333-3333-3333-3333")]
    public async Task Returns_null_when_header_value_is_not_a_guid(string raw)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[HeaderCallerContext.HeaderName] = raw;

        var resolved = await NewSut().ResolveAsync(context, CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task Rejects_guid_empty()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[HeaderCallerContext.HeaderName] = Guid.Empty.ToString();

        // Guid.Empty is parseable but is not a real identity — accepting it
        // would promote `00000000-…` to a valid caller and pollute the
        // audit trail. JWT-fallback also returns null with no claims set.
        var resolved = await NewSut().ResolveAsync(context, CancellationToken.None);

        resolved.Should().BeNull();
    }

    private static HeaderCallerContext NewSut()
        => new(new NullReader(), NullLogger<HeaderCallerContext>.Instance);

    private sealed class NullReader : IPersonsReader
    {
        public Task<Guid?> ResolvePersonIdByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken)
            => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyList<PersonObservation>> GetLatestObservationsAsync(Guid tenantId, Guid personId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PersonObservation>>(Array.Empty<PersonObservation>());
        public Task<IReadOnlyList<OrgChartEdge>> GetCurrentParentsAsync(Guid tenantId, Guid childPersonId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OrgChartEdge>>(Array.Empty<OrgChartEdge>());
        public Task<IReadOnlyList<OrgChartEdge>> GetCurrentChildrenAsync(Guid tenantId, Guid parentPersonId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OrgChartEdge>>(Array.Empty<OrgChartEdge>());
        public Task<IReadOnlyList<Guid>> ResolvePersonIdsByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
        public Task<IReadOnlyList<Guid>> ResolvePersonIdsBySourceIdAsync(Guid tenantId, string sourceType, Guid sourceId, string value, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
        public Task<IReadOnlyList<PersonSourceId>> GetCurrentSourceIdsAsync(Guid tenantId, Guid personId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PersonSourceId>>(Array.Empty<PersonSourceId>());
        public Task<Guid?> ResolvePersonIdByAccountIdAsync(string accountId, CancellationToken cancellationToken)
            => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyList<Guid>> ResolvePersonIdsByEmailAcrossTenantsAsync(string email, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
    }
}
