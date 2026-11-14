using System.Buffers;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace Bashkiria.Events.Parser.Ingestion;

public static class InputBodyReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<string> ReadAsync(HttpRequest request, string mediaType, int maxBytes, CancellationToken token)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType.Value, mediaType, StringComparison.OrdinalIgnoreCase) ||
            contentType.Charset.HasValue && !string.Equals(contentType.Charset.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
            throw new IngestionRequestException(415, $"Используйте {mediaType} в кодировке UTF-8.");
        if (request.ContentLength > maxBytes) throw TooLarge(maxBytes);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await request.Body.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maxBytes - (int)buffer.Length + 1)), token);
                if (read == 0) break;
                if (buffer.Length + read > maxBytes) throw TooLarge(maxBytes);
                await buffer.WriteAsync(chunk.AsMemory(0, read), token);
            }
            if (buffer.Length == 0) throw new IngestionRequestException(400, "Тело запроса пустое.");
            try { return StrictUtf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length); }
            catch (DecoderFallbackException) { throw new IngestionRequestException(400, "Файл должен содержать корректный текст UTF-8."); }
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
    }

    private static IngestionRequestException TooLarge(int maxBytes) => new(413, $"Размер запроса превышает {maxBytes} байт.");
}
