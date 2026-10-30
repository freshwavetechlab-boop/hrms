using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipConsolidationCatalogTests
{
    private static byte[] Compressed(byte[] text, bool addPeriod = false)
    {
        using var buffer = new MemoryStream();
        buffer.Write(new byte[4]);
        using (var compressed = new ZLibStream(buffer,CompressionLevel.SmallestSize,true)) compressed.Write(text);
        if (addPeriod) buffer.WriteByte((byte)'.');
        var payload = buffer.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(payload,(uint)text.Length); return payload;
    }
    private const string Current = "cccccccccccccccccccccccccccccccc";
    private const string Older = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string CurrentHash = ExcelPayslipLegacyReview.SnapshotHash("{\"net\":100}", "{\"formula\":\"A1*13%\"}");
    private static readonly string OlderHash = ExcelPayslipLegacyReview.SnapshotHash("{\"net\":100}", "{\"formula\":\"A1*13%\"}");
    private static ExcelPayslipConsolidationCatalogGroup Group(int client = 11, string group = "group") =>
        new(client, group, Current, CurrentHash, new Dictionary<string,string> { [Older] = OlderHash });
    private static ExcelPayslipBatchSummary Summary(int client, string id) => new() { ClientId = client, Id = id };
    private static Dictionary<(int ClientId,string BatchId),string> Hashes(params int[] clients) =>
        clients.SelectMany(client => new[] { ((client,Current),CurrentHash), ((client,Older),OlderHash) }).ToDictionary(pair => pair.Item1,pair => pair.Item2);

    [Fact]
    public void FreshSourcesAreHiddenOnlyWithinTheirExactClientEvenWithReusedBatchIds()
    {
        var groups = new[] { Group(11), Group(54) };
        var summaries = new[] { Summary(11,Current),Summary(11,Older),Summary(54,Current),Summary(54,Older),Summary(99,Older) };
        var visible = ExcelPayslipConsolidationCatalog.Visible(summaries,groups,Hashes(11,54));
        Assert.Equal(3,visible.Count);
        Assert.Contains(visible,row=>row.ClientId==11 && row.Id==Current);
        Assert.Contains(visible,row=>row.ClientId==54 && row.Id==Current);
        Assert.Contains(visible,row=>row.ClientId==99 && row.Id==Older);
        Assert.DoesNotContain(visible,row=>row.IsConsolidatedSource);
        Assert.All(summaries.Where(row=>row.Id==Older && row.ClientId!=99),row=>Assert.Equal(Current,row.CanonicalBatchId));
    }

    [Fact]
    public void AChangedCanonicalFormulaRevealsBothVersionsInsteadOfTrustingAnOldBinding()
    {
        var hashes = Hashes(54);
        hashes[(54,Current)] = ExcelPayslipLegacyReview.SnapshotHash("{\"net\":100}","{\"formula\":\"A1*12%\"}");
        var result = ExcelPayslipConsolidationCatalog.Visible([Summary(54,Current),Summary(54,Older)],[Group(54)],hashes);
        Assert.Equal(2,result.Count); Assert.All(result,row=>Assert.True(row.ConsolidationStale));
        Assert.True(result.Single(row=>row.Id==Older).IsConsolidatedSource);
    }

    [Fact]
    public void AChangedOrMissingSourceCannotBeHiddenByAnotherClientsMatchingHash()
    {
        var hashes = Hashes(11,54); hashes.Remove((54,Older));
        var result = ExcelPayslipConsolidationCatalog.Visible([Summary(54,Current),Summary(54,Older)],[Group(54)],hashes);
        Assert.Equal(2,result.Count); Assert.All(result,row=>Assert.True(row.ConsolidationStale));
        hashes[(54,Older)] = ExcelPayslipLegacyReview.SnapshotHash("{\"net\":101}","{\"formula\":\"A1*13%\"}");
        result = ExcelPayslipConsolidationCatalog.Visible([Summary(54,Current),Summary(54,Older)],[Group(54)],hashes);
        Assert.Equal(2,result.Count); Assert.All(result,row=>Assert.True(row.ConsolidationStale));
    }

    [Fact]
    public void IncludingRetainedHistoryKeepsTheExactCanonicalAndSourceMetadata()
    {
        var result = ExcelPayslipConsolidationCatalog.Visible([Summary(54,Current),Summary(54,Older)],[Group(54)],Hashes(54),true);
        Assert.Equal(2,result.Count);
        var retained = result.Single(row=>row.Id==Older);
        Assert.True(retained.IsConsolidatedSource); Assert.False(retained.ConsolidationStale);
        Assert.Equal(Current,retained.CanonicalBatchId); Assert.Equal("group",retained.ConsolidationGroupId);
        Assert.Equal(new[] { Older },result.Single(row=>row.Id==Current).ConsolidatedSourceBatchIds);
    }

    [Fact]
    public void NoActiveGroupingNeedsNoSnapshotReadsAndLeavesBothVersionsVisible()
    {
        Assert.Empty(ExcelPayslipConsolidationCatalog.SnapshotQueries([]));
        var result = ExcelPayslipConsolidationCatalog.Visible([Summary(54,Current),Summary(54,Older)],[],new Dictionary<(int,string),string>());
        Assert.Equal(2,result.Count); Assert.All(result,row=>Assert.False(row.IsConsolidatedSource));
    }

    [Fact]
    public void ConflictingActiveMembershipDoesNotSilentlyChooseAGroup()
    {
        Assert.Throws<InvalidOperationException>(()=>ExcelPayslipConsolidationCatalog.Visible(
            [Summary(54,Older)],[Group(54,"one"),Group(54,"two")],Hashes(54)));
    }

    [Fact]
    public void SnapshotQueriesDeduplicateExactClientPairsAndStayBoundedForLargeCatalogs()
    {
        var groups = Enumerable.Range(1,200).Select(client=>Group(client)).Concat([Group(1)]).ToArray();
        var queries = ExcelPayslipConsolidationCatalog.SnapshotQueries(groups);
        Assert.Equal(4,queries.Count); Assert.All(queries,query=>Assert.InRange(query.Length,1,128));
        var pairs = queries.SelectMany(query=>query).ToArray();
        Assert.Equal(400,pairs.Length); Assert.Equal(pairs.Length,pairs.Distinct().Count());
        foreach (var client in Enumerable.Range(1,200))
        {
            Assert.Contains((client,Current),pairs); Assert.Contains((client,Older),pairs);
        }
    }

    [Fact]
    public void MySqlCompressedKnownHelloVectorDecodesWithoutChangingItsBytes()
    {
        var payload = Convert.FromHexString("05000000789CCB48CDC9C90700062C0215");
        Assert.Equal("hello",ExcelPayslipConsolidationCatalog.DecodeSnapshot(payload,false));
        Assert.Equal("",ExcelPayslipConsolidationCatalog.DecodeSnapshot([],false));
        Assert.Null(ExcelPayslipConsolidationCatalog.DecodeSnapshot(null,true));
        Assert.Throws<InvalidOperationException>(()=>ExcelPayslipConsolidationCatalog.DecodeSnapshot(null,false));
    }

    [Theory]
    [InlineData("{\"text\":\"हिंदी 😀 <tag>& \\\"quotes\\\"\"}")]
    [InlineData(" {\"escape\":\"\\u003Ctag\\u003E\",\"line\":\"\\n\"}  ")]
    public void CompressedTransportKeepsExactLegacyHashIncludingUnicodeEscapesAndWhitespace(string json)
    {
        var payload = Compressed(Encoding.UTF8.GetBytes(json),true);
        var decoded = ExcelPayslipConsolidationCatalog.DecodeSnapshot(payload,false);
        Assert.Equal(json,decoded);
        Assert.Equal(ExcelPayslipLegacyReview.SnapshotHash(json,null),ExcelPayslipLegacyReview.SnapshotHash(decoded!,null));
        Assert.Equal(ExcelPayslipLegacyReview.SnapshotHash(json,json),ExcelPayslipLegacyReview.SnapshotHash(decoded!,decoded));
    }

    [Fact]
    public void CorruptTruncatedInvalidUtf8AndOversizedPayloadsNeverBecomeTrustedSnapshots()
    {
        var valid = Compressed(Encoding.UTF8.GetBytes("{\"net\":100}"));
        var corrupt = valid.ToArray(); corrupt[^1] ^= 1;
        var wrongLength = valid.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(wrongLength,1);
        var oversized = valid.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(oversized,ExcelPayslipConsolidationCatalog.MaximumSnapshotBytes+1u);
        var invalidUtf8 = Compressed([0xc3,0x28]);
        foreach (var invalid in new[] { new byte[] {1,2,3},corrupt,valid[..^2],wrongLength,oversized,invalidUtf8 })
            Assert.Throws<InvalidOperationException>(()=>ExcelPayslipConsolidationCatalog.DecodeSnapshot(invalid,false));
    }
}
