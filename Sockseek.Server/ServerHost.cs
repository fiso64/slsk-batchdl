using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockseek.Api;
using Sockseek.Core.Chat;
using Sockseek.Core.Services;
using Sockseek.Server.Persistence;

namespace Sockseek.Server;

public static class ServerHost
{
    public static WebApplication Build(string[] args, ServerOptions? options = null, string? url = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(consoleOptions =>
        {
            consoleOptions.SingleLine = false;
            consoleOptions.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        if (!string.IsNullOrWhiteSpace(url))
            builder.WebHost.UseUrls(url);

        if (options != null)
            builder.Services.AddSingleton<IOptions<ServerOptions>>(Options.Create(options));
        else
        {
            builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("SockseekServer"));
            bool isOpenApiGeneration = string.Equals(
                Assembly.GetEntryAssembly()?.GetName().Name,
                "GetDocument.Insider",
                StringComparison.Ordinal);
            if (!isOpenApiGeneration && builder.Configuration["SockseekServer:Persistence:Enabled"] == null)
                builder.Services.PostConfigure<ServerOptions>(configured => configured.Persistence.Enabled = true);
        }

        builder.Services.AddOptions<HostOptions>()
            .Configure<IOptions<ServerOptions>>((host, server) =>
            {
                if (server.Value.ShutdownTimeout <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(
                        nameof(ServerOptions.ShutdownTimeout));
                host.ShutdownTimeout = server.Value.ShutdownTimeout;
            });

        builder.Services.Configure<JsonOptions>(jsonOptions =>
        {
            jsonOptions.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            SockseekApiJson.ConfigureSerializerOptions(jsonOptions.SerializerOptions);
        });

        builder.Services.AddSignalR()
            .AddJsonProtocol(jsonOptions =>
            {
                jsonOptions.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                SockseekApiJson.ConfigureSerializerOptions(jsonOptions.PayloadSerializerOptions);
            });
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info = new()
                {
                    Title = "Sockseek daemon API",
                    Version = GetOpenApiVersion(),
                    Description = "HTTP API for the Sockseek daemon."
                };

                return Task.CompletedTask;
            });
        });
        builder.Services.AddSingleton<PersistenceCoordinator>();
        builder.Services.AddHostedService<PersistenceRuntimeHostedService>();
        builder.Services.AddHostedService<PersistenceMaintenanceHostedService>();
        builder.Services.AddSingleton<EngineSupervisor>();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<EngineSupervisor>().StateStore);
        builder.Services.AddSingleton<HistoricalQueryFacade>();
        builder.Services.AddSingleton<LiveTransferCursorCodec>();
        builder.Services.AddSingleton<IOperatorMutationAuthorizer,
            CurrentTrustDomainOperatorAuthorizer>();
        builder.Services.AddSingleton<ServerEventBroadcaster>();
        builder.Services.AddHostedService<EngineRuntimeHostedService>();

        var app = builder.Build();
        _ = app.Services.GetRequiredService<ServerEventBroadcaster>();

        app.MapOpenApi("/api/openapi.json");
        MapEndpoints(app);
        return app;
    }

    private static string GetOpenApiVersion()
    {
        var assembly = typeof(ServerHost).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        var metadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex >= 0 ? version[..metadataIndex] : version;
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", () => Results.Redirect("/api/server/info"))
            .ExcludeFromDescription();

        app.MapGet("/api/server/info", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetInfo()))
            .WithTags("Server")
            .WithSummary("Gets server identity and protocol information.")
            .Produces<ServerInfoDto>();
        app.MapGet("/api/server/status", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetStatus()))
            .WithTags("Server")
            .WithSummary("Gets current daemon and Soulseek client status.")
            .Produces<ServerStatusDto>();
        app.MapGet("/api/daemon/snapshot", (EngineStateStore stateStore) =>
                Results.Ok(stateStore.GetDaemonSnapshot()))
            .WithTags("Live State")
            .WithSummary("Gets the bounded daemon replication snapshot and stream position.")
            .WithDescription("Contains active workflows and the jobs, searches, and transfers needed to render them. Retained terminal history remains paginated.")
            .Produces<StateSnapshotDto>();
        app.MapGet("/api/workflows/{workflowId:guid}/snapshot", (
            Guid workflowId,
            EngineStateStore stateStore) =>
                Results.Ok(stateStore.GetWorkflowSnapshot(workflowId)))
            .WithTags("Live State")
            .WithSummary("Gets one complete workflow replication snapshot and its workflow-local stream position.")
            .Produces<StateSnapshotDto>();
        app.MapGet("/api/chat/conversations/{conversationId:guid}/snapshot", async (
            Guid conversationId,
            EngineSupervisor supervisor,
            EngineStateStore stateStore,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                StateStreamScopeDto scope = StateStreamScopeDto.ChatConversation(conversationId);
                StateStreamPositionDto position = stateStore.GetChatPosition(scope);
                ChatTargetSnapshotDto? target = await chat.GetConversationSnapshotAsync(
                    conversationId, cancellationToken);
                return target is null
                    ? ChatNotFound("The conversation was not found.")
                    : Results.Ok(new StateSnapshotDto(
                        scope, position, DateTimeOffset.UtcNow, null, [], [], [], [], target));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Live State")
            .WithSummary("Gets a bounded recoverable conversation snapshot.")
            .Produces<StateSnapshotDto>()
            .WithChatErrors();
        app.MapGet("/api/chat/rooms/{roomId:guid}/snapshot", async (
            Guid roomId,
            EngineSupervisor supervisor,
            EngineStateStore stateStore,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                StateStreamScopeDto scope = StateStreamScopeDto.ChatRoom(roomId);
                StateStreamPositionDto position = stateStore.GetChatPosition(scope);
                ChatTargetSnapshotDto? target = await chat.GetRoomSnapshotAsync(roomId, cancellationToken);
                return target is null
                    ? ChatNotFound("The room was not found.")
                    : Results.Ok(new StateSnapshotDto(
                        scope, position, DateTimeOffset.UtcNow, null, [], [], [], [], target));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Live State")
            .WithSummary("Gets a bounded recoverable room snapshot.")
            .Produces<StateSnapshotDto>()
            .WithChatErrors();
        app.MapPost("/api/persistence/integrity", async (
            PersistenceCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await coordinator.CheckIntegrityAsync(cancellationToken)); }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _)) { return BadRequest(ex); }
        })
            .WithTags("Persistence")
            .WithSummary("Runs SQLite integrity_check against the live database.")
            .Produces<PersistenceIntegrityResultDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/persistence/backup", async (
            PersistenceBackupRequestDto request,
            PersistenceCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await coordinator.BackupAsync(request.BackupPath, cancellationToken)); }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _)) { return BadRequest(ex); }
        })
            .WithTags("Persistence")
            .WithSummary("Creates and independently verifies a WAL-safe online backup.")
            .Produces<PersistenceBackupResultDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/persistence/checkpoint", async (
            PersistenceCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await coordinator.CheckpointAsync(cancellationToken)); }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _)) { return BadRequest(ex); }
        })
            .WithTags("Persistence")
            .WithSummary("Requests a bounded passive WAL checkpoint.")
            .Produces<PersistenceCheckpointResultDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/persistence/retention", async (
            PersistenceCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await coordinator.RunRetentionAsync(cancellationToken)); }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _)) { return BadRequest(ex); }
        })
            .WithTags("Persistence")
            .WithSummary("Runs one bounded retention batch.")
            .Produces<PersistenceRetentionResultDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/chat", (EngineSupervisor supervisor) =>
        {
            ChatRuntime? chat = supervisor.Chat;
            return chat is null
                ? ChatUnavailable()
                : Results.Ok(chat.GetState());
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Gets compact chat runtime status.")
            .Produces<ChatRuntimeStateDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/conversations", async (
            bool? unread,
            bool? archived,
            string? cursor,
            int? limit,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                var page = await chat.GetConversationsAsync(
                    unread, archived, cursor, limit ?? ChatLimits.DefaultPageSize, cancellationToken);
                return Results.Ok(new ConversationPageDto(
                    page.Items.Select(ChatDtoMapper.ToDto).ToArray(), page.NextCursor));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Pages private-message conversations.")
            .Produces<ConversationPageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/private-messages", async (
            SendPrivateMessageRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                return Results.Ok(ChatDtoMapper.ToDto(await chat.SendPrivateMessageAsync(
                    request.Username, request.MessageId, request.Text, cancellationToken)));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Sends an idempotent private message.")
            .Produces<ChatMessageDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/conversations/{conversationId:guid}", async (
            Guid conversationId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                var conversation = await chat.GetConversationAsync(conversationId, cancellationToken);
                return conversation is null
                    ? ChatNotFound("The conversation was not found.")
                    : Results.Ok(ChatDtoMapper.ToDto(conversation));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Gets a private-message conversation.")
            .Produces<ConversationSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/conversations/{conversationId:guid}/messages", async (
            Guid conversationId,
            string? cursor,
            int? limit,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                if (await chat.GetConversationAsync(conversationId, cancellationToken) is null)
                    return ChatNotFound("The conversation was not found.");
                var page = await chat.GetMessagesAsync(
                    conversationId, cursor, limit ?? ChatLimits.DefaultPageSize, cancellationToken);
                return Results.Ok(new ChatMessagePageDto(
                    page.Items.Select(ChatDtoMapper.ToDto).ToArray(), page.NextCursor));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Pages messages in a private conversation.")
            .Produces<ChatMessagePageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/conversations/{conversationId:guid}/messages", async (
            Guid conversationId,
            SendChatMessageRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                return Results.Ok(ChatDtoMapper.ToDto(await chat.SendConversationMessageAsync(
                    conversationId, request.MessageId, request.Text, cancellationToken)));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Sends an idempotent message to a conversation.")
            .Produces<ChatMessageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/conversations/{conversationId:guid}/read", async (
            Guid conversationId,
            MarkChatReadRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                await chat.MarkConversationReadAsync(
                    conversationId, request.ThroughMessageId, cancellationToken);
                return Results.Ok(ChatDtoMapper.ToDto(
                    await chat.GetConversationAsync(conversationId, cancellationToken)
                    ?? throw new KeyNotFoundException("The conversation was not found.")));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Advances a conversation's local read watermark.")
            .Produces<ConversationSummaryDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/conversations/{conversationId:guid}/archive", async (
            Guid conversationId,
            ArchiveConversationRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                await chat.ArchiveConversationAsync(conversationId, request.Archived, cancellationToken);
                return Results.Ok(ChatDtoMapper.ToDto(
                    await chat.GetConversationAsync(conversationId, cancellationToken)
                    ?? throw new KeyNotFoundException("The conversation was not found.")));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Archives or reactivates a conversation.")
            .Produces<ConversationSummaryDto>()
            .WithChatErrors();

        app.MapDelete("/api/chat/conversations/{conversationId:guid}/history", async (
            Guid conversationId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                await chat.DeleteConversationHistoryAsync(conversationId, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Permanently deletes conversation history.")
            .Produces(StatusCodes.Status204NoContent)
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/available", async (
            ChatRoomKind? kind,
            string? cursor,
            int? limit,
            bool? refresh,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                return Results.Ok(await chat.GetAvailableRoomsAsync(
                    kind, cursor, limit ?? ChatLimits.DefaultPageSize, refresh == true, cancellationToken));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Pages rooms visible to the current Soulseek account.")
            .Produces<AvailableRoomPageDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms", async (
            string? state,
            string? cursor,
            int? limit,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                return Results.Ok(await chat.GetRoomSummariesAsync(
                    state, cursor, limit ?? ChatLimits.DefaultPageSize, cancellationToken));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Pages known room subscriptions and runtime state.")
            .Produces<ChatRoomPageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/rooms", async (
            JoinRoomRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                return Results.Ok(await chat.JoinRoomAsync(
                    request.RoomName, request.Remember, cancellationToken));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Joins a room and optionally remembers it across restarts.")
            .Produces<ChatRoomSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/{roomId:guid}", async (
            Guid roomId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                var room = await chat.GetRoomDetailAsync(roomId, cancellationToken);
                return room is null ? ChatNotFound("The room was not found.") : Results.Ok(room);
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Gets room subscription, join, and private-room metadata.")
            .Produces<ChatRoomDetailDto>()
            .WithChatErrors();

        app.MapDelete("/api/chat/rooms/{roomId:guid}", async (
            Guid roomId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try { return Results.Ok(await chat.LeaveRoomAsync(roomId, cancellationToken)); }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Leaves a room and removes its runtime subscription.")
            .Produces<ChatRoomSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/{roomId:guid}/messages", async (
            Guid roomId,
            string? cursor,
            int? limit,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                if (await chat.GetRoomSummaryAsync(roomId, cancellationToken) is null)
                    return ChatNotFound("The room was not found.");
                var page = await chat.GetMessagesAsync(
                    roomId, cursor, limit ?? ChatLimits.DefaultPageSize, cancellationToken);
                return Results.Ok(new ChatMessagePageDto(
                    page.Items.Select(ChatDtoMapper.ToDto).ToArray(), page.NextCursor));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Pages messages in a joined or retained room.")
            .Produces<ChatMessagePageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/rooms/{roomId:guid}/messages", async (
            Guid roomId,
            SendChatMessageRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                return Results.Ok(ChatDtoMapper.ToDto(await chat.SendRoomMessageAsync(
                    roomId, request.MessageId, request.Text, cancellationToken)));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Sends an idempotent room message.")
            .Produces<ChatMessageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/rooms/{roomId:guid}/read", async (
            Guid roomId,
            MarkChatReadRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                await chat.MarkRoomReadAsync(roomId, request.ThroughMessageId, cancellationToken);
                return Results.Ok(await chat.GetRoomSummaryAsync(roomId, cancellationToken)
                                  ?? throw new KeyNotFoundException("The room was not found."));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Advances a room's local read watermark.")
            .Produces<ChatRoomSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/{roomId:guid}/members", async (
            Guid roomId,
            string? cursor,
            int? limit,
            long? revision,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                return Results.Ok(await chat.GetRoomMembersAsync(
                    roomId, cursor, limit ?? ChatLimits.DefaultPageSize, revision, cancellationToken));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Pages the current ephemeral room roster.")
            .Produces<RoomMemberPageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/rooms/{roomId:guid}/members", async (
            Guid roomId,
            AddRoomMemberRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                await chat.AddPrivateRoomMemberAsync(roomId, request.Username, cancellationToken);
                return Results.Ok(await chat.GetRoomDetailAsync(roomId, cancellationToken));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Adds a member to a joined private room.")
            .Produces<ChatRoomDetailDto>()
            .WithChatErrors();

        app.MapDelete("/api/chat/rooms/{roomId:guid}/history", async (
            Guid roomId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                await chat.DeleteRoomHistoryAsync(roomId, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Permanently deletes retained room history.")
            .Produces(StatusCodes.Status204NoContent)
            .WithChatErrors();

        app.MapGet("/api/notifications", async (
            bool? unread,
            UserNotificationKind? kind,
            string? cursor,
            int? limit,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                var page = await chat.GetNotificationsAsync(
                    unread, kind, cursor, limit ?? ChatLimits.DefaultPageSize, cancellationToken);
                return Results.Ok(new NotificationPageDto(
                    page.Items.Select(ChatDtoMapper.ToDto).ToArray(), page.NextCursor));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Notifications")
            .WithSummary("Pages durable chat notifications.")
            .Produces<NotificationPageDto>()
            .WithChatErrors();

        app.MapGet("/api/notifications/{notificationId:guid}", async (
            Guid notificationId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                var notification = await chat.GetNotificationAsync(notificationId, cancellationToken);
                return notification is null
                    ? ChatNotFound("The notification was not found.")
                    : Results.Ok(ChatDtoMapper.ToDto(notification));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Notifications")
            .WithSummary("Gets a durable chat notification.")
            .Produces<UserNotificationDto>()
            .WithChatErrors();

        app.MapPost("/api/notifications/{notificationId:guid}/read", async (
            Guid notificationId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                if (await chat.GetNotificationAsync(notificationId, cancellationToken) is null)
                    return ChatNotFound("The notification was not found.");
                await chat.MarkNotificationsReadAsync(null, [notificationId], cancellationToken);
                return Results.Ok(ChatDtoMapper.ToDto(
                    await chat.GetNotificationAsync(notificationId, cancellationToken)
                    ?? throw new KeyNotFoundException("The notification was not found.")));
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Notifications")
            .WithSummary("Marks one notification read.")
            .Produces<UserNotificationDto>()
            .WithChatErrors();

        app.MapPost("/api/notifications/read", async (
            MarkNotificationsReadRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            if (supervisor.Chat is not { } chat)
                return ChatUnavailable();
            try
            {
                await chat.MarkNotificationsReadAsync(
                    request.ThroughSequence, request.Ids, cancellationToken);
                return Results.Ok(chat.GetNotificationSummary());
            }
            catch (Exception ex) { return ChatFailure(ex); }
        })
            .RequireOperator()
            .WithTags("Notifications")
            .WithSummary("Marks a bounded notification set or sequence range read.")
            .Produces<NotificationSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/profiles", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetProfiles()))
            .WithTags("Profiles")
            .WithSummary("Lists configured download profiles.")
            .Produces<IReadOnlyList<ProfileSummaryDto>>();
        app.MapGet("/api/jobs", async (
            HistoricalQueryFacade queryFacade,
            HttpContext httpContext,
            ServerJobLifecycleState? lifecycleState,
            ServerJobTerminalOutcome? terminalOutcome,
            ServerJobSkipReason? skipReason,
            ServerJobKind? kind,
            Guid? workflowId,
            bool includeAll,
            string? cursor,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await queryFacade.GetJobsAsync(
                    new JobQuery(lifecycleState, terminalOutcome, kind, workflowId, includeAll, skipReason),
                    cursor,
                    limit ?? 100,
                    cancellationToken);
                if (page.NextCursor != null)
                    httpContext.Response.Headers["X-Next-Cursor"] = page.NextCursor;
                return Results.Ok(page.Items);
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Jobs")
            .WithSummary("Lists known jobs.")
            .WithDescription("Default results contain only execution roots where ParentJobId is null. Set includeAll=true for a flat list of every matching job.")
            .Produces<IReadOnlyList<JobSummaryDto>>();

        app.MapGet("/api/jobs/{jobId:guid}", async (Guid jobId, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            var detail = await queryFacade.GetJobAsync(jobId, cancellationToken);
            return detail != null ? Results.Ok(detail) : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Gets a job snapshot by id.")
            .Produces<JobDetailDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/workflows/{workflowId:guid}/jobs/display/{displayId:int}", async (
            Guid workflowId, int displayId, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            var detail = await queryFacade.GetJobByDisplayIdAsync(workflowId, displayId, cancellationToken);
            return detail != null ? Results.Ok(detail) : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Gets a job snapshot by workflow and display id.")
            .Produces<JobDetailDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/raw", async (
            Guid jobId,
            long afterSequence,
            int? limit,
            HistoricalQueryFacade queryFacade,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await queryFacade.GetRawSearchResultsAsync(jobId, afterSequence, limit ?? 200, cancellationToken);
                if (page?.NextSequence != null)
                    httpContext.Response.Headers["X-Next-Sequence"] = page.NextSequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return page != null
                    ? Results.Ok(page.Items)
                    : Results.NotFound(new ApiErrorDto(
                        "The transfer was not found.",
                        "TransferNotFound"));
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Search Results")
            .WithSummary("Gets raw search responses for a search job.")
            .WithDescription("Use afterSequence to incrementally fetch raw responses after the last seen sequence.")
            .Produces<IReadOnlyList<SearchRawResultDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);

        app.MapGet("/api/transfers", async (
            HistoricalQueryFacade queryFacade,
            HttpContext httpContext,
            Guid? jobId,
            Guid? workflowId,
            string? direction,
            string? source,
            string? state,
            string? terminalOutcome,
            string? username,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            string? cursor,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await queryFacade.GetTransfersAsync(
                    cursor, limit ?? 100, jobId, workflowId,
                    direction, source, state, terminalOutcome, username, fromUtc, toUtc,
                    cancellationToken);
                if (page.NextCursor != null)
                    httpContext.Response.Headers["X-Next-Cursor"] = page.NextCursor;
                return Results.Ok(page.Items);
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Transfers")
            .WithSummary("Lists durable transfer history.")
            .Produces<IReadOnlyList<TransferHistoryDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/sharing", (EngineSupervisor supervisor) =>
        {
            SharingRuntime? sharing = supervisor.Sharing;
            return sharing is null
                ? Results.Json(
                    new ApiErrorDto("Sharing infrastructure is unavailable.", "SharingUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(sharing.GetSharingState());
        })
            .WithTags("Sharing")
            .WithSummary("Gets bounded sharing and catalog status.")
            .Produces<SharingStateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/sharing/scans", (EngineSupervisor supervisor) =>
        {
            SharingRuntime? sharing = supervisor.Sharing;
            if (sharing is null)
            {
                return Results.Json(
                    new ApiErrorDto("Sharing infrastructure is unavailable.", "SharingUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            if (sharing.GetSharingState().State == DaemonFeatureState.Disabled)
            {
                return Results.Json(
                    new ApiErrorDto("No share roots are configured.", "SharingNotConfigured"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var started = sharing.StartScan();
            if (started is null)
            {
                return Results.Json(
                    new ApiErrorDto("The scan could not be started.", "ScanUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var response = new StartShareScanResponseDto(
                started.Value.Started
                    ? StartShareScanResult.Started
                    : StartShareScanResult.AlreadyRunning,
                started.Value.Scan);
            return started.Value.Started
                ? Results.Json(response, statusCode: StatusCodes.Status202Accepted)
                : Results.Ok(response);
        })
            .RequireOperatorMutation()
            .WithTags("Sharing")
            .WithSummary("Starts a share scan or returns the currently active scan.")
            .Produces<StartShareScanResponseDto>(StatusCodes.Status202Accepted)
            .Produces<StartShareScanResponseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/sharing/scans/{scanId:guid}", (
            Guid scanId,
            EngineSupervisor supervisor) =>
        {
            ShareScanStateDto? scan = supervisor.Sharing?.GetScan(scanId);
            return scan is null
                ? Results.NotFound(new ApiErrorDto(
                    "The scan was not found.",
                    "ScanNotFound"))
                : Results.Ok(scan);
        })
            .WithTags("Sharing")
            .WithSummary("Gets an active or recently completed share scan.")
            .Produces<ShareScanStateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);

        app.MapPost("/api/sharing/scans/{scanId:guid}/cancel", (
            Guid scanId,
            EngineSupervisor supervisor) =>
        {
            SharingRuntime? sharing = supervisor.Sharing;
            if (sharing is null)
            {
                return Results.Json(
                    new ApiErrorDto("Sharing infrastructure is unavailable.", "SharingUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            ShareScanStateDto? scan = sharing.GetScan(scanId);
            if (scan is null)
                return Results.NotFound(new ApiErrorDto("The scan was not found.", "ScanNotFound"));
            if (!sharing.CancelScan(scanId))
            {
                return Results.Conflict(
                    new ApiErrorDto("The scan is no longer cancellable.", "ScanNotCancellable"));
            }
            return Results.Ok(sharing.GetScan(scanId) ?? scan);
        })
            .RequireOperatorMutation()
            .WithTags("Sharing")
            .WithSummary("Cancels an active share scan.")
            .Produces<ShareScanStateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/transfers/live", (
            EngineSupervisor supervisor,
            EngineStateStore stateStore,
            LiveTransferCursorCodec cursors,
            string? direction,
            string? state,
            string? username,
            string? cursor,
            int? limit) =>
        {
            try
            {
                SharingRuntime? sharing = supervisor.Sharing;
                if (sharing is null)
                {
                    return Results.Json(
                        new ApiErrorDto("Upload runtime is unavailable.", "UploadsUnavailable"),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                if (direction is not null
                    && !direction.Equals("upload", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Only live upload queue rows are available.");
                if (state is not null
                    && !state.Equals("queued", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Only queued live transfer rows are pageable.");

                LiveTransferCursor? decoded = cursor is null
                    ? null
                    : cursors.Decode(cursor);
                var page = sharing.Uploads.GetQueuePage(
                    decoded?.RequestedAtUtc,
                    decoded?.TransferId,
                    limit ?? 100,
                    decoded?.ObservedQueueRevision,
                    username);
                string? next = page.NextRequestedAtUtc is { } nextTime
                               && page.NextTransferId is { } nextId
                    ? cursors.Encode(
                        nextTime,
                        nextId,
                        page.ObservedQueueRevision)
                    : null;
                var items = page.Items
                    .Select(item => stateStore.GetLiveTransfer(item.TransferId))
                    .Where(item => item is not null)
                    .Cast<TransferStateDto>()
                    .ToArray();
                return Results.Ok(new LiveTransferPageDto(
                    items,
                    next,
                    page.ObservedQueueRevision,
                    page.QueueChanged));
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Transfers")
            .WithSummary("Pages the bounded live transfer queue.")
            .Produces<LiveTransferPageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/transfers/{transferId:guid}", async (
            Guid transferId,
            int? attemptLimit,
            HistoricalQueryFacade queryFacade,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var detail = await queryFacade.GetTransferDetailAsync(transferId, attemptLimit ?? 200, cancellationToken);
                return detail != null
                    ? Results.Ok(detail)
                    : Results.NotFound(new ApiErrorDto(
                        "The transfer was not found.",
                        "TransferNotFound"));
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Transfers")
            .WithSummary("Gets one transfer using a live-first overlay and retained history fallback.")
            .Produces<TransferDetailDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);

        app.MapPost("/api/transfers/{transferId:guid}/cancel", async (
            Guid transferId,
            EngineSupervisor supervisor,
            HistoricalQueryFacade queryFacade,
            CancellationToken cancellationToken) =>
        {
            SharingRuntime? sharing = supervisor.Sharing;
            if (sharing is null)
            {
                return Results.Json(
                    new ApiErrorDto("Upload runtime is unavailable.", "UploadsUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var current = sharing.Uploads.GetTransfer(transferId);
            if (current is null)
            {
                TransferDetailDto? retained = await queryFacade.GetTransferDetailAsync(
                    transferId,
                    attemptLimit: 1,
                    cancellationToken);
                return retained is null
                    ? Results.NotFound(new ApiErrorDto(
                        "The transfer was not found.",
                        "TransferNotFound"))
                    : Results.Conflict(new ApiErrorDto(
                        "The retained transfer is no longer cancellable.",
                        "TransferNotCancellable"));
            }
            if (!sharing.Uploads.Cancel(transferId))
            {
                return Results.Conflict(
                    new ApiErrorDto("The transfer is no longer cancellable.", "TransferNotCancellable"));
            }
            return Results.Ok(supervisor.StateStore.GetLiveTransfer(transferId));
        })
            .RequireOperatorMutation()
            .WithTags("Transfers")
            .WithSummary("Cancels a queued or active upload transfer.")
            .Produces<TransferStateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/transfers/{transferId:guid}/attempts", async (
            Guid transferId,
            int afterAttemptNumber,
            int? limit,
            HistoricalQueryFacade queryFacade,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await queryFacade.GetTransferAttemptsAsync(
                    transferId, afterAttemptNumber, limit ?? 100, cancellationToken);
                if (page?.NextAttemptNumber != null)
                    httpContext.Response.Headers["X-Next-Attempt-Number"] = page.NextAttemptNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return page != null ? Results.Ok(page.Items) : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Transfers")
            .WithSummary("Lists durable attempts for one transfer.")
            .Produces<IReadOnlyList<TransferAttemptHistoryDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/results/files", async (
            Guid jobId,
            HistoricalQueryFacade queryFacade,
            CancellationToken cancellationToken) =>
        {
            var results = await queryFacade.GetFileResultsAsync(jobId, null, cancellationToken);
            return results != null ? Results.Ok(results) : Results.NotFound();
        })
            .WithTags("Search Results")
            .WithSummary("Gets file candidates for a search-like job.")
            .Produces<SearchResultSnapshotDto<FileCandidateDto>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/results/files/project", async (
            Guid jobId,
            FileSearchProjectionRequestDto request,
            HistoricalQueryFacade queryFacade,
            CancellationToken cancellationToken) =>
        {
            var results = await queryFacade.GetFileResultsAsync(jobId, request, cancellationToken);
            return results != null ? Results.Ok(results) : Results.NotFound();
        })
            .WithTags("Search Results")
            .WithSummary("Projects search results as file candidates.")
            .Produces<SearchResultSnapshotDto<FileCandidateDto>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/results/folders", async (
            Guid jobId, bool includeFiles, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            try
            {
                var results = await queryFacade.GetFolderResultsAsync(jobId, null, includeFiles, cancellationToken);
                return results != null ? Results.Ok(results) : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Search Results")
            .WithSummary("Gets folder candidates for an album search-like job.")
            .WithDescription("Set includeFiles=true only when the client needs selectable files. Folder file counts can come from search results and may not represent a full browse of the remote folder.")
            .Produces<SearchResultSnapshotDto<AlbumFolderDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/results/folders/project", async (
            Guid jobId, FolderSearchProjectionRequestDto request, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            try
            {
                var results = await queryFacade.GetFolderResultsAsync(jobId, request, request.IncludeFiles, cancellationToken);
                return results != null ? Results.Ok(results) : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Search Results")
            .WithSummary("Projects search results as album folders.")
            .Produces<SearchResultSnapshotDto<AlbumFolderDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/results/aggregate-tracks", async (
            Guid jobId, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            var results = await queryFacade.GetAggregateTrackResultsAsync(jobId, null, cancellationToken);
            return results != null ? Results.Ok(results) : Results.NotFound();
        })
            .WithTags("Search Results")
            .WithSummary("Gets aggregate track candidates.")
            .Produces<SearchResultSnapshotDto<AggregateTrackCandidateDto>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/results/aggregate-tracks/project", async (
            Guid jobId, AggregateTrackProjectionRequestDto request, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            var results = await queryFacade.GetAggregateTrackResultsAsync(jobId, request, cancellationToken);
            return results != null ? Results.Ok(results) : Results.NotFound();
        })
            .WithTags("Search Results")
            .WithSummary("Projects search results as aggregate track candidates.")
            .Produces<SearchResultSnapshotDto<AggregateTrackCandidateDto>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/results/aggregate-albums", async (
            Guid jobId, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            try
            {
                var results = await queryFacade.GetAggregateAlbumResultsAsync(jobId, null, cancellationToken);
                return results != null ? Results.Ok(results) : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Search Results")
            .WithSummary("Gets aggregate album candidates.")
            .Produces<SearchResultSnapshotDto<AggregateAlbumCandidateDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/results/aggregate-albums/project", async (
            Guid jobId, AggregateAlbumProjectionRequestDto request, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            try
            {
                var results = await queryFacade.GetAggregateAlbumResultsAsync(jobId, request, cancellationToken);
                return results != null ? Results.Ok(results) : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Search Results")
            .WithSummary("Projects search results as aggregate album candidates.")
            .Produces<SearchResultSnapshotDto<AggregateAlbumCandidateDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/retrieve-folder", async (
            Guid jobId,
            RetrieveFolderRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summary = await supervisor.StartRetrieveFolderAsync(jobId, request, ct);
                return summary != null
                    ? Results.Accepted($"/api/jobs/{summary.JobId}", summary)
                    : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Follow-up Jobs")
            .WithSummary("Starts a folder retrieval job for a selected album result folder.")
            .WithDescription("Retrieves the full remote folder contents for a selected folder result. Search responses can omit child items that did not match the original query.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/{jobId:guid}/downloads/files", async (
            Guid jobId,
            StartFileDownloadsRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summaries = await supervisor.StartFileDownloadsAsync(jobId, request, ct);
                return summaries != null
                    ? Results.Accepted($"/api/jobs/{jobId}", summaries)
                    : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Follow-up Jobs")
            .WithSummary("Starts one or more file download jobs from selected search result files.")
            .WithDescription("The source search job identifies where the candidate refs came from. Per-download settings belong in the request options, not in the original search job.")
            .Produces<IReadOnlyList<JobSummaryDto>>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/{jobId:guid}/downloads/folder", async (
            Guid jobId,
            StartFolderDownloadRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summary = await supervisor.StartFolderDownloadAsync(jobId, request, ct);
                return summary != null
                    ? Results.Accepted($"/api/jobs/{summary.JobId}", summary)
                    : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Follow-up Jobs")
            .WithSummary("Starts an album/folder download job from a selected folder result.")
            .WithDescription("The source search job identifies where the folder ref came from. Per-download settings belong in the request options, not in the original search job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/{jobId:guid}/manual/complete", async (Guid jobId, EngineSupervisor supervisor) =>
        {
            return await supervisor.CompleteManualSelectionAsync(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Completes a manual-selection job without starting additional downloads.")
            .WithDescription("Use this when a DownloadBehavior.Manual job reached AwaitingSelection and the caller wants to close the manual step without resuming the job.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/manual/skip", async (Guid jobId, EngineSupervisor supervisor) =>
        {
            return await supervisor.SkipManualSelectionAsync(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Skips a manual-selection job without starting additional downloads.")
            .WithDescription("Use this when a DownloadBehavior.Manual job reached AwaitingSelection and the caller wants to record an explicit user skip.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/cancel", (Guid jobId, EngineSupervisor supervisor) =>
        {
            return supervisor.CancelJob(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Cancels a job when cancellation is available.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/cancel-all", (EngineSupervisor supervisor) =>
        {
            int cancelled = supervisor.CancelAllJobs();
            return Results.Accepted(
                "/api/jobs",
                new CancelJobsResponseDto(cancelled));
        })
            .WithTags("Jobs")
            .WithSummary("Cancels all currently cancellable daemon jobs.")
            .WithDescription("The daemon remains running and can accept later submissions.")
            .Produces<CancelJobsResponseDto>(StatusCodes.Status202Accepted);

        app.MapPost("/api/workflows/{workflowId:guid}/jobs/display/{displayId:int}/cancel", (Guid workflowId, int displayId, EngineSupervisor supervisor) =>
        {
            return supervisor.CancelJobByDisplayId(workflowId, displayId)
                ? Results.Accepted($"/api/workflows/{workflowId}")
                : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Cancels a workflow job by display id.")
            .WithDescription("Convenience endpoint for CLI-style cancellation prompts. Normal GUI clients should prefer AvailableActions on known job ids.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/next-candidate", (Guid jobId, EngineSupervisor supervisor) =>
        {
            return supervisor.TryNextCandidate(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Tries the next candidate for an active job download.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/workflows/{workflowId:guid}/jobs/display/{displayId:int}/next-candidate", (Guid workflowId, int displayId, EngineSupervisor supervisor) =>
        {
            return supervisor.TryNextCandidateByDisplayId(workflowId, displayId)
                ? Results.Accepted($"/api/workflows/{workflowId}")
                : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Tries the next candidate for an active job download by display id.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/extract", async (SubmitExtractJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitExtractJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an input extraction job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/search", async (SubmitSearchJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitSearchJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a generic Soulseek search job.")
            .WithDescription("Search jobs are discovery-oriented. They store raw Soulseek results; use projection endpoints to view those results as files, album folders, or aggregate candidates, then use follow-up download endpoints for selected refs.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/search/tracks", async (SubmitTrackSearchJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitTrackSearchJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a track search job.")
            .WithDescription("Track search jobs are suitable for exploratory pick-then-download UIs: inspect projected file candidates from the result endpoints, then start follow-up downloads from selected refs.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/search/albums", async (SubmitAlbumSearchJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAlbumSearchJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an album search job.")
            .WithDescription("Album search jobs are suitable for exploratory pick-then-download UIs: inspect projected folder candidates from the result endpoints, then start a follow-up folder download from the selected ref.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/downloads/song", async (SubmitSongJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitSongJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a single-file download job.")
            .WithDescription("Use DownloadBehavior.Automatic for normal transfer jobs. Use DownloadBehavior.Manual when the job should pause at AwaitingSelection for caller approval/selection before resuming the same job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/downloads/album", async (SubmitAlbumJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAlbumJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an album/folder download job.")
            .WithDescription("Use DownloadBehavior.Automatic for normal transfer jobs. Use DownloadBehavior.Manual when the job should pause at AwaitingSelection for caller approval/selection before resuming the same job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/aggregate/tracks", async (SubmitAggregateJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAggregateJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an aggregate track search job.")
            .WithDescription("Aggregate jobs can download automatically or, with DownloadBehavior.Manual, pause after candidate grouping so the caller can choose which child downloads to resume.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/aggregate/albums", async (SubmitAlbumAggregateJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAlbumAggregateJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an aggregate album search job.")
            .WithDescription("Aggregate album jobs can download automatically or, with DownloadBehavior.Manual, pause after bucket projection so the caller can choose which child downloads to resume.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/lists", async (SubmitJobListRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitJobListAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a job list from draft child jobs.")
            .WithDescription("Job drafts are submission payloads only. Submitted children appear as normal runtime jobs in subsequent job/workflow snapshots.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/workflows", async (
            string? cursor,
            int? limit,
            HistoricalQueryFacade queryFacade,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await queryFacade.GetWorkflowsAsync(cursor, limit ?? 100, cancellationToken);
                if (page.NextCursor != null)
                    httpContext.Response.Headers["X-Next-Cursor"] = page.NextCursor;
                return Results.Ok(page.Items);
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Workflows")
            .WithSummary("Lists known workflows.")
            .Produces<IReadOnlyList<WorkflowSummaryDto>>();

        app.MapGet("/api/workflows/{workflowId:guid}", async (
            Guid workflowId, bool? includeAll, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            var workflow = await queryFacade.GetWorkflowAsync(workflowId, includeAll == true, cancellationToken);
            return workflow != null ? Results.Ok(workflow) : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Gets a workflow snapshot by id.")
            .WithDescription("Default results contain only execution roots where ParentJobId is null. Set includeAll=true for a flat list of every workflow job. Use /tree for the same jobs grouped by ParentJobId.")
            .Produces<WorkflowDetailDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/workflows/{workflowId:guid}/tree", async (
            Guid workflowId, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            var workflow = await queryFacade.GetWorkflowTreeAsync(workflowId, cancellationToken);
            return workflow != null ? Results.Ok(workflow) : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Gets the execution tree for a workflow.")
            .WithDescription("This tree is built only from ParentJobId relationships. Follow-up jobs started from search results remain workflow roots and expose SourceJobId instead.")
            .Produces<WorkflowTreeDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/workflows/{workflowId:guid}/cancel", (Guid workflowId, EngineSupervisor supervisor) =>
        {
            int cancelled = supervisor.CancelWorkflow(workflowId);
            return cancelled > 0
                ? Results.Accepted($"/api/workflows/{workflowId}", new CancelWorkflowResponseDto(cancelled))
                : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Cancels cancellable jobs in a workflow.")
            .Produces<CancelWorkflowResponseDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapHub<ServerEventHub>("/api/events");
    }

    private static async Task<IResult> SubmitJobAsync(Func<Task<JobSummaryDto>> submit)
    {
        try
        {
            var summary = await submit();
            return Results.Accepted($"/api/jobs/{summary.JobId}", summary);
        }
        catch (Exception ex) when (TryCreateBadRequest(ex, out _))
        {
            return BadRequest(ex);
        }
    }

    private static IResult BadRequest(Exception ex)
    {
        TryCreateBadRequest(ex, out var error);
        Sockseek.Core.SockseekLog.Daemon.Warn($"Bad request: {error}");
        string code = ex is UnsupportedNameFormatVariableException
            ? "invalid-name-format-variable"
            : "InvalidRequest";
        return Results.BadRequest(new ApiErrorDto(error, code));
    }

    private static IResult ChatUnavailable()
        => Results.Json(
            new ApiErrorDto(
                "Chat is unavailable because daemon persistence is disabled or not started.",
                "Unavailable"),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static RouteHandlerBuilder WithChatErrors(this RouteHandlerBuilder builder)
        => builder
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status429TooManyRequests)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

    private static IResult ChatNotFound(string message)
        => Results.NotFound(new ApiErrorDto(message, "NotFound"));

    private static IResult ChatFailure(Exception exception)
    {
        return exception switch
        {
            ArgumentException => Results.BadRequest(
                new ApiErrorDto(exception.Message, "InvalidRequest")),
            KeyNotFoundException => ChatNotFound(exception.Message),
            UnauthorizedAccessException => Results.Json(
                new ApiErrorDto(exception.Message, "Denied"),
                statusCode: StatusCodes.Status403Forbidden),
            ChatCapacityException => Results.Json(
                new ApiErrorDto(exception.Message, "Capacity"),
                statusCode: StatusCodes.Status429TooManyRequests),
            ChatStateConflictException => Results.Conflict(
                new ApiErrorDto(exception.Message, "Conflict")),
            InvalidOperationException when exception.Message.Contains(
                "MessageId", StringComparison.Ordinal) => Results.Conflict(
                    new ApiErrorDto(exception.Message, "Conflict")),
            InvalidOperationException when exception.Message.Contains(
                "revision changed", StringComparison.OrdinalIgnoreCase) => Results.Conflict(
                    new ApiErrorDto(exception.Message, "Conflict")),
            InvalidOperationException => Results.Json(
                new ApiErrorDto(exception.Message, "Unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(
                new ApiErrorDto("The chat operation failed.", "Unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }

    private static bool TryCreateBadRequest(Exception ex, out string error)
    {
        error = ex.Message;
        return ex is ArgumentException
            || ex.Message.StartsWith("Input error:", StringComparison.Ordinal);
    }
}
