using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Payroll.API.Models;

namespace Payroll.API.Services;

internal sealed record ExcelPayslipConsolidationCatalogGroup(int ClientId, string GroupId, string CanonicalBatchId,
    string CanonicalHash, IReadOnlyDictionary<string, string> SourceHashes);

internal static class ExcelPayslipConsolidationCatalog
{
    internal const int SnapshotPairsPerQuery = 128;
    internal const int MaximumSnapshotBytes = 64 * 1024 * 1024;

    internal static string? DecodeSnapshot(byte[]? payload, bool optional)
    {
        if (payload is null)
        {
            if (optional) return null;
            throw new InvalidOperationException("Saved snapshot compression is unavailable; no consolidation binding was assumed.");
        }
        if (payload.Length == 0) return ""; // MySQL COMPRESS preserves an empty input as an empty binary string.
        try
        {
            if (payload.Length < 4 || payload.Length > MaximumSnapshotBytes) throw new InvalidDataException();
            var length = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            if (length > MaximumSnapshotBytes) throw new InvalidDataException();
            using var input = new MemoryStream(payload,4,payload.Length-4,false);
            using var inflater = new ZLibStream(input,CompressionMode.Decompress);
            using var output = new MemoryStream((int)Math.Min(length,81920));
            var buffer = new byte[81920]; int count;
            while ((count = inflater.Read(buffer)) != 0)
            {
                if (output.Length+count > length) throw new InvalidDataException();
                output.Write(buffer,0,count);
            }
            if (output.Length != length) throw new InvalidDataException();
            var bytes = output.GetBuffer().AsSpan(0,(int)output.Length);
            uint first = 1, second = 0;
            foreach (var value in bytes) { first = (first+value)%65521; second = (second+first)%65521; }
            var checksum = (second<<16)|first;
            // MySQL may append one period to protect a compressed value whose last byte is a space.
            var complete = payload.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(payload.Length-4)) == checksum;
            if (!complete && !(payload.Length >= 9 && payload[^1] == (byte)'.'
                && BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(payload.Length-5,4)) == checksum)) throw new InvalidDataException();
            return new UTF8Encoding(false,true).GetString(bytes);
        }
        catch (Exception failure) when (failure is InvalidDataException or DecoderFallbackException or IOException)
        {
            throw new InvalidOperationException("Saved snapshot compression could not be verified; no consolidation binding was assumed.",failure);
        }
    }

    internal static IReadOnlyList<(int ClientId, string BatchId)[]> SnapshotQueries(IEnumerable<ExcelPayslipConsolidationCatalogGroup> groups) =>
        groups.SelectMany(group => group.SourceHashes.Keys.Append(group.CanonicalBatchId).Select(id => (group.ClientId, BatchId: id)))
            .Distinct().OrderBy(key => key.ClientId).ThenBy(key => key.BatchId, StringComparer.Ordinal)
            .Chunk(SnapshotPairsPerQuery).ToArray();

    internal static IReadOnlyList<ExcelPayslipBatchSummary> Visible(IReadOnlyList<ExcelPayslipBatchSummary> summaries,
        IReadOnlyList<ExcelPayslipConsolidationCatalogGroup> groups,
        IReadOnlyDictionary<(int ClientId, string BatchId), string> currentHashes, bool includeConsolidated = false)
    {
        // Every binding is recomputed from the current read snapshot, never cached across requests.
        var fresh = groups.ToDictionary(group => (group.ClientId, group.GroupId), group =>
        {
            currentHashes.TryGetValue((group.ClientId, group.CanonicalBatchId), out var canonicalHash);
            var sources = group.SourceHashes.Keys.ToDictionary(id => id,
                id => currentHashes.TryGetValue((group.ClientId, id), out var hash) ? hash : null, StringComparer.Ordinal);
            return ExcelPayslipSavedConsolidation.SnapshotBindingsMatch(canonicalHash, group.CanonicalHash, sources, group.SourceHashes);
        });
        foreach (var summary in summaries)
        {
            var source = groups.SingleOrDefault(group => group.ClientId == summary.ClientId && group.SourceHashes.ContainsKey(summary.Id));
            var canonical = groups.SingleOrDefault(group => group.ClientId == summary.ClientId && group.CanonicalBatchId == summary.Id);
            if (source is not null)
            {
                summary.IsConsolidatedSource = true; summary.ConsolidationStale = !fresh[(source.ClientId, source.GroupId)];
                summary.CanonicalBatchId = source.CanonicalBatchId; summary.ConsolidationGroupId = source.GroupId;
            }
            if (canonical is not null)
            {
                summary.ConsolidationStale = !fresh[(canonical.ClientId, canonical.GroupId)];
                summary.ConsolidationGroupId = canonical.GroupId; summary.ConsolidatedSourceBatchIds = canonical.SourceHashes.Keys.ToList();
            }
        }
        return summaries.Where(summary => !ExcelPayslipSavedConsolidation.ShouldHideSource(
            summary.IsConsolidatedSource, !summary.ConsolidationStale, includeConsolidated)).ToArray();
    }
}
