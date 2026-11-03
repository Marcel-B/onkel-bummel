using OnkelBummel.Api.Auth;
using OnkelBummel.Api.Data;

namespace OnkelBummel.Api.Endpoints;

/// <param name="Topics">Topic names, or ["*"] for all.</param>
public sealed record CreatePublishTokenRequest(string? Name, List<string>? Topics);

public static class PublishTokenEndpoints
{
    public static RouteGroupBuilder MapPublishTokenEndpoints(this RouteGroupBuilder api)
    {
        var tokens = api.MapGroup("/publish-tokens").RequireAuthorization(Policies.User);

        tokens.MapGet("/", async (OnkelBummelDbContext db, CancellationToken cancellationToken) =>
            TypedResults.Ok(await PublishTokens.ListAsync(db, cancellationToken)));

        tokens.MapPost("/", async (CreatePublishTokenRequest request, OnkelBummelDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
            {
                var (topics, error) = PublishTokens.Validate(request.Name, request.Topics);
                if (topics is null)
                    return Problems.BadRequest(error!);
                var created = await PublishTokens.CreateAsync(db, request.Name!, topics, time, cancellationToken);
                return TypedResults.Created($"/api/v1/publish-tokens/{created.Id}", created);
            })
            .WithSummary("Create a token; the answer is the only time it is shown");

        tokens.MapDelete("/{id}", async (string id, OnkelBummelDbContext db, CancellationToken cancellationToken) =>
            await PublishTokens.DeleteAsync(db, id, cancellationToken)
                ? TypedResults.NoContent()
                : Problems.NotFound($"Publish-Token {id} gibt es nicht."));

        return api;
    }
}
