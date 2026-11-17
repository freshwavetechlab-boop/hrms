using System.Security.Cryptography;using System.Text;using System.Text.RegularExpressions;using Payroll.API.Models;
namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    internal static string? ValidateExpectedSaveSnapshot(ExcelPayslipBatch request)
    {
        if(request.ExpectedSaveSnapshotHash is null)return null;
        return !Guid.TryParseExact(request.Id,"N",out _)||request.CreateLocationBatch
            ||!Regex.IsMatch(request.ExpectedSaveSnapshotHash,@"\A[a-fA-F0-9]{64}\z")
            ?"A conditional save requires an existing same-ID batch and its exact reviewed snapshot hash.":null;
    }
    internal static string SaveSnapshotHash(string batchHash,string sourceHash,string mailHash,string mappingHash="")
    {
        static bool Valid(string s)=>Regex.IsMatch(s??"",@"\A[a-fA-F0-9]{64}\z");
        if(!Valid(batchHash)||sourceHash!=""&&!Valid(sourceHash)||!Valid(mailHash)||mappingHash!=""&&!Valid(mappingHash))
            throw new InvalidOperationException("A saved batch/source/mail fingerprint is invalid.");
        var value=batchHash.ToLowerInvariant()+":"+sourceHash.ToLowerInvariant()+":"+mailHash.ToLowerInvariant();
        if(mappingHash!="")value+=":"+mappingHash.ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value))).ToLowerInvariant();
    }
    internal static bool ExpectedSaveSnapshotMatches(string expected,string batchHash,string sourceHash,string mailHash,string mappingHash="")
        =>Regex.IsMatch(expected??"",@"\A[a-fA-F0-9]{64}\z")&&CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected!.ToLowerInvariant()),Encoding.ASCII.GetBytes(SaveSnapshotHash(batchHash,sourceHash,mailHash,mappingHash)));
    private sealed class SaveSnapshotHashes
    {
        public string BatchHash{get;set;}="";public string SourceHash{get;set;}="";public string MailHash{get;set;}="";public string MappingHash{get;set;}="";
    }
}
