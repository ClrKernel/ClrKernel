using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClrKernel.Jobs;

/// <summary>
/// What you have run, and what you have kept.
/// <para>
/// Its own group rather than more routes under <c>/connections/{id}</c>, because
/// neither belongs to one connection: history is what <em>you</em> ran, wherever you
/// ran it, and a saved query outlives the connection it was written against. The
/// per-connection history stays where it is — that one is an audit of a database,
/// and it is a different question.
/// </para>
/// </summary>
public static class QueriesApi {
    public static void MapQueriesApi(this IEndpointRouteBuilder app) {
        var api = app.MapGroup("/api/queries");

        // Yours, across every connection. The store decides which rows that is: a
        // private connection's are its actor's alone, so this route does not get to
        // ask for anybody else's even by mistake.
        api.MapGet("/history", async (HttpContext context, IRunStore runs, int? limit) => {
            if (context.CurrentUser() is not { } user) {
                return Unauthorized();
            }
            var history = await runs.QueryAuditAsync(new QueryAuditQuery {
                ViewerId = user.Id,
                // Deliberately not the admin view. This is "what did I run", and an
                // admin looking at everybody's belongs on the connection they are
                // auditing, where the question is about that database.
                ViewerIsAdmin = false,
                Limit = Math.Clamp(limit ?? 100, 1, 500),
            });
            return Results.Ok(new { history });
        });

        api.MapGet("/", async (HttpContext context, IRunStore runs) => {
            if (context.CurrentUser() is not { } user) {
                return Unauthorized();
            }
            var queries = await runs.SavedQueriesAsync(new SavedQueryFilter { ViewerId = user.Id });
            return Results.Ok(new { queries = queries.Select(q => View(q, context)) });
        });

        api.MapPost("/", async (HttpContext context, IRunStore runs, SavedQueryBody body) => {
            if (context.CurrentUser() is not { } user) {
                return Unauthorized();
            }
            if (string.IsNullOrWhiteSpace(body?.Name)) {
                return Results.BadRequest(new { error = "A saved query needs a name." });
            }
            if (string.IsNullOrWhiteSpace(body.Sql)) {
                return Results.BadRequest(new { error = "There is nothing to save." });
            }

            var existing = body.Id == null ? null : await runs.SavedQueryAsync(body.Id.Value, user.Id);
            if (body.Id != null && existing == null) {
                return NoSuchQuery(body.Id.Value);
            }
            // Fixed at creation, like a connection's: moving a private query into the
            // shared list would publish it on a dropdown change, and moving one out
            // would take it away from everybody who had found it.
            var scope = existing?.Scope ?? (Shared(body.Scope) ? "shared" : "private");
            if (Refusal(context, scope, existing?.OwnerId ?? user.Id) is { } refusal) {
                return refusal;
            }

            var saved = new SavedQuery {
                Id = existing?.Id ?? Guid.NewGuid(),
                Name = body.Name.Trim(),
                Scope = scope,
                OwnerId = scope == "private" ? existing?.OwnerId ?? user.Id : null,
                ConnectionId = Trimmed(body.ConnectionId),
                ConnectionName = Trimmed(body.ConnectionName),
                Sql = body.Sql,
                CreatedBy = existing?.CreatedBy ?? user.Id,
                CreatedByName = existing?.CreatedByName ?? user.DisplayName,
                CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            await runs.SaveQueryAsync(saved);
            return Results.Ok(View(saved, context));
        });

        api.MapDelete("/{id:guid}", async (HttpContext context, IRunStore runs, Guid id) => {
            if (context.CurrentUser() is not { } user) {
                return Unauthorized();
            }
            var existing = await runs.SavedQueryAsync(id, user.Id);
            if (existing == null) {
                return NoSuchQuery(id);
            }
            if (Refusal(context, existing.Scope, existing.OwnerId) is { } refusal) {
                return refusal;
            }
            await runs.DeleteSavedQueryAsync(id);
            return Results.Ok(new { removed = id });
        });
    }

    /// <summary>
    /// Who may write this one: a server admin for anything shared, and only you for
    /// your own. The same rule connections follow, in the same shape, because they
    /// are used together and two rules would be one too many to remember.
    /// </summary>
    private static IResult Refusal(HttpContext context, string scope, Guid? owner) {
        if (scope == "shared") {
            return context.IsAdmin()
                ? null
                : Results.Json(new { error = "Only a server admin manages shared queries." },
                    statusCode: 403);
        }
        return owner == context.CurrentUser()?.Id
            ? null
            : Results.Json(new { error = "That is not your query." }, statusCode: 403);
    }

    private static object View(SavedQuery query, HttpContext context) => new {
        query.Id,
        query.Name,
        query.Scope,
        query.ConnectionId,
        query.ConnectionName,
        query.Sql,
        query.CreatedByName,
        query.UpdatedAt,
        // Whether this reader may change it, so nothing offers a button that refuses.
        canEdit = query.Scope != "shared" || context.IsAdmin(),
    };

    private static bool Shared(string scope) =>
        string.Equals(scope, "shared", StringComparison.OrdinalIgnoreCase);

    private static string Trimmed(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult Unauthorized() =>
        Results.Json(new { error = "Sign in first." }, statusCode: 401);

    private static IResult NoSuchQuery(Guid id) =>
        Results.NotFound(new { error = $"No saved query '{id}'." });
}

public sealed class SavedQueryBody {
    /// <summary>Null to create; an existing id to replace it.</summary>
    public Guid? Id { get; set; }

    public string Name { get; set; }
    public string Scope { get; set; }
    public string ConnectionId { get; set; }
    public string ConnectionName { get; set; }
    public string Sql { get; set; }
}
