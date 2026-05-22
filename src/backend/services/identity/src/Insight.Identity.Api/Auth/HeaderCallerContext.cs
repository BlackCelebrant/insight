using Microsoft.AspNetCore.Http;

namespace Insight.Identity.Api.Auth;

/// <summary>
/// Reads <c>X-Insight-Person-Id</c> from the request. api-gateway sets
/// the header from the validated JWT subject; downstream services treat
/// it as the authenticated caller id.
/// </summary>
public sealed class HeaderCallerContext : ICallerContext
{
    public const string HeaderName = "X-Insight-Person-Id";

    public Guid? Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.Headers.TryGetValue(HeaderName, out var raw)
            && Guid.TryParse(raw.ToString(), out var personId))
        {
            return personId;
        }
        return null;
    }
}
