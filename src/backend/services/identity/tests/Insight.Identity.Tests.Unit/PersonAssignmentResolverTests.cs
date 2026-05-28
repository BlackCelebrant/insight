using FluentAssertions;
using Insight.Identity.Domain.Services;
using Xunit;

namespace Insight.Identity.Tests.Unit;

public sealed class PersonAssignmentResolverTests
{
    private static readonly Guid SourceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid KnownPerson = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EmailPerson = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MintedPerson = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static SeedProfile Profile(string accountId, string? email, bool closed = false)
        => new(
            new SourceAccountKey("bamboohr", SourceId, accountId),
            Observations: Array.Empty<IdentityInputRow>(),
            LatestEmail: email,
            IsClosed: closed);

    private static Func<Guid> Mint() => () => MintedPerson;

    [Fact]
    public void Reuses_known_account_binding_over_email()
    {
        var group = new ProfileGroup(new[] { Profile("acc-1", "a@x.io") });
        var known = new Dictionary<SourceAccountKey, Guid>
        {
            [new SourceAccountKey("bamboohr", SourceId, "acc-1")] = KnownPerson,
        };
        var emails = new Dictionary<string, Guid> { ["a@x.io"] = EmailPerson };

        var result = PersonAssignmentResolver.Resolve(new[] { group }, known, emails, Mint());

        result.Assignments.Should().ContainSingle();
        result.Assignments[0].PersonId.Should().Be(KnownPerson);
        result.Assignments[0].Kind.Should().Be(AssignmentKind.ReusedKnown);
        result.ReusedKnown.Should().Be(1);
    }

    [Fact]
    public void Links_unknown_account_to_existing_email_person()
    {
        var group = new ProfileGroup(new[] { Profile("acc-1", "a@x.io") });
        var emails = new Dictionary<string, Guid> { ["a@x.io"] = EmailPerson };

        var result = PersonAssignmentResolver.Resolve(
            new[] { group }, new Dictionary<SourceAccountKey, Guid>(), emails, Mint());

        result.Assignments[0].PersonId.Should().Be(EmailPerson);
        result.Assignments[0].Kind.Should().Be(AssignmentKind.LinkedByEmail);
        result.LinkedByEmail.Should().Be(1);
    }

    [Fact]
    public void Mints_new_person_for_unknown_active_account_with_no_email_match()
    {
        var group = new ProfileGroup(new[] { Profile("acc-1", "new@x.io") });

        var result = PersonAssignmentResolver.Resolve(
            new[] { group },
            new Dictionary<SourceAccountKey, Guid>(),
            new Dictionary<string, Guid>(),
            Mint());

        result.Assignments[0].PersonId.Should().Be(MintedPerson);
        result.Assignments[0].Kind.Should().Be(AssignmentKind.Minted);
        result.MintedNew.Should().Be(1);
    }

    [Fact]
    public void Skips_closed_account_with_no_email_match()
    {
        var group = new ProfileGroup(new[] { Profile("acc-1", "gone@x.io", closed: true) });

        var result = PersonAssignmentResolver.Resolve(
            new[] { group },
            new Dictionary<SourceAccountKey, Guid>(),
            new Dictionary<string, Guid>(),
            Mint());

        result.Assignments.Should().BeEmpty();
        result.SkippedClosed.Should().Be(1);
    }

    [Fact]
    public void Links_closed_account_when_email_matches()
    {
        // Closed account is mint-blocked but link is still allowed.
        var group = new ProfileGroup(new[] { Profile("acc-1", "a@x.io", closed: true) });
        var emails = new Dictionary<string, Guid> { ["a@x.io"] = EmailPerson };

        var result = PersonAssignmentResolver.Resolve(
            new[] { group }, new Dictionary<SourceAccountKey, Guid>(), emails, Mint());

        result.Assignments[0].PersonId.Should().Be(EmailPerson);
        result.Assignments[0].Kind.Should().Be(AssignmentKind.LinkedByEmail);
        result.LinkedByEmail.Should().Be(1);
    }

    [Fact]
    public void Skips_group_with_no_email()
    {
        var group = new ProfileGroup(new[] { Profile("acc-1", email: null) });

        var result = PersonAssignmentResolver.Resolve(
            new[] { group },
            new Dictionary<SourceAccountKey, Guid>(),
            new Dictionary<string, Guid>(),
            Mint());

        result.Assignments.Should().BeEmpty();
        result.SkippedNoEmail.Should().Be(1);
    }

    [Fact]
    public void Two_accounts_sharing_email_get_one_minted_person()
    {
        // EmailProfileResolver groups them; resolver mints once for the group.
        var group = new ProfileGroup(new[] { Profile("acc-1", "shared@x.io"), Profile("acc-2", "shared@x.io") });

        var result = PersonAssignmentResolver.Resolve(
            new[] { group },
            new Dictionary<SourceAccountKey, Guid>(),
            new Dictionary<string, Guid>(),
            Mint());

        result.Assignments.Should().ContainSingle();
        result.Assignments[0].PersonId.Should().Be(MintedPerson);
        result.Assignments[0].Profiles.Should().HaveCount(2);
        result.MintedNew.Should().Be(2);
    }
}
