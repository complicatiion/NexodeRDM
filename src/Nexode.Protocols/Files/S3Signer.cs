using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nexode.Protocols.Files;

/// <summary>AWS Signature Version 4 for S3 requests (header based).</summary>
public static class S3Signer
{
    public const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    public const string Unsigned = "UNSIGNED-PAYLOAD";

    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>RFC 3986 encoding as S3 expects it (slash kept in paths).</summary>
    public static string Encode(string value, bool keepSlash)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~' || (keepSlash && c == '/')) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Computes the Authorization header. <paramref name="headers"/> must contain every header to sign (host,
    /// x-amz-date, x-amz-content-sha256 …), names in any case. <paramref name="canonicalPath"/> is already encoded.
    /// </summary>
    public static string Authorization(string method, string canonicalPath, IEnumerable<KeyValuePair<string, string>> query,
        IReadOnlyDictionary<string, string> headers, string payloadHash, string accessKey, string secretKey, string region, DateTime utc,
        string service = "s3")
    {
        var amzDate = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var day = amzDate[..8];
        var canonicalQuery = string.Join('&', query
            .Select(q => (K: Encode(q.Key, false), V: Encode(q.Value, false)))
            .OrderBy(q => q.K, StringComparer.Ordinal).ThenBy(q => q.V, StringComparer.Ordinal)
            .Select(q => q.K + "=" + q.V));
        var sorted = headers.Select(h => (K: h.Key.ToLowerInvariant().Trim(), V: string.Join(' ', h.Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))))
            .OrderBy(h => h.K, StringComparer.Ordinal).ToList();
        var canonicalHeaders = string.Concat(sorted.Select(h => h.K + ":" + h.V + "\n"));
        var signedHeaders = string.Join(';', sorted.Select(h => h.K));
        var canonical = $"{method}\n{canonicalPath}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var scope = $"{day}/{region}/{service}/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hash(Encoding.UTF8.GetBytes(canonical))}";
        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), day);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(key, toSign));
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
