namespace Bashkiria.Events.Parser.Storage;

public sealed class RawDocumentStorageException(string message, Exception? innerException = null)
    : Exception(message, innerException);
