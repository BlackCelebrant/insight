using System.Globalization;
using Microsoft.Extensions.Options;
using Octonica.ClickHouseClient;

namespace Insight.Identity.Infrastructure.ClickHouse;

/// <summary>
/// Builds opened <see cref="ClickHouseConnection"/> instances from
/// <see cref="ClickHouseOptions"/>. Mirrors the
/// <c>MariaDbConnectionFactory</c> shape so caller code can dispose
/// via <c>await using</c>.
/// </summary>
public sealed class ClickHouseConnectionFactory
{
    private readonly IOptions<ClickHouseOptions> _options;
    private readonly Lazy<string> _connectionString;

    public ClickHouseConnectionFactory(IOptions<ClickHouseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        // Build lazily: the factory is constructed for every service
        // start (the persons-seed worker depends on it), but ClickHouse
        // config is only required when a seed actually runs. Deferring
        // the build keeps environments that never seed — including the
        // test host — from failing at DI time on missing CH options.
        _connectionString = new Lazy<string>(() => BuildConnectionString(_options.Value));
    }

    public string ConnectionString => _connectionString.Value;

    /// <summary>Sanitised "host:port/database" for diagnostics — no creds.</summary>
    public string Target
    {
        get
        {
            var b = new ClickHouseConnectionStringBuilder(_connectionString.Value);
            return $"{b.Host}:{b.Port}/{b.Database}";
        }
    }

    public async Task<ClickHouseConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new ClickHouseConnection(_connectionString.Value);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string BuildConnectionString(ClickHouseOptions options)
    {
        var builder = new ClickHouseConnectionStringBuilder();
        if (!string.IsNullOrWhiteSpace(options.Url))
        {
            ApplyUrl(builder, options.Url);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.Host))
            {
                throw new ArgumentException(
                    "clickhouse options must set either 'url' or 'host' (+ port/user/password/database)",
                    nameof(options));
            }
            builder.Host = options.Host;
            if (options.Port is { } port) builder.Port = (ushort)port;
            if (options.User is not null) builder.User = options.User;
            if (options.Password is not null) builder.Password = options.Password;
            if (options.Database is not null) builder.Database = options.Database;
        }
        return builder.ConnectionString;
    }

    private static void ApplyUrl(ClickHouseConnectionStringBuilder builder, string url)
    {
        // Accept both `http(s)://user:pass@host:port/db` and bare
        // `clickhouse://user:pass@host:port/db` shapes — strip the
        // scheme and parse the rest by hand. Octonica's builder does
        // not accept HTTP URLs directly.
        var trimmed = url;
        var schemeIdx = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0) trimmed = trimmed[(schemeIdx + 3)..];

        string? userPass = null;
        var atIdx = trimmed.IndexOf('@');
        if (atIdx >= 0)
        {
            userPass = trimmed[..atIdx];
            trimmed  = trimmed[(atIdx + 1)..];
        }

        string? database = null;
        var slashIdx = trimmed.IndexOf('/');
        if (slashIdx >= 0)
        {
            database = trimmed[(slashIdx + 1)..];
            trimmed  = trimmed[..slashIdx];
        }

        var hostPort = trimmed.Split(':', 2);
        builder.Host = Uri.UnescapeDataString(hostPort[0]);
        if (hostPort.Length == 2 && ushort.TryParse(hostPort[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            builder.Port = port;
        }
        if (userPass is not null)
        {
            var creds = userPass.Split(':', 2);
            builder.User = Uri.UnescapeDataString(creds[0]);
            if (creds.Length == 2) builder.Password = Uri.UnescapeDataString(creds[1]);
        }
        if (database is not null) builder.Database = database;
    }
}
