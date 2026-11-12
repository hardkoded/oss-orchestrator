using Microsoft.AspNetCore.Authorization;

namespace OssOrchestrator.Web;

public static class OwnerPolicy
{
    public const string Name = "Owner";
}

/// <summary>Requires the signed-in user to be in Auth:AllowedUsers (UPN or object id).</summary>
public sealed class AllowedUserRequirement : IAuthorizationRequirement;

public sealed class AllowedUserHandler(IConfiguration configuration) : AuthorizationHandler<AllowedUserRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AllowedUserRequirement requirement)
    {
        var allowed = configuration.GetSection("Auth:AllowedUsers").Get<string[]>() ?? [];
        var upn = context.User.FindFirst("preferred_username")?.Value;
        var oid = context.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

        if (allowed.Any(a => string.Equals(a, upn, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(a, oid, StringComparison.OrdinalIgnoreCase)))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}
