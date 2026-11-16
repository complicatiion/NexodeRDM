using System.Buffers.Binary;
using Xunit;

namespace Nexode.Protocols.Tests;

public class AgentTests
{
    [Fact]
    public void SignRequestLayout()
    {
        var req = SshAgent.BuildSignRequest([1, 2, 3], [9, 9], 4);
        Assert.Equal(13, req[0]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(1)));
        Assert.Equal(new byte[] { 1, 2, 3 }, req[5..8]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(8)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(req.Length - 4)));
    }

    [Fact]
    public void ExchangeIsLengthFramed()
    {
        // Response prepared in the stream after the request: length 2, body {12, 0}.
        var duplex = new LoopStream([0, 0, 0, 2, 12, 0]);
        var answer = SshAgent.Exchange(duplex, [11]);
        Assert.Equal(new byte[] { 12, 0 }, answer);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 11 }, duplex.Written.ToArray());
    }

    private sealed class LoopStream(byte[] response) : Stream
    {
        private readonly MemoryStream _in = new(response);
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }
}
