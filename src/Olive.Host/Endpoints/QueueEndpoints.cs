using Microsoft.AspNetCore.Authorization;
using Olive.Application;
using Olive.Contracts;
using Olive.Domain;
using Olive.Host.Auth;
using Olive.Host.Problems;

namespace Olive.Host.Endpoints;

public static class QueueEndpoints
{
    // D-01/D-03: reviewer list, get, claim and release. There is deliberately no create and no withdraw route in Phase 5.
    public static IEndpointRouteBuilder MapQueueEndpoints(this IEndpointRouteBuilder app)
    {
        // D-01: one policy scheme picks DevReviewer or ServiceToken by header shape. Create, withdraw and poll attach ProducerOnly in plans 04 and 06.
        var group = app.MapGroup("/v1/queue/items")
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = QueueCaller.SchemeName });
        group.MapPost("", CreateAsync).AddEndpointFilter(QueueCallerFilters.ProducerOnly);
        group.MapGet("", ListAsync).AddEndpointFilter(QueueCallerFilters.ReviewerOnly);
        group.MapGet("/{id:guid}", GetAsync).AddEndpointFilter(QueueCallerFilters.ReviewerOnly);
        group.MapGet("/{id:guid}/payload", PayloadAsync).AddEndpointFilter(QueueCallerFilters.ReviewerOnly);
        group.MapGet("/{id:guid}/events", EventsAsync).AddEndpointFilter(QueueCallerFilters.ReviewerOnly);
        group.MapPost("/{id:guid}/claim", ClaimAsync).AddEndpointFilter(QueueCallerFilters.ReviewerOnly);
        group.MapPost("/{id:guid}/release", ReleaseAsync).AddEndpointFilter(QueueCallerFilters.ReviewerOnly);
        group.MapPost("/{id:guid}/decision", DecisionAsync).AddEndpointFilter(QueueCallerFilters.ReviewerOnly);
        return app;
    }

    private static readonly HashSet<string> ServerAssigned = new(StringComparer.Ordinal)
    {
        "tenant_id", "producer", "item_id", "state", "decision", "decided_by", "decided_at", "rationale", "claimed_by", "claimed_at",
        "auth_source", "withdrawn_reason", "expires_at", "created_at", "proposal_id",
    };

    private static readonly string[] CreateFields = ["kind", "subtype", "producer_ref", "subject_ref", "payload_ref", "ruleset_version"];

    // IRQ-01: tenant and producer come only from the verified token (ProducerOnly filter). The body is hashed exactly as parsed (RFC 8785).
    private static async Task<IResult> CreateAsync(HttpContext http, QueueService service, CancellationToken ct)
    {
        var invalid = ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.InvalidRequest);
        if (!http.Request.HasJsonContentType()) { return invalid; }
        var bytes = await ProposalEndpoints.ReadBoundedAsync(http.Request, ct);
        if (bytes is null) { return ProblemResults.Result(StatusCodes.Status413PayloadTooLarge, ProblemReasons.PayloadTooLarge); }

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(bytes, new System.Text.Json.JsonDocumentOptions { MaxDepth = JsonGate.MaxDepth, AllowDuplicateProperties = false });
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            return invalid;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !JsonGate.Walk(root)) { return invalid; }
            if (root.EnumerateObject().Any(p => ServerAssigned.Contains(p.Name)))
            {
                return ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.ServerAssignedField);
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
            {
                if (!CreateFields.Contains(p.Name, StringComparer.Ordinal) || p.Value.ValueKind != System.Text.Json.JsonValueKind.String) { return invalid; }
                values[p.Name] = p.Value.GetString()!;
            }

            if (values.Count != CreateFields.Length) { return invalid; }

            var producer = (ProducerContext)http.Items[QueueCallerFilters.ProducerItemKey]!;
            var hash = Jcs.Sha256Hex(Jcs.Canonicalize(root));
            var request = new CreateItemRequest(values["kind"], values["subtype"], values["producer_ref"], values["subject_ref"], values["payload_ref"], values["ruleset_version"]);
            var result = await service.CreateAsync(producer, request, hash, ct);
            return result switch
            {
                CreateResult.Created c => Results.Json(new CreateItemResponse(c.ItemId, c.State), statusCode: StatusCodes.Status201Created),
                CreateResult.Replay r => Results.Ok(new CreateItemResponse(r.ItemId, r.State)),
                CreateResult.Conflict => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.IdempotencyConflict),
                CreateResult.InvalidRequest => invalid,
                _ => throw new InvalidOperationException("Unknown create result."),
            };
        }
    }

    private static async Task<IResult> ListAsync(HttpContext http, QueueService service, string? kind, string? state, string? limit, CancellationToken ct)
    {
        int? parsed = null;
        if (limit is not null)
        {
            if (!int.TryParse(limit, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var l))
            {
                return ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.InvalidRequest);
            }

            parsed = l;
        }

        var reviewer = http.User.ToReviewerContext()!;
        var result = await service.ListAsync(reviewer, kind, state, parsed, ct);
        return result switch
        {
            QueueListResult.Ok ok => Results.Ok(new QueueItemListResponse(ok.Items.Select(ToDto).ToList())),
            _ => ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.InvalidRequest),
        };
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
    {
        var item = await service.GetAsync(http.User.ToReviewerContext()!, id, ct);
        return item is null
            ? ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound)
            : Results.Ok(ToDto(item));
    }

    // Each result switch has an explicit default arm: an unmapped future subtype fails with a static message, not a SwitchExpressionException.
    private static async Task<IResult> PayloadAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
    {
        var result = await service.GetPayloadAsync(http.User.ToReviewerContext()!, id, ct);
        return result switch
        {
            PayloadResult.Found f => Results.Ok(new ReviewerPayloadResponse(f.Payload.ActionType, ParseJson(f.Payload.ParametersJson), f.Payload.Justification, f.Payload.OnBehalfOf, f.Payload.ExpiresAt, f.Payload.TraceRef)),
            PayloadResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
            PayloadResult.RoleNotPermitted => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.RoleNotPermitted),
            _ => throw new InvalidOperationException("Unknown payload result."),
        };
    }

    private static async Task<IResult> EventsAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
    {
        var result = await service.EventsAsync(http.User.ToReviewerContext()!, id, ct);
        return result switch
        {
            EventsResult.Found f => Results.Ok(new QueueItemEventsResponse(f.Events.Select(e => new QueueItemEventDto(e.Seq, e.EventType, e.FromState, e.ToState, e.Actor, e.CreatedAt, ParseJson(e.DetailJson))).ToList())),
            EventsResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
            _ => throw new InvalidOperationException("Unknown events result."),
        };
    }

    private static async Task<IResult> ClaimAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
    {
        var result = await service.ClaimAsync(http.User.ToReviewerContext()!, id, ct);
        return result switch
        {
            ClaimResult.Claimed c => Results.Ok(ToDto(service.ToView(c.Item))),
            ClaimResult.AlreadyClaimed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.AlreadyClaimed),
            ClaimResult.ProposalExpired => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ProposalExpired),
            ClaimResult.Closed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ItemClosed),
            ClaimResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
            _ => throw new InvalidOperationException("Unknown claim result."),
        };
    }

    private static async Task<IResult> ReleaseAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
    {
        var result = await service.ReleaseAsync(http.User.ToReviewerContext()!, id, ct);
        return result switch
        {
            ReleaseResult.Released r => Results.Ok(ToDto(service.ToView(r.Item))),
            ReleaseResult.NotClaimant => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.NotClaimant),
            ReleaseResult.NotClaimed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ItemNotClaimed),
            ReleaseResult.ProposalExpired => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ProposalExpired),
            ReleaseResult.Closed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ItemClosed),
            ReleaseResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
            _ => throw new InvalidOperationException("Unknown release result."),
        };
    }

    // APR-01: only decision and rationale are read from the body. Identity, tenant and auth source come from the authenticated claim.
    private static async Task<IResult> DecisionAsync(Guid id, HttpContext http, QueueService service, ExecutionService execution, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var invalid = ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.InvalidRequest);
        if (!http.Request.HasJsonContentType()) { return invalid; }
        var bytes = await ProposalEndpoints.ReadBoundedAsync(http.Request, ct);
        if (bytes is null) { return ProblemResults.Result(StatusCodes.Status413PayloadTooLarge, ProblemReasons.PayloadTooLarge); }

        string? decision = null;
        string? rationale = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(bytes, new System.Text.Json.JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) { return invalid; }
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != System.Text.Json.JsonValueKind.String) { return invalid; }
                switch (p.Name)
                {
                    case "decision": decision = p.Value.GetString(); break;
                    case "rationale": rationale = p.Value.GetString(); break;
                    default: return invalid;
                }
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            return invalid;
        }

        var result = await service.DecideAsync(http.User.ToReviewerContext()!, id, decision, rationale, ct);
        await ApprovalExecution.RunAsync(result, execution, loggerFactory);
        return result switch
        {
            DecisionResult.Decided d => Results.Ok(ToDto(service.ToView(d.Item))),
            DecisionResult.AlreadyDecidedSame d => Results.Ok(ToDto(service.ToView(d.Item))),
            DecisionResult.Conflict => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.DecisionConflict),
            DecisionResult.NotClaimant => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.NotClaimant),
            DecisionResult.NotClaimed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ItemNotClaimed),
            DecisionResult.Closed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ItemClosed),
            DecisionResult.ProposalExpired => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ProposalExpired),
            DecisionResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
            DecisionResult.RoleNotPermitted => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.RoleNotPermitted),
            DecisionResult.SelfApproval => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.SelfApprovalForbidden),
            DecisionResult.Unresolved => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.ApprovalIdentityUnresolved),
            DecisionResult.Unavailable => ProblemResults.Retryable(StatusCodes.Status503ServiceUnavailable, ProblemReasons.IdentityResolutionUnavailable),
            DecisionResult.KindNotSupported => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.KindNotSupported),
            DecisionResult.InvalidRequest => invalid,
            _ => throw new InvalidOperationException("Unknown decision result."),
        };
    }

    private static System.Text.Json.JsonElement? ParseJson(string? json)
    {
        if (json is null) { return null; }
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static QueueItemDto ToDto(QueueItemView v) => new(v.TenantId, v.ItemId, v.Kind, v.Subtype, v.Producer, v.ProducerRef, v.State,
        v.SubjectRef, v.PayloadRef, v.CreatedAt, v.AgeSeconds, v.ClaimedBy, v.ExpiresAt, v.Decision, v.DecidedBy, v.DecidedAt, v.AuthSource, v.WithdrawnReason);
}
