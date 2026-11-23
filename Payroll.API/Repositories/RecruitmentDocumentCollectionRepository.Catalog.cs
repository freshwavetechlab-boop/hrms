using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentCollectionRepository
{
    public async Task InitializeAsync()
    {
        await using var db = Db(); await db.OpenAsync();
        await EnsureSchemaAsync(db);
        await EnsureCatalogAsync(db);
    }

    public async Task ValidateSelectionsAsync(int clientId, IReadOnlyList<RecruitmentRequestedDocument> documentRequests, AuthUser user)
    {
        var catalog = await GetCatalogAsync(clientId, user);
        ValidateSelections(documentRequests, catalog.Select(field => field.FieldConfigurationId));
    }

    public async Task<IReadOnlyList<RecruitmentDocumentRequestField>> GetCatalogAsync(int clientId, AuthUser user)
    {
        if (clientId <= 0 || user.Id <= 0 || !user.IsActive || user.MustChangePassword
            || !SelectedClientAccess.Allows(user, clientId) || user.ClientId.HasValue && user.ClientId != clientId
            || !RecruitmentPermissions.Has(user, "recruitment.candidate.manage", "recruitment.offer.manage", "recruitment.offer.issue"))
            throw new UnauthorizedAccessException("The document list is outside your permitted client scope.");
        await using var db = Db(); await db.OpenAsync();
        await EnsureCatalogAsync(db);
        var fields = await LoadConfigurationsAsync(db, null, clientId);
        return fields.Select(field => new RecruitmentDocumentRequestField
        {
            FieldConfigurationId = field.Id, Label = AttachmentRepository.JoiningDocumentDisplayLabel(field), IsRequired = field.IsRequired
        }).ToArray();
    }

    // Request delivery is a consumer of the attachment core, not another catalog.
    public static Task EnsureCatalogAsync(MySqlConnection db) => AttachmentRepository.EnsureJoiningDocumentCatalogAsync(db);

    private static Task<IReadOnlyList<AttachmentFieldConfiguration>> LoadConfigurationsAsync(MySqlConnection db, MySqlTransaction? tx, int clientId)
        => AttachmentRepository.LoadJoiningDocumentConfigurationsAsync(db, tx, clientId);
}
