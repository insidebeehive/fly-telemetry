namespace Beehive.Telemetry.Http;

/// <summary>
/// Pass-through write stream that counts every response byte and keeps the first
/// <c>keep</c> of them. Observe-only: every call is forwarded to the real body stream
/// first-class, and a capture failure can never fail the response.
/// </summary>
internal sealed class ResponseCaptureStream : Stream
{
    private readonly Stream inner;
    private readonly byte[]? buffer;
    private int kept;
    private long total;

    internal ResponseCaptureStream(Stream inner, int keep)
    {
        this.inner = inner;
        buffer = keep > 0 ? new byte[keep] : null;
    }

    /// <summary>Total bytes written, including anything past the cap.</summary>
    internal long Total => Interlocked.Read(ref total);

    /// <summary>The kept prefix of the response body.</summary>
    internal ReadOnlySpan<byte> Captured => buffer is null ? default : buffer.AsSpan(0, kept);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Capture(new ReadOnlySpan<byte>(buffer, offset, count));
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Capture(buffer);
        inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Capture(new ReadOnlySpan<byte>(buffer, offset, count));
        return inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Capture(buffer.Span);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    // Deliberately NOT disposing `inner`: the server owns the real body stream.
    protected override void Dispose(bool disposing)
    {
        // no-op
    }

    public override ValueTask DisposeAsync() => default;

    private void Capture(ReadOnlySpan<byte> data)
    {
        try
        {
            Interlocked.Add(ref total, data.Length);
            if (buffer is null)
            {
                return;
            }

            var room = buffer.Length - kept;
            if (room <= 0)
            {
                return;
            }

            var take = Math.Min(room, data.Length);
            data[..take].CopyTo(buffer.AsSpan(kept));
            kept += take;
        }
        catch (Exception)
        {
            // Observe-only — a capture failure must never touch the response.
        }
    }
}
