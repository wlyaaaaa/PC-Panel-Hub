using System.Text;

namespace HS2.CrystalOverlay.Core;

public static class BoundedLineReader
{
    public static async Task<string?> ReadAsync(
        TextReader reader,
        int maxCharacters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var readCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        readCancellation.CancelAfter(timeout);
        var builder = new StringBuilder(Math.Min(maxCharacters, 4096));
        var buffer = new char[Math.Min(maxCharacters + 1, 4096)];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(
                    buffer.AsMemory(),
                    readCancellation.Token);
                if (count == 0)
                {
                    return builder.Length == 0
                        ? null
                        : builder.ToString().TrimEnd('\r');
                }

                for (var index = 0; index < count; index++)
                {
                    var character = buffer[index];
                    if (character == '\n')
                    {
                        return builder.ToString().TrimEnd('\r');
                    }

                    if (builder.Length >= maxCharacters)
                    {
                        return null;
                    }

                    builder.Append(character);
                }
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
