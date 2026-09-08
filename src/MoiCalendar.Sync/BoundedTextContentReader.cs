using System.Text;

namespace MoiCalendar.Sync;

internal static class BoundedTextContentReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<string> ReadUtf8Async(
        HttpContent content,
        int maximumBytes,
        string tooLargeMessage,
        string invalidContentMessage,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new SyncContentTooLargeException(tooLargeMessage);
        }

        try
        {
            await using var source = await content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
            var chunk = new byte[8 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maximumBytes)
                {
                    throw new SyncContentTooLargeException(tooLargeMessage);
                }

                buffer.Write(chunk, 0, read);
            }

            return StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        }
        catch (SyncStorageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SyncStorageException(invalidContentMessage, exception);
        }
    }
}
