using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Web;

// The browser's view of the Parrot service. Calls for a session hosted by
// another parrot process are forwarded to it; everything else is answered here.
internal sealed class WebParrotProxy(GeneratedParrot.ParrotBase service, WebSessionRouter router) : GeneratedParrot.ParrotBase
{
    public override Task<ListModelsResponse> ListModels(ListModelsRequest request, ServerCallContext context) =>
        service.ListModels(request, context);

    public override Task<ListModelAliasesResponse> ListModelAliases(ListModelAliasesRequest request, ServerCallContext context) =>
        service.ListModelAliases(request, context);

    public override Task<ConfigureModelAliasResponse> ConfigureModelAlias(ConfigureModelAliasRequest request, ServerCallContext context) =>
        service.ConfigureModelAlias(request, context);

    public override Task<ListProviderModelAliasDefaultsResponse> ListProviderModelAliasDefaults(ListProviderModelAliasDefaultsRequest request, ServerCallContext context) =>
        service.ListProviderModelAliasDefaults(request, context);

    public override Task<ApplyProviderModelAliasDefaultsResponse> ApplyProviderModelAliasDefaults(ApplyProviderModelAliasDefaultsRequest request, ServerCallContext context) =>
        service.ApplyProviderModelAliasDefaults(request, context);

    public override Task<SetModelPresetResponse> SetModelPreset(SetModelPresetRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.SetModelPresetAsync(request, cancellationToken: context.CancellationToken));

    public override Task<SelectModelPresetResponse> SelectModelPreset(SelectModelPresetRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.SelectModelPresetAsync(request, cancellationToken: context.CancellationToken));

    public override Task<ListModelPresetsResponse> ListModelPresets(ListModelPresetsRequest request, ServerCallContext context) =>
        service.ListModelPresets(request, context);

    public override Task<ListModesResponse> ListModes(ListModesRequest request, ServerCallContext context) =>
        service.ListModes(request, context);

    public override Task<ListSkillsResponse> ListSkills(ListSkillsRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.ListSkillsAsync(request, cancellationToken: context.CancellationToken));

    public override Task<ConfigureSkillResponse> ConfigureSkill(ConfigureSkillRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.ConfigureSkillAsync(request, cancellationToken: context.CancellationToken));

    public override Task<UserSession> CreateSession(CreateSessionRequest request, ServerCallContext context) =>
        service.CreateSession(request, context);

    public override Task<UserSession> UpdateSession(UpdateSessionRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.UpdateSessionAsync(request, cancellationToken: context.CancellationToken));

    public override Task<SetGoalResponse> SetGoal(SetGoalRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.SetGoalAsync(request, cancellationToken: context.CancellationToken));

    public override Task<SendMessageResponse> SendMessage(SendMessageRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.SendMessageAsync(request, cancellationToken: context.CancellationToken));

    public override Task<InterruptResponse> Interrupt(InterruptRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.InterruptAsync(request, cancellationToken: context.CancellationToken));

    public override Task<CompactResponse> Compact(CompactRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.CompactAsync(request, cancellationToken: context.CancellationToken));

    public override Task<SetContextLimitResponse> SetContextLimit(SetContextLimitRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.SetContextLimitAsync(request, cancellationToken: context.CancellationToken));

    public override Task<SandboxEnableResponse> SandboxEnable(SandboxEnableRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.SandboxEnableAsync(request, cancellationToken: context.CancellationToken));

    public override Task<SessionStatusResponse> SessionStatus(SessionStatusRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.SessionStatusAsync(request, cancellationToken: context.CancellationToken));

    public override Task<ListPendingQuestionsResponse> ListPendingQuestions(ListPendingQuestionsRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.ListPendingQuestionsAsync(request, cancellationToken: context.CancellationToken));

    public override Task<ReplyQuestionResponse> ReplyQuestion(ReplyQuestionRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.ReplyQuestionAsync(request, cancellationToken: context.CancellationToken));

    public override Task<RejectQuestionResponse> RejectQuestion(RejectQuestionRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.RejectQuestionAsync(request, cancellationToken: context.CancellationToken));

    public override Task<ListPendingPermissionsResponse> ListPendingPermissions(ListPendingPermissionsRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.ListPendingPermissionsAsync(request, cancellationToken: context.CancellationToken));

    public override Task<ReplyPermissionResponse> ReplyPermission(ReplyPermissionRequest request, ServerCallContext context) =>
        Forward(request.UserSessionId, client => client.ReplyPermissionAsync(request, cancellationToken: context.CancellationToken));

    public override Task<ListSessionsResponse> ListSessions(ListSessionsRequest request, ServerCallContext context) =>
        service.ListSessions(request, context);

    public override async Task<UserSession> ResumeSession(ResumeSessionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!router.IsRemote(request.UserSessionId))
        {
            try
            {
                return await service.ResumeSession(request, context).ConfigureAwait(false);
            }
            catch (RpcException failure) when (failure.StatusCode == StatusCode.AlreadyExists)
            {
            }
        }

        return await AttachSession(
            new AttachSessionRequest { UserSessionId = request.UserSessionId, WorkingDirectory = request.WorkingDirectory },
            context).ConfigureAwait(false);
    }

    public override async Task<UserSession> AttachSession(AttachSessionRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (router.IsRemote(request.UserSessionId))
        {
            return await Forward(
                request.UserSessionId,
                client => client.AttachSessionAsync(request, cancellationToken: context.CancellationToken)).ConfigureAwait(false);
        }

        try
        {
            return await service.AttachSession(request, context).ConfigureAwait(false);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.NotFound)
        {
            return await router.AttachRemote(request, context.CancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<AttachmentUploadResponse> UploadAttachment(
        IAsyncStreamReader<AttachmentUploadFrame> requestStream, ServerCallContext context) =>
        service.UploadAttachment(requestStream, context);

    public override async Task Listen(ListenRequest request, IServerStreamWriter<Event> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            using var call = router.For(request.UserSessionId).Listen(request, cancellationToken: context.CancellationToken);
            await foreach (var item in call.ResponseStream.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            {
                router.Observe(request.UserSessionId, item);
                await responseStream.WriteAsync(item, context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.Unavailable)
        {
            router.Forget(request.UserSessionId);
            throw;
        }
    }

    // A session whose process went away is forgotten, so reopening it can
    // resume it here.
    private async Task<T> Forward<T>(string userSessionId, Func<GeneratedParrot.ParrotClient, AsyncUnaryCall<T>> call)
    {
        try
        {
            using var pending = call(router.For(userSessionId));
            return await pending.ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.Unavailable)
        {
            router.Forget(userSessionId);
            throw;
        }
    }
}
