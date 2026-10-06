using System.Reflection;

namespace SpireAiCoach.Core;

// Diagnostic data only. Neither classification nor a caught exception certifies
// a partial candidate or authorizes dropping a model effect.
public sealed record LocalSimulationFailure(string ExceptionType, string Message,
    string? Method, string Stack, int? Worker = null, string? Stage = null, string? Category = null,
    string? ExpectedNativeHash = null, string? ActualNativeHash = null)
{
    public static LocalSimulationFailure Capture(Exception exception, int? worker = null, string? stage = null)
    {
        var cause = exception;
        while (true)
        {
            if (cause is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
                cause = aggregate.Flatten().InnerExceptions[0];
            else if (cause is TargetInvocationException { InnerException: { } inner }) cause = inner;
            else break;
        }
        var method = cause.TargetSite;
        return new(cause.GetType().FullName ?? cause.GetType().Name, cause.Message,
            method == null ? null : method.DeclaringType?.FullName + "." + method.Name,
            exception.ToString(), worker, stage, cause is LocalIpcBusyException ? "local_ipc" : (cause as CoachException)?.Category,
            cause.Data["expected_native_hash"] as string, cause.Data["actual_native_hash"] as string);
    }
}
