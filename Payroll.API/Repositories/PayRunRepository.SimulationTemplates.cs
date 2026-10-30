using System.Data;
using System.Globalization;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;
public partial class PayRunRepository
{
    // Explicit read-only metadata projection: no employee rows, identifiers, credentials or amount overrides.
    public async Task<object> SimulationTemplatesAsync(int clientId,CancellationToken token) {
        if(clientId<=0)throw new InvalidOperationException("Select a permitted client.");
        await using var db=CreateConnection();await db.OpenAsync(token);
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,isReadOnly:true,token);
        if(await db.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE",new {ClientId=clientId},tx,cancellationToken:token)) is null)
            throw new InvalidOperationException("Select an active client.");
        var setup=ReadPayrollSetup(await PayrollDataTableStore.GetSetupJsonAsync(db,tx));
        var structures=setup.Structures.Where(s=>s.Active&&s.ClientId.Split([';',',','|']).Any(v=>v.Split(':')[0].Trim()==clientId.ToString(CultureInfo.InvariantCulture))).Take(101).ToList();
        if(structures.Count>100)throw new InvalidOperationException("This client has more than 100 active templates. Review a narrower template catalogue before simulation.");
        var result=structures.Select(s=>{
            var components=setup.Components.Where(c=>s.Lines.Any(l=>l.ComponentId==c.Id)).Select(c=>new PayrollReviewComponent(c.Id,c.Code,c.Category,c.ComponentRole,c.StatutoryType,c.CalculationType,c.Value,c.Formula,c.BaseComponent,c.ProRata,c.Taxable,c.Active,c.Priority,c.PayType)).ToList();
            var snapshot=new PayrollReviewSnapshot("Template",clientId,"",NumberFrom(s.AnnualCtc),components,s.Lines.Select(l=>new PayrollReviewLine(l.ComponentId,l.Value,l.Formula,l.CalculationType,l.BaseComponent,l.ProRataOverride)).ToList());
            return new {id=s.Id,snapshotHash=PayrollAdvisorService.Hash(snapshot),snapshot};
        }).ToList();
        await tx.CommitAsync(token);return result;
    }
}
