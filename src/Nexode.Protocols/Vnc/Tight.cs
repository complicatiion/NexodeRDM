using System.Buffers.Binary;
using System.IO.Compression;

namespace Nexode.Protocols.Vnc;

/// <summary>
/// Tight encoding (TightVNC / TigerVNC / LibVNCServer): fill, JPEG, and "basic" rectangles with four persistent zlib
/// streams and the copy, palette and gradient filters. With the 32 bpp / depth 24 pixel format a pixel (TPIXEL) is
/// 3 bytes R, G, B.
/// </summary>
internal sealed class TightDecoder(JpegDecoder? jpeg) : IDisposable
{
    private const int MinToCompress = 12;
    private readonly ZStream?[] _streams = new ZStream?[4];

    public async Task DecodeAsync(RfbClient c, int x, int y, int w, int h, CancellationToken ct)
    {
        var control = (await c.ReadAsync(1, ct).ConfigureAwait(false))[0];
        for (var i = 0; i < 4; i++)
        {
            if ((control & (1 << i)) == 0) continue;
            _streams[i]?.Dispose();
            _streams[i] = null;
        }

        var type = control >> 4;
        if (type == 0x08)
        {
            var px = await c.ReadAsync(3, ct).ConfigureAwait(false);
            lock (c.FrameLock) c.Fill(x, y, w, h, Rgb(px, 0));
            return;
        }

        if (type == 0x09)
        {
            var len = await CompactLengthAsync(c, ct).ConfigureAwait(false);
            var data = await c.ReadAsync(len, ct).ConfigureAwait(false);
            var pixels = jpeg?.Invoke(data, w, h) ?? throw new InvalidDataException("JPEG rectangle without a JPEG decoder.");
            lock (c.FrameLock) c.BlitPixels(pixels, x, y, w, h);
            return;
        }

        if (type > 0x07) throw new InvalidDataException($"Invalid Tight compression type {type}.");

        var streamId = type & 3;
        var filter = (type & 4) != 0 ? (await c.ReadAsync(1, ct).ConfigureAwait(false))[0] : 0;
        uint[]? palette = null;
        var bitsPerPixel = 24;
        if (filter == 1)
        {
            var n = (await c.ReadAsync(1, ct).ConfigureAwait(false))[0] + 1;
            var raw = await c.ReadAsync(n * 3, ct).ConfigureAwait(false);
            palette = new uint[n];
            for (var i = 0; i < n; i++) palette[i] = Rgb(raw, i * 3);
            bitsPerPixel = n == 2 ? 1 : 8;
        }
        else if (filter is not (0 or 2))
        {
            throw new InvalidDataException($"Invalid Tight filter {filter}.");
        }

        var rowBytes = bitsPerPixel == 1 ? (w + 7) / 8 : bitsPerPixel == 8 ? w : w * 3;
        var size = rowBytes * h;
        byte[] buf;
        if (size < MinToCompress)
        {
            buf = await c.ReadAsync(size, ct).ConfigureAwait(false);
        }
        else
        {
            var len = await CompactLengthAsync(c, ct).ConfigureAwait(false);
            var compressed = await c.ReadAsync(len, ct).ConfigureAwait(false);
            var z = _streams[streamId] ??= new ZStream();
            buf = z.Inflate(compressed, size);
        }

        var pixelsOut = new uint[w * h];
        if (palette is not null)
        {
            for (var row = 0; row < h; row++)
            {
                for (var col = 0; col < w; col++)
                {
                    var index = bitsPerPixel == 1 ? (buf[row * rowBytes + col / 8] >> (7 - col % 8)) & 1 : buf[row * rowBytes + col];
                    pixelsOut[row * w + col] = index < palette.Length ? palette[index] : 0;
                }
            }
        }
        else if (filter == 2)
        {
            Gradient(buf, w, h, pixelsOut);
        }
        else
        {
            for (var i = 0; i < w * h; i++) pixelsOut[i] = Rgb(buf, i * 3);
        }

        lock (c.FrameLock) c.BlitPixels(pixelsOut, x, y, w, h);
    }

    /// <summary>Gradient filter: each component is the difference to left + above − above-left (clamped).</summary>
    internal static void Gradient(byte[] buf, int w, int h, uint[] output)
    {
        var prev = new int[w * 3];
        var cur = new int[w * 3];
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                for (var k = 0; k < 3; k++)
                {
                    var left = col > 0 ? cur[(col - 1) * 3 + k] : 0;
                    var up = prev[col * 3 + k];
                    var upLeft = col > 0 ? prev[(col - 1) * 3 + k] : 0;
                    var predicted = Math.Clamp(left + up - upLeft, 0, 255);
                    cur[col * 3 + k] = (buf[(row * w + col) * 3 + k] + predicted) & 0xFF;
                }

                output[row * w + col] = 0xFF000000u | (uint)(cur[col * 3] << 16 | cur[col * 3 + 1] << 8 | cur[col * 3 + 2]);
            }

            (prev, cur) = (cur, prev);
        }
    }

    private static uint Rgb(byte[] b, int o) => 0xFF000000u | (uint)(b[o] << 16 | b[o + 1] << 8 | b[o + 2]);

    private static async Task<int> CompactLengthAsync(RfbClient c, CancellationToken ct)
    {
        var length = 0;
        for (var i = 0; i < 3; i++)
        {
            var b = (await c.ReadAsync(1, ct).ConfigureAwait(false))[0];
            length |= (i < 2 ? b & 0x7F : b) << (7 * i);
            if (i < 2 && (b & 0x80) == 0) break;
        }

        if (length > 64 << 20) throw new InvalidDataException("Tight rectangle too large.");
        return length;
    }

    public void Dispose()
    {
        foreach (var s in _streams) s?.Dispose();
    }

    /// <summary>Persistent zlib stream fed chunk by chunk.</summary>
    private sealed class ZStream : IDisposable
    {
        private readonly ZrleInflater.FeedStream _feed = new();
        private readonly ZLibStream _zlib;

        public ZStream() => _zlib = new ZLibStream(_feed, CompressionMode.Decompress);

        public byte[] Inflate(byte[] compressed, int size)
        {
            _feed.Append(compressed);
            var result = new byte[size];
            _zlib.ReadExactly(result);
            return result;
        }

        public void Dispose() => _zlib.Dispose();
    }
}
