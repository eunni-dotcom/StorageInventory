using System.Security;

namespace StorageInventory.Core.Scanning;

/// <summary>Maps exceptions to <see cref="ScanErrorType"/>, walking inner exceptions (same order as the reference).</summary>
internal static class ScanErrorClassifier
{
    public static (ScanErrorType Type, string Message) Classify(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            ScanErrorType? type = current switch
            {
                UnauthorizedAccessException or SecurityException => ScanErrorType.AccessDenied,
                PathTooLongException => ScanErrorType.PathTooLong,
                DirectoryNotFoundException or FileNotFoundException => ScanErrorType.NotFound,
                IOException => ScanErrorType.IOError,
                ArgumentException or NotSupportedException => ScanErrorType.InvalidPath,
                _ => null,
            };
            if (type is { } t) return (t, current.Message);
        }
        return (ScanErrorType.UnexpectedError, exception.Message);
    }
}
