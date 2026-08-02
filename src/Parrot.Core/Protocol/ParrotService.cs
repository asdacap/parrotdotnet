using Grpc.Core;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Permissions;
using Parrot.Questions;
using Parrot.Security;
using Parrot.Store;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Protocol;

// The gRPC contract. Commands in, one flat event stream out.
//
// It deals in user sessions only. Agent sessions live inside one and are not
// addressable here, because a user never spawns a subagent -- an agent does.
//
// The base class is generated: `service Parrot` in parrot.proto produces a
// container class `Parrot.Protocol.Parrot` holding `ParrotBase` for servers and
// `ParrotClient` for clients. The alias exists because `Parrot` is also this
// repository's root namespace, and `Parrot.ParrotBase` reads as though it were
// a namespace lookup.
internal sealed class ParrotService(
    ModelRouter router,
    ProviderRegistry registry,
    ModelAliasConfigurator aliases,
    SessionStore store,
    SessionCatalog sessionCatalog,
    ModeRegistry modes) : GeneratedParrot.ParrotBase, IAsyncDisposable
{
    private readonly UserSessionRegistry _userSessions = new();

    public override async Task<ListModelsResponse> ListModels(ListModelsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var listed = await registry.AvailableModels(context.CancellationToken).ConfigureAwait(false);
        var response = new ListModelsResponse();

        foreach (var model in listed)
        {
            var listedModel = new Model { Id = model.Model.Id, ProviderId = model.Model.ProviderId };
            listedModel.Variants.AddRange(model.Model.Capabilities.Variants.Select(variant =>
                new ModelVariant { Name = variant.Name, ReasoningEffort = variant.ReasoningEffort }));
            response.Models.Add(listedModel);
        }

        return response;
    }

    public override Task<ListModelAliasesResponse> ListModelAliases(
        ListModelAliasesRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var response = new ListModelAliasesResponse();
        response.Aliases.AddRange(aliases.Capture().Definitions.Values.Select(ToProtocol));
        return Task.FromResult(response);
    }

    public override Task<ConfigureModelAliasResponse> ConfigureModelAlias(
        ConfigureModelAliasRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return Task.FromResult(new ConfigureModelAliasResponse
            {
                Alias = ToProtocol(aliases.Configure(request.Name, request.ModelString)),
            });
        }
        catch (Exception failure) when (failure is LLMProviderException or InvalidDataException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, failure.Message));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new RpcException(new Status(
                StatusCode.Internal,
                $"failed to persist model alias: {failure.Message}"));
        }
    }

    public override async Task<ListProviderModelAliasDefaultsResponse> ListProviderModelAliasDefaults(
        ListProviderModelAliasDefaultsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var available = await registry.AvailableModels(context.CancellationToken).ConfigureAwait(false);
        var response = new ListProviderModelAliasDefaultsResponse();
        response.Providers.AddRange(aliases.ListProviderDefaults(
            available.Select(model => model.Provider.Id)).Select(defaults =>
                new ProviderModelAliasDefaults { ProviderId = defaults.ProviderId }));
        return response;
    }

    public override async Task<ApplyProviderModelAliasDefaultsResponse> ApplyProviderModelAliasDefaults(
        ApplyProviderModelAliasDefaultsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var available = await registry.AvailableModels(context.CancellationToken).ConfigureAwait(false);
            var providerIds = aliases.ListProviderDefaults(available.Select(model => model.Provider.Id))
                .Select(defaults => defaults.ProviderId);
            if (!providerIds.Contains(request.ProviderId, StringComparer.Ordinal))
            {
                throw new LLMProviderException(
                    $"model alias defaults: provider \"{request.ProviderId}\" is not available");
            }

            var response = new ApplyProviderModelAliasDefaultsResponse();
            response.Aliases.AddRange(aliases.ApplyProviderDefaults(request.ProviderId).Select(ToProtocol));
            return response;
        }
        catch (Exception failure) when (failure is LLMProviderException or InvalidDataException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, failure.Message));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new RpcException(new Status(
                StatusCode.Internal,
                $"failed to persist model aliases: {failure.Message}"));
        }
    }

    public override Task<ListModesResponse> ListModes(ListModesRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var response = new ListModesResponse();
        response.Modes.AddRange(modes.List().Select(id => new Mode { Id = id }));
        return Task.FromResult(response);
    }

    public override Task<ListSessionsResponse> ListSessions(
        ListSessionsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var response = new ListSessionsResponse();
        response.Sessions.AddRange(sessionCatalog.List()
            .OrderBy(entry => entry.CreatedAt, StringComparer.Ordinal)
            .ThenBy(entry => entry.Id.Value, StringComparer.Ordinal)
            .Select(entry => new SessionSummary
            {
                UserSessionId = entry.Id.Value,
                Model = entry.Model,
                Mode = entry.Mode,
                CreatedAt = entry.CreatedAt,
                RootAgentName = entry.RootAgentName,
                State = _userSessions.Contains(entry.Id.Value)
                    ? SessionState.Active
                    : entry.State == SessionCatalogState.Corrupt
                        ? SessionState.Corrupt
                        : SessionState.Inactive,
            }));
        return Task.FromResult(response);
    }

    public override async Task<UserSession> CreateSession(CreateSessionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        ResolvedModelSelection model;

        try
        {
            model = router.Resolve(request.Model);
        }
        catch (LLMProviderException failure)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, failure.Message));
        }

        Agent.UserSession created;

        try
        {
            _ = modes.Resolve(request.Mode);
            created = await _userSessions.Host(() =>
                store.CreateFresh(model, request.Mode, request.InteractivePermissions)).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ModeRegistryException or LLMProviderException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, failure.Message));
        }

        return UserSession.From(created, false);
    }

    public override Task<UserSession> UpdateSession(UpdateSessionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var found = Find(request.UserSessionId);

        IMode? selectedMode = null;
        ResolvedModelSelection? selectedModel = null;

        try
        {
            if (request.Mode.Length > 0)
            {
                selectedMode = found.ResolveMode(request.Mode);
            }

            if (request.Model.Length > 0)
            {
                selectedModel = router.Resolve(request.Model);
            }
        }
        catch (Exception failure) when (failure is ModeRegistryException or LLMProviderException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, failure.Message));
        }

        found.Update(selectedModel, selectedMode);
        SessionStore.Publish(found);

        return Task.FromResult(UserSession.From(found, false));
    }

    public override async Task<AttachmentUploadResponse> UploadAttachment(
        IAsyncStreamReader<AttachmentUploadFrame> requestStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var header = await ReadUploadHeader(requestStream, context.CancellationToken).ConfigureAwait(false);
            var session = Find(header.UserSessionId);
            await using var content = new MemoryStream();
            AttachmentUploadDescription? description = null;
            var chunks = 0;

            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var frame = requestStream.Current;
                if (description is not null)
                {
                    throw new InvalidDataException("the upload description must be the terminal frame");
                }

                switch (frame.PayloadCase)
                {
                    case AttachmentUploadFrame.PayloadOneofCase.Chunk:
                        if (frame.Chunk.Length is 0 or > 1024 * 1024)
                        {
                            throw new InvalidDataException("an upload chunk must contain at most 1 MiB");
                        }

                        if (content.Length > ImageArtifactLimits.MaximumImageBytes - frame.Chunk.Length)
                        {
                            throw new InvalidDataException("an image cannot exceed 5 MiB");
                        }

                        await content.WriteAsync(frame.Chunk.Memory, context.CancellationToken).ConfigureAwait(false);
                        chunks++;
                        break;
                    case AttachmentUploadFrame.PayloadOneofCase.Description:
                        if (chunks == 0)
                        {
                            throw new InvalidDataException("an upload needs at least one chunk");
                        }

                        description = frame.Description;
                        break;
                    default:
                        throw new InvalidDataException("upload frames must contain chunks followed by one description");
                }
            }

            if (description is null)
            {
                throw new InvalidDataException("an upload needs a terminal description");
            }

            ValidateUploadDescription(description);
            content.Position = 0;
            var artifact = await session.Images.Persist(
                content,
                header.UploadId,
                description.DisplayName,
                "upload",
                description.MediaType,
                description.ByteLength,
                context.CancellationToken).ConfigureAwait(false);
            return new AttachmentUploadResponse { Artifact = ToProtocol(artifact) };
        }
        catch (Exception failure) when (failure is InvalidDataException or ArgumentException or InputConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, failure.Message));
        }
    }

    public override async Task<SendMessageResponse> SendMessage(
        SendMessageRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // Refused rather than defaulted: a default would decide silently
        // whether this prompt interrupts the work in flight or waits for it.
        if (request.Delivery is not (Delivery.Steer or Delivery.Queue))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "a prompt needs a delivery"));
        }

        var found = Find(request.UserSessionId);
        if (request.Text.Length > 0 && request.Parts.Count > 0)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "legacy text and structured prompt parts are mutually exclusive"));
        }

        IReadOnlyList<ConversationPart> parts;
        try
        {
            parts = request.Parts.Count == 0
                ? [ConversationPart.TextPart(request.Text)]
                : ResolveParts(found, request.Parts);
        }
        catch (InvalidDataException failure)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, failure.Message));
        }

        // The sender's id when they supplied one, so a re-send after a dropped
        // connection is recognisable as the same prompt rather than a second.
        var messageId = request.MessageId.Length > 0 ? request.MessageId : Identifier.MessageId();

        Admission admitted;

        try
        {
            admitted = await found.Send(parts, messageId, request.Delivery, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (InputConflictException conflict)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, conflict.Message));
        }

        return new SendMessageResponse
        {
            MessageId = admitted.Input.MessageId,
            InputId = admitted.Input.Id,
            Created = admitted.Created,
        };
    }

    // Returns once the turn has stopped and every tool call has settled, so a
    // client that sends again cannot race the turn it just stopped.
    public override async Task<InterruptResponse> Interrupt(InterruptRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        await Find(request.UserSessionId).Interrupt(context.CancellationToken).ConfigureAwait(false);

        return new InterruptResponse();
    }

    public override Task<ListPendingQuestionsResponse> ListPendingQuestions(
        ListPendingQuestionsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var response = new ListPendingQuestionsResponse();
        response.Questions.AddRange(Find(request.UserSessionId).Questions.Pending().Select(ToProtocol));
        return Task.FromResult(response);
    }

    public override Task<ReplyQuestionResponse> ReplyQuestion(ReplyQuestionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (request.QuestionRequestId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "a question request id is required"));
        }

        try
        {
            Find(request.UserSessionId).Questions.Reply(
                request.QuestionRequestId,
                new QuestionReply([.. request.Answers.Select(answer => new global::Parrot.Questions.QuestionAnswer(
                    answer.QuestionId,
                    [.. answer.OptionIds],
                    answer.Custom))]));
            return Task.FromResult(new ReplyQuestionResponse());
        }
        catch (QuestionException failure)
        {
            throw new RpcException(new Status(
                failure.Message.StartsWith("question request not found:", StringComparison.Ordinal)
                    ? StatusCode.NotFound
                    : StatusCode.InvalidArgument,
                failure.Message));
        }
    }

    public override Task<RejectQuestionResponse> RejectQuestion(RejectQuestionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (request.QuestionRequestId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "a question request id is required"));
        }

        try
        {
            Find(request.UserSessionId).Questions.Reject(request.QuestionRequestId);
            return Task.FromResult(new RejectQuestionResponse());
        }
        catch (QuestionException failure)
        {
            throw new RpcException(new Status(StatusCode.NotFound, failure.Message));
        }
    }

    public override Task<ListPendingPermissionsResponse> ListPendingPermissions(
        ListPendingPermissionsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var response = new ListPendingPermissionsResponse();
        response.Permissions.AddRange(Find(request.UserSessionId).Permissions.Pending().Select(ToProtocol));
        return Task.FromResult(response);
    }

    public override Task<ReplyPermissionResponse> ReplyPermission(
        ReplyPermissionRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (request.PermissionRequestId.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "a permission request id is required"));
        }

        try
        {
            Find(request.UserSessionId).Permissions.Reply(
                request.PermissionRequestId,
                request.ChoiceValue,
                request.Reason);
            return Task.FromResult(new ReplyPermissionResponse());
        }
        catch (PermissionNotFoundException failure)
        {
            throw new RpcException(new Status(StatusCode.NotFound, failure.Message));
        }
        catch (PermissionException failure)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, failure.Message));
        }
    }

    // Indefinite. It ends when the client stops listening or the call is
    // cancelled -- not when a turn finishes, because a subagent spawned by the
    // agent keeps publishing to this same stream long afterwards.
    public override async Task Listen(
        ListenRequest request,
        IServerStreamWriter<Event> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var found = Find(request.UserSessionId);

        await foreach (var published in found.Listen(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(published, context.CancellationToken).ConfigureAwait(false);
        }
    }

    // M1 keeps every user session for the process lifetime, which is right for
    // a one-shot CLI. Evicting an idle session is an M6 concern, when a server
    // outlives the sessions it hosts.
    //
    // All at once, not one after another: each session cancels its own drains
    // first thing, so starting them together makes shutdown as long as the
    // slowest session rather than as long as all of them added up.
    public ValueTask DisposeAsync() => _userSessions.DisposeAsync();

    private static async Task<AttachmentUploadHeader> ReadUploadHeader(
        IAsyncStreamReader<AttachmentUploadFrame> requestStream,
        CancellationToken cancellationToken)
    {
        if (!await requestStream.MoveNext(cancellationToken).ConfigureAwait(false)
            || requestStream.Current.PayloadCase != AttachmentUploadFrame.PayloadOneofCase.Header)
        {
            throw new InvalidDataException("the first upload frame must be a header");
        }

        var header = requestStream.Current.Header;
        if (string.IsNullOrWhiteSpace(header.UserSessionId) || string.IsNullOrWhiteSpace(header.UploadId))
        {
            throw new InvalidDataException("an upload header needs a user session id and upload id");
        }

        return header;
    }

    private static void ValidateUploadDescription(AttachmentUploadDescription description)
    {
        if (string.IsNullOrWhiteSpace(description.DisplayName)
            || !string.Equals(description.DisplayName, Path.GetFileName(description.DisplayName), StringComparison.Ordinal)
            || description.DisplayName.Any(char.IsControl))
        {
            throw new InvalidDataException("an upload display name must be a basename");
        }

        if (string.IsNullOrWhiteSpace(description.MediaType) || description.ByteLength <= 0)
        {
            throw new InvalidDataException("an upload description needs a media type and positive byte length");
        }
    }

    private static List<ConversationPart> ResolveParts(
        Agent.UserSession session,
        IEnumerable<MessageContentPart> requested)
    {
        var parts = new List<ConversationPart>();
        var imageCount = 0;
        long imageBytes = 0;
        foreach (var part in requested)
        {
            switch (part.ContentCase)
            {
                case MessageContentPart.ContentOneofCase.Text:
                    parts.Add(ConversationPart.TextPart(part.Text));
                    break;
                case MessageContentPart.ContentOneofCase.ArtifactId:
                    var artifact = session.Images.Resolve(part.ArtifactId)
                        ?? throw new InvalidDataException($"image artifact {part.ArtifactId} does not belong to this session");
                    imageCount++;
                    imageBytes += artifact.ByteLength;
                    if (imageCount > ImageArtifactLimits.MaximumImages
                        || imageBytes > ImageArtifactLimits.MaximumBatchBytes)
                    {
                        throw new InvalidDataException("the prompt image batch exceeds the supported limits");
                    }

                    parts.Add(ConversationPart.ImageArtifact(artifact));
                    break;
                default:
                    throw new InvalidDataException("every prompt part needs text or an artifact id");
            }
        }

        if (parts.Count == 0)
        {
            throw new InvalidDataException("a structured prompt needs at least one part");
        }

        return parts;
    }

    private static ArtifactReference ToProtocol(ImageArtifactMetadata artifact) => new()
    {
        ArtifactId = artifact.ArtifactId,
        Sha256 = artifact.Sha256,
        MediaType = artifact.MediaType,
        ByteLength = artifact.ByteLength,
        Width = artifact.Width,
        Height = artifact.Height,
        FrameCount = artifact.FrameCount,
        AggregatePixels = artifact.AggregatePixels,
        DisplayName = artifact.DisplayName,
        Origin = artifact.Origin,
    };

    private static PendingQuestion ToProtocol(PendingQuestionRequest request)
    {
        var pending = new PendingQuestion { Id = request.Id };
        pending.Questions.AddRange(request.Questions.Select(question =>
        {
            var converted = new QuestionDefinition
            {
                Id = question.Id,
                Header = question.Header,
                Prompt = question.Prompt,
                Multiple = question.Multiple,
                Custom = question.Custom,
            };
            converted.Options.AddRange(question.Options.Select(option => new QuestionOption
            {
                Id = option.Id,
                Label = option.Label,
            }));
            return converted;
        }));
        return pending;
    }

    private static PendingPermission ToProtocol(PermissionPending request)
    {
        var pending = new PendingPermission
        {
            Id = request.Id,
            AgentSessionId = request.AgentSessionId,
            Reason = request.Reason,
        };
        pending.Targets.AddRange(request.Targets.Select(target => new PermissionTarget
        {
            Kind = target.Kind == SecurityWriteTargetKind.File
                ? PermissionTargetKind.File
                : PermissionTargetKind.Directory,
            Scope = PermissionTargetScope.Write,
            Path = target.Path,
        }));
        pending.Choices.AddRange(request.Choices.Select(choice => new global::Parrot.Protocol.PermissionChoice
        {
            Value = choice.Value,
            Label = choice.Label,
            Action = choice.Decision == PermissionDecision.Grant
                ? PermissionAction.Allow
                : PermissionAction.Deny,
            RequiresReason = choice.RequiresReason,
        }));
        return pending;
    }

    private static ModelAlias ToProtocol(ModelAliasDefinition definition) =>
        new() { Name = definition.Name, ModelString = definition.ModelString, Usage = definition.Usage };

    private Agent.UserSession Find(string userSessionId) => _userSessions.Find(userSessionId);
}
