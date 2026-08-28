using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClrKernel.Studio;

/// <summary>
/// Saved connections: the list, the form's schema, and CRUD.
/// <para>
/// Not under <c>/api/projects/{project}</c>, because connections are not
/// project-scoped — one server, one list, shared entries and each person's own.
/// The scoping rules are the whole authorization model here: shared is
/// Server-Admin-managed and world-readable, private is invisible to everybody but
/// its owner, and no route lets one turn into the other by accident.
/// </para>
/// </summary>
public static class ConnectionsApi {
    public static void MapConnectionsApi(this IEndpointRouteBuilder app) {
        var api = app.MapGroup("/api/connections");

        // The settings schema the one generic form renders — same descriptor shape
        // the notebook connection wizard already renders, so a provider describes
        // itself once and both surfaces follow.
        api.MapGet("/providers", async (
            HttpContext context, ConnectionStore store, JobsOptions options,
            ConnectionProviderCatalog catalog, CancellationToken cancellationToken) =>
            context.CurrentUser() == null
                ? Unauthorized()
                : Results.Ok(new {
                    providers = (await catalog.GetAsync(cancellationToken)).Select(ProviderView.From),
                    // The form needs this to know whether a *private* connection also
                    // needs a least-privilege login to be runnable — otherwise it
                    // renders no field for one and the connection can never run.
                    privateConnectionsReadOnly = options.PrivateConnectionsReadOnly,
                    // The form asks for a password only where one can be kept.
                    canPersistSecrets = store.CanPersistSecrets,
                    secretHelp = store.CanPersistSecrets
                        ? null
                        : "This server has nowhere to keep a password, so it has no credential "
                            + "store to put one in. Give the name of a secret and set the matching "
                            + "CLRKERNEL_SECRET_* variable — or give the server a keyring, which "
                            + "in the container image means supplying its password at start "
                            + "(CLRKERNEL_STUDIO_KEYRING_PASSWORD_FILE).",
                }));

        api.MapGet("/", (HttpContext context, ConnectionStore store, JobsOptions options) => {
            if (context.CurrentUser() is not { } user) {
                return Unauthorized();
            }
            return Results.Ok(new {
                connections = store.VisibleTo(user).Select(c => ConnectionView.From(c, store, context, options)),
            });
        });

        api.MapGet("/{id}", (HttpContext context, ConnectionStore store, JobsOptions options, string id) => {
            if (context.CurrentUser() is not { } user) {
                return Unauthorized();
            }
            return store.Find(id, user) is { } found
                ? Results.Ok(ConnectionView.From(found, store, context, options))
                : NoSuchConnection(id);
        });

        api.MapPost("/", async (
                HttpContext context, ConnectionStore store, JobsOptions options,
                ConnectionMaterializer files, ConnectionProviderCatalog catalog,
                ConnectionBody body, CancellationToken cancellationToken) => {
                    // Awaited here so a type the kernel offers is accepted the first time
                    // somebody saves one, rather than only after something else has probed.
                    await catalog.GetAsync(cancellationToken);
                    return Save(context, store, options, files, catalog, id: null, body);
                });

        api.MapPut("/{id}", async (
                HttpContext context, ConnectionStore store, JobsOptions options,
                ConnectionMaterializer files, ConnectionProviderCatalog catalog, string id,
                ConnectionBody body, CancellationToken cancellationToken) => {
                    await catalog.GetAsync(cancellationToken);
                    return Save(context, store, options, files, catalog, id, body);
                });

        // --- execution ------------------------------------------------------

        // Before it exists. A connection that does not answer is one you probably do
        // not want saved, so this writes nothing — no row, no secret, no file sync.
        api.MapPost("/test", async (
            HttpContext context, ConnectionStore store, QueryRunner runner, JobsOptions options,
            ConnectionProviderCatalog catalog, ConnectionBody body,
            CancellationToken cancellationToken) => {
                await catalog.GetAsync(cancellationToken);
                if (Draft(context, store, options, catalog, id: null, body, out var draft) is { } refusal) {
                    return refusal;
                }
                if (!ConnectionProviderCatalog.IsQueryable(draft.Type)) {
                    return NotOpenedHere(draft.Type);
                }
                // The password has to be the one on screen. A *reference* could name a
                // secret this server holds for somebody else, and the read-only rule
                // exists precisely so that a non-admin cannot open a connection as a
                // privileged login of their choosing — which an unsaved draft, pointed
                // at any host, would otherwise be a way to do.
                if (Restricted(context, options, draft) && string.IsNullOrEmpty(body.Password)) {
                    return Results.Json(new {
                        error = "Type the password to test this. This server holds you to a "
                            + "read-only login, so it will not open a connection using a secret "
                            + "it already stores for something else.",
                    }, statusCode: 403);
                }
                var failure = await runner.TestAsync(
                    draft, leastPrivilege: false, body.Password, cancellationToken);
                return Results.Ok(new { ok = failure == null, error = failure });
            });

        api.MapPost("/{id}/test", async (
            HttpContext context, ConnectionStore store, QueryRunner runner, JobsOptions options,
            string id, TestBody body, CancellationToken cancellationToken) => {
                if (Executable(context, store, options, id, out var connection, out var refusal) is false) {
                    return refusal;
                }
                var error = await runner.TestAsync(
                    connection, LeastPrivilege(context, options, connection), body?.Password, cancellationToken);
                return Results.Ok(new { ok = error == null, error });
            });

        api.MapPost("/{id}/query", async (
            HttpContext context, ConnectionStore store, QueryRunner runner, IRunStore runs,
            JobsOptions options, string id, QueryBody body, CancellationToken cancellationToken) => {
                if (Executable(context, store, options, id, out var connection, out var refusal) is false) {
                    return refusal;
                }
                var sql = (body?.Sql ?? string.Empty).Trim();
                if (sql.Length == 0) {
                    return Results.BadRequest(new { error = "There is nothing to run." });
                }
                var user = context.CurrentUser();
                var leastPrivilege = LeastPrivilege(context, options, connection);
                // Defence in depth, and only that: the read-only login is what
                // actually refuses a write. This catches the plain cases early so the
                // answer is a sentence rather than a permissions error, and it is
                // deliberately not trusted for anything — see StatementCheck.
                if (leastPrivilege && StatementCheck.WriteVerbIn(sql) is { } verb) {
                    return Results.Json(
                        new { error = StatementCheck.Refusal(verb) }, statusCode: 403);
                }
                // The client names the query so it can cancel it, and a client-chosen id
                // is fine because cancelling checks who started it, not who knows the id.
                var queryId = Trimmed(body.QueryId) ?? Guid.NewGuid().ToString("N");
                var startedAt = DateTime.UtcNow;

                var result = await runner.RunAsync(
                    connection, sql, leastPrivilege, user.Id, queryId, body.Password, cancellationToken);

                // Every execution, private connections included — that is what makes a
                // personal history worth having. The scope rides along, and it is what
                // decides who may ever read the row back: a private one is its actor's
                // alone, admins included.
                await runs.RecordQueryAsync(new QueryAudit {
                    Id = Guid.NewGuid(),
                    ConnectionId = connection.Id,
                    ConnectionName = connection.Name,
                    ActorId = user.Id,
                    ActorName = user.DisplayName,
                    StartedAt = startedAt,
                    DurationMs = result.ElapsedMs,
                    Statement = sql,
                    LeastPrivilege = leastPrivilege,
                    Outcome = result.Canceled ? "Cancelled" : result.Error == null ? "Succeeded" : "Failed",
                    RowsAffected = result.RowsAffected,
                    ErrorSummary = result.Error,
                    Scope = connection.Scope == ConnectionScope.Shared ? "shared" : "private",
                });
                return Results.Ok(new {
                    queryId,
                    result.ResultSets,
                    result.Messages,
                    result.RowsAffected,
                    result.ElapsedMs,
                    result.Canceled,
                    result.Error,
                });
            });

        api.MapPost("/{id}/cancel", (
            HttpContext context, ConnectionStore store, QueryRunner runner, JobsOptions options,
            string id, CancelBody body) => {
                if (Executable(context, store, options, id, out _, out var refusal) is false) {
                    return refusal;
                }
                return Results.Ok(new { cancelled = runner.Cancel(body?.QueryId, context.CurrentUser().Id) });
            });

        // --- the object tree ------------------------------------------------

        // One route with a level rather than four near-identical ones, and a POST
        // rather than a GET: a prompt-every-session connection carries its password in
        // the body, and a password in a query string is a password in the access log.
        api.MapPost("/{id}/metadata", async (
            HttpContext context, ConnectionStore store, QueryRunner runner, JobsOptions options,
            string id, MetadataBody body, CancellationToken cancellationToken) => {
                if (Executable(context, store, options, id, out var connection, out var refusal) is false) {
                    return refusal;
                }
                if (!ConnectionProviderCatalog.IsQueryable(connection.Type)
                || !SqlServerMetadata.Supports(connection.Type)) {
                    // Degrade rather than error: a provider this process cannot open is
                    // still a connection worth having in the list, and the tree shows it as
                    // a leaf instead of a folder that opens onto nothing.
                    return Results.Ok(new {
                        supported = false,
                        reason = $"{connection.Type} connections cannot be browsed from here.",
                    });
                }
                var leastPrivilege = LeastPrivilege(context, options, connection);
                var level = (body?.Level ?? "databases").ToLowerInvariant();
                var (payload, error) = await runner.BrowseAsync<object>(
                    connection, leastPrivilege, body?.Password,
                    async (live, token) => level switch {
                        "databases" => new { nodes = await SqlServerMetadata.DatabasesAsync(live, token) },
                        "schemas" => new {
                            nodes = await SqlServerMetadata.SchemasAsync(live, body.Database, token),
                        },
                        "objects" => new {
                            nodes = await SqlServerMetadata.ObjectsAsync(live, body.Database, body.Schema, token),
                        },
                        "detail" => (object)await SqlServerMetadata.DetailAsync(
                            live, body.Database, body.Schema, body.Object, token),
                        "completions" => (object)await SqlServerMetadata.CompletionsAsync(
                            live, body.Database, token),
                        "script" => new {
                            script = await SqlServerMetadata.ScriptAsync(
                                live, body.Database, body.Schema, body.Object, body.Kind, body.Variant,
                                token),
                        },
                        _ => throw new ConnectionException($"No metadata level '{level}'."),
                    },
                    cancellationToken);
                return error == null
                    ? Results.Ok(new { supported = true, payload })
                    : Results.Ok(new { supported = true, error });
            });

        // Disconnect is a real thing rather than a UI state: it drops the pooled
        // sockets. The tree forgets what it had loaded at the same time, so "connected"
        // and "we have its objects" stay the same fact.
        api.MapPost("/{id}/disconnect", (
            HttpContext context, ConnectionStore store, QueryRunner runner, JobsOptions options,
            string id) => {
                if (Executable(context, store, options, id, out var connection, out var refusal) is false) {
                    return refusal;
                }
                runner.Disconnect(connection, LeastPrivilege(context, options, connection));
                return Results.Ok(new { disconnected = connection.Id });
            });

        // Shared connections only — a private one is somebody's own credential
        // against a server they could reach with SSMS anyway, and logging it would be
        // surveillance rather than audit.
        api.MapGet("/{id}/history", async (
            HttpContext context, ConnectionStore store, IRunStore runs, string id) => {
                if (context.CurrentUser() is not { } user) {
                    return Unauthorized();
                }
                var connection = store.Find(id, user);
                if (connection == null) {
                    return NoSuchConnection(id);
                }
                // Who may see which rows is the store's rule, not this route's — an
                // admin reads everybody's against a shared connection, and nobody but
                // its actor reads one against a private connection.
                var history = await runs.QueryAuditAsync(new QueryAuditQuery {
                    ConnectionId = connection.Id,
                    ViewerId = user.Id,
                    ViewerIsAdmin = context.IsAdmin(),
                });
                return Results.Ok(new { history });
            });

        api.MapDelete("/{id}", (
                HttpContext context, ConnectionStore store, ConnectionMaterializer files, string id) => {
                    if (context.CurrentUser() is not { } user) {
                        return Unauthorized();
                    }
                    var found = store.Find(id, user);
                    if (found == null) {
                        return NoSuchConnection(id);
                    }
                    if (Refusal(context, found.Scope, found.OwnerId) is { } refusal) {
                        return refusal;
                    }
                    store.Remove(found.Id);
                    files.Sync();
                    return Results.Ok(new { removed = found.Id });
                });
    }

