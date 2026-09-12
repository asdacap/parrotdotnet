namespace Parrot.Store;

internal sealed class SessionAdmissionException : InvalidOperationException
{
    public SessionAdmissionException()
    {
    }

    public SessionAdmissionException(string message)
        : base(message)
    {
    }

    public SessionAdmissionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SessionAdmissionException(AdmissionResult admission)
        : base($"Cannot activate session '{admission.SessionId}': {admission.Disposition}.") => Admission = admission;

    public AdmissionResult? Admission { get; }
}
