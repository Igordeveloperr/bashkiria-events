namespace Bashkiria.Events.Parser.Storage;

public sealed class ParserStorageException(string message, Exception? innerException = null)
    : Exception(message, innerException);