    /// <summary>
    /// Gives a worktree that has just been created its owner's private connections.
    /// <para>
    /// Resolved from the request rather than injected: the two places that create a
    /// worktree are deep inside the notebook routes, and threading a connections
    /// service through them to write one file would put connections in the signature
    /// of everything on the way down.
    /// </para>
    /// </summary>
    internal static void OnWorktreeCreated(HttpContext context, GitService git, Guid userId) =>
        (context.RequestServices.GetService(typeof(ConnectionMaterializer)) as ConnectionMaterializer)
            ?.SyncUser(git, userId);

    private static IResult Save(
        HttpContext context, ConnectionStore store, JobsOptions options, ConnectionMaterializer files,
        ConnectionProviderCatalog catalog, string id, ConnectionBody body) {
        if (Draft(context, store, options, catalog, id, body, out var entry) is { } denied) {
            return denied;
        }
        try {
            var saved = store.Save(entry, body.Password, body.ReadOnlyPassword);
            // On the way out, not on a timer: a connection somebody just saved has to
            // be resolvable by the next notebook that names it.
            files.Sync();
            return Results.Ok(ConnectionView.From(saved, store, context, options));
        } catch (ConnectionException e) {
            return Results.BadRequest(new { error = e.Message });
        } catch (SecretNotFoundException e) {
            return Results.BadRequest(new { error = e.Message });
        }
    }

