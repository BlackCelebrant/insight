using FluentAssertions;
using Insight.Identity.Domain.Services;
using Xunit;

namespace Insight.Identity.Tests.Unit;

public sealed class ValueRoutingTests
{
    [Theory]
    [InlineData("id")]
    [InlineData("email")]
    [InlineData("username")]
    [InlineData("employee_id")]
    [InlineData("parent_email")]
    [InlineData("parent_id")]
    [InlineData("parent_person_id")]
    public void Routes_identifier_types_to_value_id(string valueType)
    {
        var (valueId, valueFullText, value) = ValueRouting.Route(valueType, "x");
        valueId.Should().Be("x");
        valueFullText.Should().BeNull();
        value.Should().BeNull();
    }

    [Theory]
    [InlineData("display_name")]
    [InlineData("first_name")]
    [InlineData("last_name")]
    [InlineData("department")]
    [InlineData("division")]
    [InlineData("job_title")]
    [InlineData("status")]
    public void Routes_attribute_types_to_value_full_text(string valueType)
    {
        var (valueId, valueFullText, value) = ValueRouting.Route(valueType, "x");
        valueId.Should().BeNull();
        valueFullText.Should().Be("x");
        value.Should().BeNull();
    }

    [Fact]
    public void Routes_unknown_type_to_value_text()
    {
        var (valueId, valueFullText, value) = ValueRouting.Route("functional_team", "x");
        valueId.Should().BeNull();
        valueFullText.Should().BeNull();
        value.Should().Be("x");
    }

    [Fact]
    public void Rejects_oversized_value_id()
    {
        var oversized = new string('a', ValueRouting.MaxValueIdLen + 1);
        var (valueId, valueFullText, value) = ValueRouting.Route("email", oversized);
        valueId.Should().BeNull();
        valueFullText.Should().BeNull();
        value.Should().BeNull();
    }

    [Fact]
    public void Rejects_oversized_value_full_text()
    {
        var oversized = new string('a', ValueRouting.MaxValueFullTextLen + 1);
        var (valueId, valueFullText, value) = ValueRouting.Route("display_name", oversized);
        valueId.Should().BeNull();
        valueFullText.Should().BeNull();
        value.Should().BeNull();
    }

    [Fact]
    public void Does_not_cap_text_column()
    {
        var huge = new string('a', 10_000);
        var (_, _, value) = ValueRouting.Route("functional_team", huge);
        value.Should().Be(huge);
    }
}
