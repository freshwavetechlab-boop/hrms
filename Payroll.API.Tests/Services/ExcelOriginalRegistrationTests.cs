using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelOriginalRegistrationTests
{
    [Fact]
    public async Task MissingFieldIsResolvedOnUploadWithoutRepeatedOrExistingConfigurationChanges()
    {
        AttachmentFieldConfiguration? configured=null;var seeds=0;
        Task<AttachmentFieldConfiguration?> Read()=>Task.FromResult(configured);
        Task Seed(){seeds++;configured=new(){Id=7,AttachmentAttributeId=8,IsActive=true};return Task.CompletedTask;}
        var first=await AttachmentRepository.ResolveOriginalWorkbookFieldAsync(Read,Seed);
        var second=await AttachmentRepository.ResolveOriginalWorkbookFieldAsync(Read,Seed);
        Assert.Same(first,second);Assert.Equal(1,seeds);
        configured=new(){Id=9,IsActive=false};
        Assert.Same(configured,await AttachmentRepository.ResolveOriginalWorkbookFieldAsync(Read,Seed));
        Assert.Equal(1,seeds);Assert.False(configured.IsActive);
    }

    [Fact]
    public async Task DisabledOrUnavailableFieldDoesNotCauseRepeatedSeedsOrAStorageFallback()
    {
        var seeds=0;
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>AttachmentRepository.ResolveOriginalWorkbookFieldAsync(
            ()=>Task.FromResult<AttachmentFieldConfiguration?>(null),()=>{seeds++;return Task.CompletedTask;}));
        Assert.Equal(1,seeds);Assert.Contains("no batch was saved",error.Message);
    }
}
