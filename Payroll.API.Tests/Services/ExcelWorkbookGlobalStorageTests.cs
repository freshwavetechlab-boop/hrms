using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelWorkbookGlobalStorageTests
{
    [Fact]
    public void WorkbookUsesExistingGlobalDefaultIncludingConfiguredApiLocalWithoutProvisioning()
    {
        var configured=new[]{new AttachmentStorageServer{Id=2,ServerCode="GOOGLE_DRIVE",Priority=50,IsActive=true,IsWriteEnabled=true},
            new AttachmentStorageServer{Id=1,ServerCode="API_LOCAL",Priority=100,IsActive=true,IsWriteEnabled=true,IsDefaultWriteServer=true}};
        var selected=AttachmentRepository.ConfiguredWriteServers(configured);
        Assert.Equal(new long[]{1,2},selected.Select(s=>s.Id));Assert.Same(configured[1],selected[0]);
        Assert.True(configured[1].IsDefaultWriteServer);Assert.Equal(2,configured.Length);
    }

    [Fact]
    public void DisabledOrAbsentLocalStorageIsNeitherSelectedNorCreated()
    {
        var configured=new[]{new AttachmentStorageServer{Id=2,ServerCode="GOOGLE_DRIVE",IsActive=true,IsWriteEnabled=true},
            new AttachmentStorageServer{Id=1,ServerCode="API_LOCAL",IsActive=false,IsWriteEnabled=true,IsDefaultWriteServer=true},
            new AttachmentStorageServer{Id=3,ServerCode="READ_ONLY",IsActive=true,IsWriteEnabled=false}};
        Assert.Equal(2,Assert.Single(AttachmentRepository.ConfiguredWriteServers(configured)).Id);
        Assert.Empty(AttachmentRepository.ConfiguredWriteServers([]));Assert.False(configured[1].IsActive);
    }
}
