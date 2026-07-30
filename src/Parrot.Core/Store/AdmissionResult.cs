namespace Parrot.Store;

internal sealed class AdmissionResult
{
    public AdmissionResult(
        ClaimDisposition disposition,
        UserSessionId? sessionId,
        SessionActivationLease? activationLease,
        OpenIntent? intent)
    {
        Disposition = disposition;
        SessionId = sessionId;
        ActivationLease = activationLease;
        Intent = intent;
    }

    public AdmissionResult(
        ClaimDisposition disposition,
        OpenIntent intent,
        WorkingDirectoryClaim claim,
        string leaseId,
        long generation,
        string runtimeInstanceId)
    {
        Disposition = disposition;
        SessionId = intent.SessionId;
        ActivationLease = new SessionActivationLease(
            claim, intent, leaseId, generation, runtimeInstanceId);
        Intent = intent;
    }

    public ClaimDisposition Disposition { get; }

    public UserSessionId? SessionId { get; }

    public SessionActivationLease? ActivationLease { get; }

    public OpenIntent? Intent { get; }
}