    /// <summary>
    /// What this body would become, and whether this caller may make it — everything
    /// <see cref="Save"/> does except writing it down. Testing takes the same path so
    /// that "may I test this" cannot drift from "may I save this"; it returns the
    /// refusal, or null with <paramref name="entry"/> set.
    /// </summary>
    private static IResult Draft(
        HttpContext context, ConnectionStore store, JobsOptions options,
        ConnectionProviderCatalog catalog, string id, ConnectionBody body,
        out StoredConnection entry) {
        entry = null;
        if (context.CurrentUser() is not { } user) {
            return Unauthorized();
        }
        if (body == null) {
            return Results.BadRequest(new { error = "A connection body is required." });
        }
        var existing = id == null ? null : store.Find(id, user);
        if (id != null && existing == null) {
            return NoSuchConnection(id);
        }

        var scope = existing?.Scope ?? ParseScope(body.Scope);
        // Which list an entry lives in is fixed when it is created. Moving a private
        // connection into the shared list would publish somebody's credential to the
        // whole server on a dropdown change; moving one out would silently break every
        // notebook naming it.
        if (existing != null && body.Scope != null && ParseScope(body.Scope) != existing.Scope) {
            return Results.BadRequest(new {
                error = "A connection cannot change between shared and private. Create a new one.",
            });
        }
        var owner = existing?.OwnerId ?? (scope == ConnectionScope.Private ? user.Id : (Guid?)null);
        if (Refusal(context, scope, owner) is { } refusal) {
            return refusal;
        }
        if (catalog.Find(body.Type) == null) {
            return Results.BadRequest(new { error = $"No connection type '{body.Type}'." });
        }

        entry = new StoredConnection {
            Id = existing?.Id,
            Name = body.Name,
            Scope = scope,
            OwnerId = owner,
            Type = body.Type,
            Settings = Clean(body.Settings),
            SecretRef = Trimmed(body.SecretRef) ?? existing?.SecretRef,
            PromptForPassword = body.PromptForPassword ?? existing?.PromptForPassword ?? false,
            ReadOnlyUser = Trimmed(body.ReadOnlyUser),
            ReadOnlySecretRef = Trimmed(body.ReadOnlySecretRef) ?? existing?.ReadOnlySecretRef,
            TimeoutSeconds = body.TimeoutSeconds ?? existing?.TimeoutSeconds ?? 30,
            RowCap = body.RowCap ?? existing?.RowCap ?? 10_000,
            CreatedBy = existing?.CreatedBy ?? user.Id,
        };
        return null;
    }

