using Microsoft.AspNetCore.OutputCaching;

namespace OfficeAschiApi.Caching;

/// <summary>
/// Adds a dynamic "team-{id}" output cache tag based on route values.
/// This enables granular eviction — mutating team 5's data only evicts team-5 cache entries,
/// leaving all other teams' cached responses intact.
/// </summary>
public sealed class TeamScopedTagPolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken ct)
    {
        var routeValues = context.HttpContext.Request.RouteValues;

        // Controllers use either {teamId} or {id} for the team identifier
        if (routeValues.TryGetValue("teamId", out var teamId))
            context.Tags.Add($"team-{teamId}");
        else if (routeValues.TryGetValue("id", out var id))
            context.Tags.Add($"team-{id}");

        return ValueTask.CompletedTask;
    }
}
