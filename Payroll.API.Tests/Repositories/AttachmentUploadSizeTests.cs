using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class AttachmentUploadSizeTests
{
    private static AttachmentRepository Repository(long globalLimit = 25 * 1024 * 1024) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AttachmentStorage:GlobalMaximumFileSizeBytes"] = globalLimit.ToString() }).Build(),
        null!, null!, null!, null!, NullLogger<AttachmentRepository>.Instance);

    [Theory]
    [InlineData(1048576)]
    [InlineData(10485760)]
    public void Shared_server_validation_allows_exact_field_limit_and_rejects_one_extra_byte_before_reading_file(long limit)
    {
        var repository = Repository();
        var field = new AttachmentFieldConfiguration { Id = 89, AttachmentAttributeId = 1, MaximumFileSizeBytes = limit };
        var metadata = new AttachmentUploadMetadata { FieldConfigurationId = 89 };
        Assert.Null(repository.ValidateMetadata(field, metadata, new FormFile(Stream.Null, 0, limit, "file", "proof.pdf")));
        var error = repository.ValidateMetadata(field, metadata, new FormFile(Stream.Null, 0, limit + 1, "file", "proof.pdf"));
        Assert.NotNull(error);
        Assert.Contains($"{limit / (1024 * 1024)} MB", error);
    }

    [Fact]
    public void Shared_server_keeps_global_cap_when_a_legacy_field_has_a_larger_limit()
    {
        var field = new AttachmentFieldConfiguration { AttachmentAttributeId = 1, MaximumFileSizeBytes = 10 * 1024 * 1024 };
        var error = Repository(2 * 1024 * 1024).ValidateMetadata(field, new(), new FormFile(Stream.Null, 0, 3 * 1024 * 1024, "file", "proof.pdf"));
        Assert.NotNull(error);
        Assert.Contains("2 MB", error);
    }
}