    /// <summary>
    /// Who may write this connection: a Server Admin for anything shared, and only
    /// you for your own. One function, because "who owns this" is asked by create,
    /// update and delete, and three copies is three chances to differ.
    /// </summary>
    private static IResult Refusal(HttpContext context, ConnectionScope scope, Guid? owner) {
        if (scope == ConnectionScope.Shared) {
            return context.IsAdmin()
                ? null
                : Results.Json(new { error = "Only a server admin manages shared connections." },
                    statusCode: 403);
        }
        return owner == context.CurrentUser()?.Id
            ? null
            // Unreachable through Find, which already hides other people's — but the
            // check is here rather than assumed, because that is one route away from
            // being wrong.
            : Results.Json(new { error = "That is not your connection." }, statusCode: 403);
    }

    /// <summary>
    /// Resolves the connection and decides whether this caller may run against it.
    /// One function for test, query and cancel: three copies of an authorization
    /// check is three chances for one of them to be wrong.
    /// </summary>
    private static bool Executable(
        HttpContext context, ConnectionStore store, JobsOptions options, string id,
        out StoredConnection connection, out IResult refusal) {
        connection = null;
        if (context.CurrentUser() is not { } user) {
            refusal = Unauthorized();
            return false;
        }
        connection = store.Find(id, user);
        if (connection == null) {
            refusal = NoSuchConnection(id);
            return false;
        }
        if (!ConnectionProviderCatalog.IsQueryable(connection.Type)) {
            refusal = NotOpenedHere(connection.Type);
            return false;
        }
        if (Restricted(context, options, connection) && !store.HasReadOnlyLogin(connection)) {
            refusal = Results.Json(new {
                error = connection.Scope == ConnectionScope.Private
                    ? "This server requires a read-only login on every connection. Add one to this "
                        + "connection to run against it."
                    : "Read-only execution is not configured on this connection, so only a "
                        + "server admin can run against it.",
            }, statusCode: 403);
            return false;
        }
        refusal = null;
        return true;
    }

