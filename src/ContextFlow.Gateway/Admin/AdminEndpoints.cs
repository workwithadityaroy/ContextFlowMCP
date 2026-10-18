using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace ContextFlow.Gateway.Admin;

/// <summary>
/// Composition extension that mounts all Admin REST API endpoints under <c>/api</c>
/// protected by the <c>Admin</c> role requirement.
/// </summary>
public static class AdminEndpoints
{
    public const string AdminPolicyName = "AdminOnly";

    public static RouteGroupBuilder MapAdminApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints
            .MapGroup("/api")
            .RequireAuthorization(static policy => policy.RequireRole("Admin"));

        group.MapAdminLogsEndpoints();
        group.MapAdminToolsEndpoints();
        group.MapAdminTokensEndpoints();
        group.MapAdminMetricsEndpoints();

        return group;
    }
}

