using Grpc.Core;

namespace Parrot.Protocol;

// The owning CLI reaches its service directly; attached CLIs use Kestrel.
// Only the call shapes the contract uses are implemented; the rest throw.
internal sealed class InProcessCallInvoker(ParrotService service) : CallInvoker
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method,
        string? host,
        CallOptions options,
        TRequest request)
    {
        var context = new InProcessServerCallContext(options.CancellationToken);

        return new AsyncUnaryCall<TResponse>(
            Unary<TResponse>(request, context),
            Task.FromResult(new Metadata()),
            static () => Status.DefaultSuccess,
            static () => [],
            static () => { });
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method,
        string? host,
        CallOptions options,
        TRequest request)
    {
        var writer = new ChannelStreamWriter<TResponse>();
        var context = new InProcessServerCallContext(options.CancellationToken);

        if (request is not ListenRequest listen || writer is not ChannelStreamWriter<Event> events)
        {
            throw new NotImplementedException($"no in-process route for {typeof(TRequest).Name}");
        }

        _ = Drain(listen, events, context);

        return new AsyncServerStreamingCall<TResponse>(
            writer.Reader,
            Task.FromResult(new Metadata()),
            static () => Status.DefaultSuccess,
            static () => [],
            writer.Complete);
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw new NotImplementedException("the contract has no blocking unary call");

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method,
        string? host,
        CallOptions options)
    {
        if (typeof(TRequest) != typeof(AttachmentUploadFrame)
            || typeof(TResponse) != typeof(AttachmentUploadResponse))
        {
            throw new NotImplementedException($"no in-process route for {typeof(TRequest).Name}");
        }

        var stream = new BoundedClientStream<AttachmentUploadFrame>();
        var context = new InProcessServerCallContext(options.CancellationToken);
        var response = Upload<TResponse>(stream, context);

        return new AsyncClientStreamingCall<TRequest, TResponse>(
            (IClientStreamWriter<TRequest>)(object)stream,
            response,
            Task.FromResult(new Metadata()),
            static () => Status.DefaultSuccess,
            static () => [],
            stream.Stop);
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options) =>
        throw new NotImplementedException("the contract has no duplex call");

    // The service call is started here rather than handed in, so the task
    // being awaited is one this method owns.
    private async Task<TResponse> Unary<TResponse>(object request, InProcessServerCallContext context)
        where TResponse : class
    {
        object answered = request switch
        {
            ListModelsRequest list => await service.ListModels(list, context).ConfigureAwait(false),
            ListModelAliasesRequest list => await service.ListModelAliases(list, context).ConfigureAwait(false),
            ConfigureModelAliasRequest configure =>
                await service.ConfigureModelAlias(configure, context).ConfigureAwait(false),
            ListProviderModelAliasDefaultsRequest list =>
                await service.ListProviderModelAliasDefaults(list, context).ConfigureAwait(false),
            ApplyProviderModelAliasDefaultsRequest apply =>
                await service.ApplyProviderModelAliasDefaults(apply, context).ConfigureAwait(false),
            SetModelPresetRequest setPreset =>
                await service.SetModelPreset(setPreset, context).ConfigureAwait(false),
            SelectModelPresetRequest selectPreset =>
                await service.SelectModelPreset(selectPreset, context).ConfigureAwait(false),
            ListModelPresetsRequest listPresets =>
                await service.ListModelPresets(listPresets, context).ConfigureAwait(false),
            ListModesRequest list => await service.ListModes(list, context).ConfigureAwait(false),
            ListSkillsRequest list => await service.ListSkills(list, context).ConfigureAwait(false),
            ConfigureSkillRequest configure => await service.ConfigureSkill(configure, context).ConfigureAwait(false),
            ListSessionsRequest list => await service.ListSessions(list, context).ConfigureAwait(false),
            CreateSessionRequest create => await service.CreateSession(create, context).ConfigureAwait(false),
            ResumeSessionRequest resume => await service.ResumeSession(resume, context).ConfigureAwait(false),
            AttachSessionRequest attach => await service.AttachSession(attach, context).ConfigureAwait(false),
            SetGoalRequest setGoal => await service.SetGoal(setGoal, context).ConfigureAwait(false),
            UpdateSessionRequest update => await service.UpdateSession(update, context).ConfigureAwait(false),
            SendMessageRequest send => await service.SendMessage(send, context).ConfigureAwait(false),
            InterruptRequest interrupt => await service.Interrupt(interrupt, context).ConfigureAwait(false),
            CompactRequest compact => await service.Compact(compact, context).ConfigureAwait(false),
            SetContextLimitRequest setContextLimit =>
                await service.SetContextLimit(setContextLimit, context).ConfigureAwait(false),
            SandboxEnableRequest sandboxEnable =>
                await service.SandboxEnable(sandboxEnable, context).ConfigureAwait(false),
            SessionStatusRequest sessionStatus =>
                await service.SessionStatus(sessionStatus, context).ConfigureAwait(false),
            ListPendingQuestionsRequest list =>
                await service.ListPendingQuestions(list, context).ConfigureAwait(false),
            ReplyQuestionRequest reply => await service.ReplyQuestion(reply, context).ConfigureAwait(false),
            RejectQuestionRequest reject => await service.RejectQuestion(reject, context).ConfigureAwait(false),
            ListPendingPermissionsRequest list =>
                await service.ListPendingPermissions(list, context).ConfigureAwait(false),
            ReplyPermissionRequest reply => await service.ReplyPermission(reply, context).ConfigureAwait(false),
            _ => throw new NotImplementedException($"no in-process route for {request.GetType().Name}"),
        };

        return answered as TResponse
            ?? throw new InvalidOperationException($"a {answered.GetType().Name} cannot answer a {typeof(TResponse).Name}");
    }

    private async Task<TResponse> Upload<TResponse>(
        BoundedClientStream<AttachmentUploadFrame> stream,
        InProcessServerCallContext context)
        where TResponse : class
    {
        try
        {
            var response = await service.UploadAttachment(stream, context).ConfigureAwait(false);
            return response as TResponse
                ?? throw new InvalidOperationException($"an attachment upload cannot answer a {typeof(TResponse).Name}");
        }
        finally
        {
            await stream.CompleteAsync().ConfigureAwait(false);
        }
    }

    // Task, not void: an async void that throws takes the process down. It
    // catches everything, so discarding the task at the call site loses
    // nothing.
    private async Task Drain(
        ListenRequest request,
        ChannelStreamWriter<Event> writer,
        InProcessServerCallContext context)
    {
        try
        {
            await service.Listen(request, writer, context).ConfigureAwait(false);
            writer.Complete();
        }
        catch (Exception failure)
        {
            // Deliberate containment: the failure travels to the client as a
            // faulted stream rather than as an unobserved task exception.
            writer.Fault(failure);
        }
    }
}