    /// <summary>
    /// Whether this execution uses the least-privilege login. Everyone below a server
    /// admin does, on a shared connection — that credential is the read-only boundary,
    /// because reading the SQL to decide whether it writes loses to <c>EXEC</c>,
    /// <c>SELECT … INTO</c> and dynamic SQL.
    /// </summary>
    private static bool LeastPrivilege(
        HttpContext context, JobsOptions options, StoredConnection connection) =>
        Restricted(context, options, connection);

    /// <summary>
    /// Whether this caller is held to the read-only rule on this connection: always
    /// on a shared one, and on a private one too where the install has asked for it.
    /// </summary>
    private static bool Restricted(
        HttpContext context, JobsOptions options, StoredConnection connection) =>
        !context.IsAdmin()
        && (connection.Scope == ConnectionScope.Shared || options.PrivateConnectionsReadOnly);

    /// <summary>
    /// Saving one is the point — a notebook can name it and the kernel opens it there.
    /// Opening it *here* would build a SQL Server connection out of an Oracle
    /// connection's settings and dial it, which is worse than refusing.
    /// </summary>
    private static IResult NotOpenedHere(string type) =>
        Results.Json(new {
            error = $"{type} connections are opened by the kernel, not by this server, so they "
                + "cannot be browsed or queried here. A notebook can still use this connection "
                + "by name.",
        }, statusCode: 400);

    private static IResult Unauthorized() =>
        Results.Json(new { error = "Sign in first." }, statusCode: 401);

