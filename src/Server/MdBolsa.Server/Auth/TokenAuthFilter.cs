using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MdBolsa.Server.Auth;

/// <summary>
/// The one token check, in one place, so the three endpoint groups cannot drift.
///
/// It was duplicated in each group in Phase 9, which worked right up until this
/// phase made the check do more than a string comparison: three copies of "is
/// this token allowed" is three places to forget that a device token is not
/// allowed to mint devices, and that kind of omission is invisible until it
/// matters.
/// </summary>
public static class TokenAuthFilter
{
    /// <summary>
    /// Where the authenticated caller is stashed for the rest of the request. Not
    /// used by anything yet - it exists so that adding an audit log later is a
    /// read, not a re-derivation of who was acting from headers that can be
    /// spoofed.
    /// </summary>
    public const string CallerKey = "mdbolsa.caller";

    // Spelled out rather than named: `EndpointFilter` is the *method* on
    // RouteGroupBuilder, not a delegate type, so there is nothing to return it as.
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> ForGroup(
        TokenAuthenticator auth) =>
        async (context, next) =>
        {
            var http = context.HttpContext;
            var presented = http.Request.Headers["X-MdBolsa-Token"].ToString();

            var caller = await auth.VerifyAsync(presented, http.RequestAborted);
            if (caller is null) return Results.Unauthorized();

            http.Items[CallerKey] = caller;
            return await next(context);
        };

    /// <summary>The caller of the current request, or null if it got this far unauthenticated.</summary>
    public static AuthenticatedCaller? CallerOf(HttpContext http) =>
        http.Items[CallerKey] as AuthenticatedCaller;
}
