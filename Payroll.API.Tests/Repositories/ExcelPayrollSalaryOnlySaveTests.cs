using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class ExcelPayrollSalaryOnlySaveTests
{
    private static Employee Candidate() => new()
    {
        Id=10,ClientId=11,EmployeeCode="SYN010",FirstName="Synthetic",SalaryStructureId="source-variant",AnnualCtc=120000,
        SalaryComponents=new(){{"BASIC",10000m},{"PF",1200m}},SalaryJson="{}",
        PersonalJson="{\"unchanged\":true}",PaymentJson="{\"account\":\"1.2346E+11\"}",
        PersonalDetails=new(){Mobile="synthetic-contact"},PaymentDetails=new(){BankAccountNo="1.2346E+11"}
    };

    [Fact] public async Task RealSalaryCommandsNeverTouchBankPersonalOrganizationOrTheirHistory()
    {
        using var db=new RecordingConnection();using var tx=db.BeginTransaction();var employee=Candidate();
        var personal=employee.PersonalDetails;var payment=employee.PaymentDetails;
        var personalJson=employee.PersonalJson;var paymentJson=employee.PaymentJson;
        await EmployeeRepository.WriteExcelPayrollSalaryStateAsync(db,tx,employee);
        Assert.Equal(employee.SalaryStructureId,db.Template);
        Assert.Equal(employee.AnnualCtc,db.AnnualCtc);
        Assert.Equal(new Dictionary<string,decimal>{{"BASIC",10000m},{"PF",1200m}},db.Salary);
        Assert.Equal("123460000001",db.BaseBankText);
        Assert.Equal(new[]{"prior-exact-bank","1.2346E+11"},db.BankHistory);
        Assert.Same(personal,employee.PersonalDetails);Assert.Same(payment,employee.PaymentDetails);
        Assert.Equal(personalJson,employee.PersonalJson);Assert.Equal(paymentJson,employee.PaymentJson);
        Assert.Equal("1.2346E+11",employee.PaymentDetails.BankAccountNo);
        Assert.All(db.Commands,c=>Assert.Same(tx,c.Transaction));
        Assert.All(db.Commands.Where(c=>c.IsWrite),c=>{
            Assert.DoesNotContain("payment",c.Sql,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("personal",c.Sql,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("0009",c.Sql,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payrun",c.Sql,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("attendance",c.Sql,StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(0,db.CommitCalls);Assert.Equal(0,db.RollbackCalls);
    }

    [Fact] public async Task WrongClientCannotWriteAnySalaryOrBankState()
    {
        using var db=new RecordingConnection();using var tx=db.BeginTransaction();var employee=Candidate();employee.ClientId=12;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>EmployeeRepository.WriteExcelPayrollSalaryStateAsync(db,tx,employee));
        Assert.DoesNotContain(db.Commands,c=>c.IsWrite);Assert.Equal("original",db.Template);
    }

    [Theory]
    [InlineData(0,11,"0008",false)]
    [InlineData(10,0,"0008",false)]
    [InlineData(10,11,"0009",false)]
    [InlineData(10,11,null,false)]
    [InlineData(10,11,"0008",true)]
    public void SalaryOnlyScopeCannotCreateAnEmployeeOrWriteOtherInfotypes(int id,int client,string? infotype,bool attendance)
    {
        var employee=Candidate();employee.Id=id;employee.ClientId=client;
        Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.ValidateExcelSalaryOnlyScope(employee,infotype,attendance));
    }

    [Fact] public async Task ATransactionFromAnotherConnectionIsRejectedBeforeAnyCommand()
    {
        using var db=new RecordingConnection();using var other=new RecordingConnection();using var tx=other.BeginTransaction();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>EmployeeRepository.WriteExcelPayrollSalaryStateAsync(db,tx,Candidate()));Assert.Empty(db.Commands);
    }

    [Fact] public async Task RepeatedStateWriteKeepsOneSalarySetAndProtectedBytesUnchanged()
    {
        using var db=new RecordingConnection();using var tx=db.BeginTransaction();var employee=Candidate();
        await EmployeeRepository.WriteExcelPayrollSalaryStateAsync(db,tx,employee);
        var first=db.SalaryJson;
        await EmployeeRepository.WriteExcelPayrollSalaryStateAsync(db,tx,employee);
        Assert.Equal(first,db.SalaryJson);Assert.Equal(2,db.Salary.Count);
        Assert.Equal("123460000001",db.BaseBankText);Assert.Equal(2,db.BankHistory.Length);
    }

    [Fact] public async Task ExistingSerializedSalaryInputsKeepTheirFallbackContract()
    {
        using var db=new RecordingConnection();using var tx=db.BeginTransaction();var employee=Candidate();employee.SalaryComponents.Clear();employee.SalaryJson="{\"BASIC\":0,\"PF\":0}";
        await EmployeeRepository.WriteExcelPayrollSalaryStateAsync(db,tx,employee);
        Assert.Equal(0,db.Salary["BASIC"]);Assert.Equal(0,db.Salary["PF"]);
        Assert.Equal("123460000001",db.BaseBankText);
    }

    [Fact] public async Task FailedTargetUpdateCannotReportSuccessOrCommitCallerTransaction()
    {
        using var db=new RecordingConnection(){FailUpdate=true};using var tx=db.BeginTransaction();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>EmployeeRepository.WriteExcelPayrollSalaryStateAsync(db,tx,Candidate()));
        Assert.Equal(0,db.CommitCalls);Assert.Equal(0,db.RollbackCalls);
    }

    private sealed record Seen(string Sql,bool IsWrite,DbTransaction? Transaction);
    private sealed class RecordingConnection : DbConnection
    {
        public List<Seen> Commands {get;}=[];
        public Dictionary<string,decimal> Salary {get;}=new(){{"OLD",1m}};
        public string BaseBankText {get;}="123460000001";
        public string[] BankHistory {get;}=["prior-exact-bank","1.2346E+11"];
        public string Template {get;private set;}="original";
        public decimal AnnualCtc {get;private set;}
        public string SalaryJson {get;private set;}="{}";
        public bool FailUpdate {get;set;}
        public int CommitCalls {get;set;}
        public int RollbackCalls {get;set;}
        [AllowNull] public override string ConnectionString {get;set;}="";
        public override string Database=>"synthetic";
        public override string DataSource=>"recording-only";
        public override string ServerVersion=>"synthetic";
        public override ConnectionState State=>ConnectionState.Open;
        public override void Open(){} public override void Close(){} public override void ChangeDatabase(string databaseName)=>throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)=>new RecordingTransaction(this);
        protected override DbCommand CreateDbCommand()=>new RecordingCommand(this);
        internal object? Execute(RecordingCommand command,bool scalar)
        {
            var sql=command.CommandText;Commands.Add(new(sql,!scalar,command.ActiveTransaction));
            object? Value(string name)=>command.Parameters.Cast<DbParameter>().Single(p=>p.ParameterName.TrimStart('@')==name).Value;
            if(scalar && sql.StartsWith("SELECT ClientId FROM employees ",StringComparison.Ordinal))return Convert.ToInt32(Value("Id"))==10?11:null;
            if(scalar && sql.StartsWith("SELECT Code FROM salarycomponents ",StringComparison.Ordinal))return Value("Id");
            if(!scalar && sql.StartsWith("DELETE FROM employeesalarycomponents ",StringComparison.Ordinal)){Assert.Equal(10,Convert.ToInt32(Value("employeeId")));Salary.Clear();return 1;}
            if(!scalar && sql.StartsWith("INSERT INTO employeesalarycomponents ",StringComparison.Ordinal)){Assert.Equal(10,Convert.ToInt32(Value("employeeId")));Salary[Convert.ToString(Value("ComponentId"))!]=Convert.ToDecimal(Value("Amount"));return 1;}
            if(!scalar && sql.StartsWith("UPDATE employees SET SalaryStructureId=",StringComparison.Ordinal))
            {
                var set=sql[(sql.IndexOf(" SET ",StringComparison.Ordinal)+5)..sql.IndexOf("WHERE",StringComparison.Ordinal)];
                Assert.Equal(new[]{"SalaryStructureId","AnnualCtc","SalaryJson"},set.Split(',').Select(part=>part.Split('=')[0].Trim()).ToArray());
                Assert.Contains("WHERE Id=@Id AND ClientId=@ClientId",sql);Assert.Equal(10,Convert.ToInt32(Value("Id")));Assert.Equal(11,Convert.ToInt32(Value("ClientId")));
                if(FailUpdate)return 0;Template=Convert.ToString(Value("SalaryStructureId"))!;AnnualCtc=Convert.ToDecimal(Value("AnnualCtc"));SalaryJson=Convert.ToString(Value("SalaryJson"))!;return 1;
            }
            throw new InvalidOperationException("Unexpected SQL in salary-only boundary: "+sql);
        }
    }
    private sealed class RecordingTransaction(RecordingConnection db) : DbTransaction
    {
        public override IsolationLevel IsolationLevel=>IsolationLevel.RepeatableRead;
        protected override DbConnection DbConnection=>db;
        public override void Commit()=>db.CommitCalls++;
        public override void Rollback()=>db.RollbackCalls++;
    }
    private sealed class RecordingCommand(RecordingConnection db) : DbCommand
    {
        private readonly Parameters parameters=new();
        public DbTransaction? ActiveTransaction=>DbTransaction;
        [AllowNull] public override string CommandText {get;set;}="";
        public override int CommandTimeout {get;set;}=30;
        public override CommandType CommandType {get;set;}=CommandType.Text;
        public override bool DesignTimeVisible {get;set;}
        public override UpdateRowSource UpdatedRowSource {get;set;}
        [AllowNull] protected override DbConnection DbConnection {get;set;}=db;
        protected override DbTransaction? DbTransaction {get;set;}
        protected override DbParameterCollection DbParameterCollection=>parameters;
        public override void Cancel(){} public override void Prepare(){}
        protected override DbParameter CreateDbParameter()=>new Parameter();
        public override int ExecuteNonQuery()=>Convert.ToInt32(db.Execute(this,false));
        public override object? ExecuteScalar()=>db.Execute(this,true);
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)=>throw new NotSupportedException();
    }
    private sealed class Parameter : DbParameter
    {
        public override DbType DbType {get;set;}
        public override ParameterDirection Direction {get;set;}=ParameterDirection.Input;
        public override bool IsNullable {get;set;}
        [AllowNull] public override string ParameterName {get;set;}="";
        [AllowNull] public override string SourceColumn {get;set;}="";
        public override object? Value {get;set;}
        public override bool SourceColumnNullMapping {get;set;}
        public override int Size {get;set;}
        public override void ResetDbType(){}
    }
    private sealed class Parameters : DbParameterCollection
    {
        private readonly List<DbParameter> items=[];
        public override int Count=>items.Count;public override object SyncRoot=>this;
        public override int Add(object value){items.Add((DbParameter)value);return items.Count-1;}
        public override void AddRange(Array values){foreach(var value in values)Add(value!);}
        public override void Clear()=>items.Clear();public override bool Contains(object value)=>items.Contains((DbParameter)value);
        public override bool Contains(string value)=>IndexOf(value)>=0;
        public override void CopyTo(Array array,int index)=>((ICollection)items).CopyTo(array,index);
        public override IEnumerator GetEnumerator()=>items.GetEnumerator();
        public override int IndexOf(object value)=>items.IndexOf((DbParameter)value);
        public override int IndexOf(string name)=>items.FindIndex(p=>p.ParameterName==name);
        public override void Insert(int index,object value)=>items.Insert(index,(DbParameter)value);
        public override void Remove(object value)=>items.Remove((DbParameter)value);
        public override void RemoveAt(int index)=>items.RemoveAt(index);public override void RemoveAt(string name)=>RemoveAt(IndexOf(name));
        protected override DbParameter GetParameter(int index)=>items[index];protected override DbParameter GetParameter(string name)=>items[IndexOf(name)];
        protected override void SetParameter(int index,DbParameter value)=>items[index]=value;
        protected override void SetParameter(string name,DbParameter value){var i=IndexOf(name);if(i<0)items.Add(value);else items[i]=value;}
    }
}