    private static IResult NoSuchConnection(string id) =>
        Results.NotFound(new { error = $"No connection '{id}'." });

    private static ConnectionScope ParseScope(string scope) =>
        string.Equals(scope, "shared", StringComparison.OrdinalIgnoreCase)
            ? ConnectionScope.Shared
            : ConnectionScope.Private;

    private static string Trimmed(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Drops blank settings so an untouched optional field is absent rather
    /// than an empty string the provider would treat as a value.</summary>
    private static Dictionary<string, string> Clean(Dictionary<string, string> settings) {
        var cleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings ?? new Dictionary<string, string>()) {
            if (!string.IsNullOrWhiteSpace(pair.Value)) {
                cleaned[pair.Key] = pair.Value.Trim();
            }
        }
        return cleaned;
    }
}

/// <summary>A password for a prompt-every-session connection, supplied for this call
/// only and never stored.</summary>
public sealed class TestBody {
    public string Password { get; set; }
}

public sealed class QueryBody {
    public string Sql { get; set; }

    /// <summary>What Cancel will name. The client picks it so it can cancel before the
    /// response it would have learned the id from arrives.</summary>
    public string QueryId { get; set; }

    public string Password { get; set; }
}

public sealed class CancelBody {
    public string QueryId { get; set; }
}

/// <summary>Which level of the object tree is being opened, and where.</summary>
public sealed class MetadataBody {
    /// <summary>databases | schemas | objects | detail | script | completions.</summary>
    public string Level { get; set; }

    public string Database { get; set; }
    public string Schema { get; set; }
    public string Object { get; set; }

    /// <summary>Only for <c>script</c>: a table has no stored definition and one is
    /// generated, everything else has one on the server.</summary>
    public string Kind { get; set; }

    /// <summary>Only for <c>script</c>: create | drop | select | insert | update |
    /// delete | execute. Defaults to create.</summary>
    public string Variant { get; set; }

    public string Password { get; set; }
}

/// <summary>What a create or update sends. Passwords come in and are never sent back.</summary>
public sealed class ConnectionBody {
    public string Name { get; set; }
    public string Scope { get; set; }
    public string Type { get; set; }
    public Dictionary<string, string> Settings { get; set; }

    /// <summary>Stored in the OS credential store under <see cref="SecretRef"/>, then
    /// discarded. Never persisted in any file and never echoed.</summary>
    public string Password { get; set; }

    /// <summary>An existing credential-store key to use instead of saving a password —
    /// the only option where the server has no writable store.</summary>
    public string SecretRef { get; set; }

    public bool? PromptForPassword { get; set; }
    public string ReadOnlyUser { get; set; }
    public string ReadOnlyPassword { get; set; }
    public string ReadOnlySecretRef { get; set; }
    public int? TimeoutSeconds { get; set; }
    public int? RowCap { get; set; }
}

/// <summary>A connection as the browser sees it: settings yes, secrets never.</summary>
public sealed class ConnectionView {
    public string Id { get; set; }
    public string Name { get; set; }
    public string Scope { get; set; }
    public string Type { get; set; }
    public Dictionary<string, string> Settings { get; set; }

    /// <summary>Whether a password exists — the only thing said about one.</summary>
    public bool SecretConfigured { get; set; }

    public string SecretRef { get; set; }
    public bool PromptForPassword { get; set; }
    public string ReadOnlyUser { get; set; }
    public bool ReadOnlySecretConfigured { get; set; }

    /// <summary>
    /// Whether this caller may execute against it. False with a reason rather than
    /// an Execute button that fails: a shared connection with no least-privilege
    /// login configured refuses everyone below Server Admin, because the app cannot
    /// make a writable login read-only by inspecting the SQL sent through it.
    /// </summary>
    public bool CanExecute { get; set; }

    public string CanExecuteReason { get; set; }

    /// <summary>Whether this server can open it at all, as opposed to only storing
    /// it for a notebook to name.</summary>
    public bool Queryable { get; set; }

