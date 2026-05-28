using Microsoft.Extensions.Configuration;

namespace Insight.Identity.Infrastructure.ClickHouse;

/// <summary>
/// ClickHouse connection options for the identity_inputs reader. Bound
/// from configuration under <c>clickhouse</c>. Either <see cref="Url"/>
/// (<c>http://user:pass@host:port/database</c>) or the discrete
/// host/port/user/password/database fields must be set.
/// </summary>
public sealed class ClickHouseOptions
{
    public const string SectionName = "clickhouse";

    /// <summary>
    /// Full URL form, parity with the Python seed-script
    /// <c>CLICKHOUSE_URL</c> env var. When set, the discrete
    /// host/port/user/password fields are ignored.
    /// </summary>
    public string? Url { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? User { get; init; }

    public string? Password { get; init; }

    public string? Database { get; init; }
}
