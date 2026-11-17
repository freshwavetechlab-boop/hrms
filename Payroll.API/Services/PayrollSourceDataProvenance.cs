using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Payroll.API.Services;

// Raw provenance, excluding only canonical server-owned top-level profileLinks.
// Numeric and string lexemes are retained; no G15/decimal/float normalization.
internal static class PayrollSourceDataProvenance
{
    private const int MaximumBytes=64*1024*1024,MaximumNodes=2_000_000;
    internal static string BatchHash(string rawBatchJson)
    {
        using var document=Document(rawBatchJson);if(document.RootElement.ValueKind!=JsonValueKind.Object)throw Invalid();
        using var output=new MemoryStream();using(var writer=new Utf8JsonWriter(output))
        {var count=0;Write(document.RootElement,writer,true,ref count);}
        return Digest(output.ToArray());
    }
    internal static string SourceHash(string rawSourceJson)
    {
        using var document=Document(rawSourceJson);if(document.RootElement.ValueKind!=JsonValueKind.Object)throw Invalid();
        using var output=new MemoryStream();using(var writer=new Utf8JsonWriter(output))
        {var count=0;Write(document.RootElement,writer,false,ref count);}
        // The source JSON itself has no mutable metadata exception.
        return Digest(Encoding.UTF8.GetBytes(rawSourceJson));
    }
    internal static string SnapshotHash(string rawBatchJson,string rawSourceJson)
        =>Digest(Encoding.UTF8.GetBytes("PayrollSourceDataProvenance.v1:"+BatchHash(rawBatchJson)+":"+SourceHash(rawSourceJson)));
    private static JsonDocument Document(string value)
    {
        if(value is null||Encoding.UTF8.GetByteCount(value)>MaximumBytes)throw Invalid();
        try{return JsonDocument.Parse(value,new JsonDocumentOptions{MaxDepth=64});}
        catch(JsonException error){throw new InvalidOperationException("Payroll source raw provenance is malformed or exceeds its depth bound.",error);}
    }
    private static void Write(JsonElement value,Utf8JsonWriter writer,bool batchRoot,ref int count)
    {
        if(++count>MaximumNodes)throw Invalid();
        switch(value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties=value.EnumerateObject().ToArray();
                if(properties.Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=properties.Length)throw Invalid();
                writer.WriteStartObject();
                foreach(var property in properties.OrderBy(p=>p.Name,StringComparer.Ordinal))
                {
                    if(batchRoot&&property.Name=="profileLinks")
                    {ValidateProfileLinks(property.Value,ref count);continue;}
                    writer.WritePropertyName(property.Name);Write(property.Value,writer,false,ref count);
                }
                writer.WriteEndObject();break;
            case JsonValueKind.Array:
                writer.WriteStartArray();foreach(var item in value.EnumerateArray())Write(item,writer,false,ref count);writer.WriteEndArray();break;
            case JsonValueKind.String:case JsonValueKind.Number:writer.WriteRawValue(value.GetRawText());break;
            case JsonValueKind.True:writer.WriteBooleanValue(true);break;
            case JsonValueKind.False:writer.WriteBooleanValue(false);break;
            case JsonValueKind.Null:writer.WriteNullValue();break;
            default:throw Invalid();
        }
    }
    private static void ValidateProfileLinks(JsonElement value,ref int count)
    {
        if(++count>MaximumNodes)throw Invalid();if(value.ValueKind==JsonValueKind.Null)return;
        if(value.ValueKind!=JsonValueKind.Array||value.GetArrayLength()>10000)throw Invalid();
        var keys=new[]{"rowId","sourceRow","employeeId","sourceIdentityHash"};
        foreach(var link in value.EnumerateArray())
        {
            if(++count>MaximumNodes||link.ValueKind!=JsonValueKind.Object)throw Invalid();
            var fields=link.EnumerateObject().ToArray();
            if(fields.Length!=keys.Length||!fields.Select(p=>p.Name).Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)))throw Invalid();
            var identity=link.GetProperty("sourceIdentityHash").ValueKind==JsonValueKind.String?link.GetProperty("sourceIdentityHash").GetString():null;
            if(link.GetProperty("rowId").ValueKind!=JsonValueKind.String||link.GetProperty("rowId").GetString() is not{Length:>0 and <=200}
                ||!link.GetProperty("sourceRow").TryGetInt32(out var sourceRow)||sourceRow<=0
                ||!link.GetProperty("employeeId").TryGetInt32(out var employee)||employee<=0
                ||identity is not{Length:64}||!identity.All(Uri.IsHexDigit))throw Invalid();
        }
    }
    private static string Digest(byte[] value)=>Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static InvalidOperationException Invalid()=>new("Payroll raw provenance has duplicate/unsupported properties, noncanonical profile-only links or exceeds its bounded source contract.");
}