    public bool CanEdit { get; set; }
    public int TimeoutSeconds { get; set; }
    public int RowCap { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static ConnectionView From(
        StoredConnection c, ConnectionStore store, HttpContext context, JobsOptions options) {
        var admin = context.IsAdmin();
        var mine = c.Scope == ConnectionScope.Private;
        var readOnlyConfigured = store.HasReadOnlyLogin(c);
        // The same rules the execution routes apply, asked here so the button agrees
        // with the server rather than being disabled by a second opinion.
        var queryable = ConnectionProviderCatalog.IsQueryable(c.Type);
        var restricted = !admin && (!mine || options.PrivateConnectionsReadOnly);
        var (canExecute, reason) =
            !queryable
                ? (false, $"{c.Type} connections are opened by the kernel rather than by this "
                    + "server, so they cannot be browsed or queried here — a notebook can still "
                    + "name this one.")
            : !restricted ? (true, (string)null)
            : readOnlyConfigured ? (true, null)
            : mine
                ? (false, "This server requires a read-only login on every connection. "
                    + "Add one to this connection to run against it.")
                : (false, "Read-only execution is not configured on this connection, so only a "
                    + "server admin can run against it. An admin can add a least-privilege login.");
        return new ConnectionView {
            Id = c.Id,
            Name = c.Name,
            Scope = c.Scope.ToString().ToLowerInvariant(),
            Type = c.Type,
            Settings = new Dictionary<string, string>(c.Settings, StringComparer.OrdinalIgnoreCase),
            SecretConfigured = store.HasSecret(c.SecretRef),
            SecretRef = c.SecretRef,
            PromptForPassword = c.PromptForPassword,
            ReadOnlyUser = c.ReadOnlyUser,
            ReadOnlySecretConfigured = readOnlyConfigured,
            CanExecute = canExecute,
            CanExecuteReason = reason,
            Queryable = queryable,
            CanEdit = mine || admin,
            TimeoutSeconds = c.TimeoutSeconds,
            RowCap = c.RowCap,
            UpdatedAt = c.UpdatedAt,
        };
    }
}

/// <summary>A provider descriptor flattened for the form. Mirrors the payload the
/// notebook connection wizard already consumes, minus the directive half — nothing
/// here composes a cell.</summary>
public sealed class ProviderView {
    public string Type { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }

    /// <summary>Whether this server can open the connection itself. False means it
    /// can be saved and named by a notebook — which is the point of saving it — but
    /// not browsed or queried from the Connections area.</summary>
    public bool Queryable { get; set; }

    public IReadOnlyList<SettingView> Settings { get; set; }

    public static ProviderView From(ConnectionProviderDescriptor d) => new() {
        Type = d.Type,
        DisplayName = d.DisplayName,
        Description = d.Description,
        Queryable = ConnectionProviderCatalog.IsQueryable(d.Type),
        // `name` is dropped: a provider declares it because the connect directive
        // carries it, but here the name is the connection's identity — what a
        // notebook references — and the store owns it. Leaving it in gives the form
        // two name fields that disagree.
        Settings = d.Settings
            .Where(s => !s.RuntimeOnly && !string.Equals(s.Name, "name", StringComparison.OrdinalIgnoreCase))
            .Select(SettingView.From).ToList(),
    };
}

public sealed class SettingView {
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public string Kind { get; set; }
    public bool Required { get; set; }
    public string OneOfGroup { get; set; }
    public IReadOnlyList<string> EnumValues { get; set; }
    public IReadOnlyList<string> CredentialValues { get; set; }
    public IReadOnlyList<string> Requires { get; set; }
    public string Default { get; set; }
    public string Description { get; set; }

    public static SettingView From(ConnectionSetting s) => new() {
        Name = s.Name,
        DisplayName = s.DisplayName,
        Kind = s.Kind.ToString(),
        Required = s.Required,
        OneOfGroup = s.OneOfGroup,
        EnumValues = s.EnumValues,
        CredentialValues = s.CredentialValues,
        Requires = s.Requires,
        Default = s.Default,
        Description = s.Description,
    };
}
