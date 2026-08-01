using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MigrationStudio.Application.Validation;
using MigrationStudio.Domain.Validation;

namespace MigrationStudio.Validation;

public sealed class CanonicalChecksumService : ICanonicalChecksumService
{
    public string HashRow(IReadOnlyList<CanonicalValue> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values)
        {
            Append(hash, value.Kind.ToString());
            Append(hash, value.Representation);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public string HashOrderedRows(IEnumerable<IReadOnlyList<CanonicalValue>> rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in rows)
        {
            Append(hash, HashRow(row));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public string HashUnorderedRows(IEnumerable<IReadOnlyList<CanonicalValue>> rows)
    {
        var accumulator = new byte[32];
        long count = 0;
        foreach (var row in rows)
        {
            var rowHash = Convert.FromHexString(HashRow(row));
            AddUnsigned256(accumulator, rowHash);
            count++;
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(accumulator);
        Span<byte> countBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(countBytes, count);
        hash.AppendData(countBytes);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public string HashChunks(IEnumerable<string> chunkHashes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var chunk in chunkHashes)
        {
            Append(hash, chunk);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AddUnsigned256(Span<byte> accumulator, ReadOnlySpan<byte> value)
    {
        var carry = 0;
        for (var index = accumulator.Length - 1; index >= 0; index--)
        {
            var sum = accumulator[index] + value[index] + carry;
            accumulator[index] = (byte)sum;
            carry = sum >> 8;
        }
    }
}
