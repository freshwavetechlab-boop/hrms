using System.Text.RegularExpressions; using Dapper; using MySqlConnector; using Payroll.API.Models;
namespace Payroll.API.Repositories;
public class WorkflowRepository(IConfiguration configuration)
{
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));
    public async Task InitializeAsync(){await using var db=Db();await db.OpenAsync();await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS workflowactivities (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ActivityCode VARCHAR(120) NOT NULL,
    DisplayName VARCHAR(200) NOT NULL,
    ModuleCode VARCHAR(80) NOT NULL,
    ResourceType VARCHAR(100) NOT NULL,
    Description VARCHAR(500) NOT NULL DEFAULT '',
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_WorkflowActivity_Code (ActivityCode),
    INDEX IX_WorkflowActivity_Module (ModuleCode, IsActive),
    INDEX IX_WorkflowActivity_Resource (ResourceType, IsActive)
);
CREATE TABLE IF NOT EXISTS workflow_action_rules (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ActivityCode VARCHAR(120) NOT NULL,
    HttpMethod VARCHAR(12) NOT NULL,
    PathPattern VARCHAR(300) NOT NULL,
    ResourceType VARCHAR(100) NOT NULL,
    ResourceIdSource VARCHAR(120) NOT NULL DEFAULT 'route.id',
    ResourceIdRouteKey VARCHAR(80) NOT NULL DEFAULT 'id',
    ClientIdSource VARCHAR(120) NOT NULL DEFAULT '',
    ClientIdSql VARCHAR(1000) NOT NULL DEFAULT '',
    ClientLookupTable VARCHAR(120) NOT NULL DEFAULT '',
    ClientLookupKeyColumn VARCHAR(120) NOT NULL DEFAULT '',
    ClientLookupClientColumn VARCHAR(120) NOT NULL DEFAULT '',
    WorkflowId INT NULL,
    TriggerMode VARCHAR(40) NOT NULL DEFAULT 'AfterSuccess',
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_WorkflowActionRule (HttpMethod, PathPattern, ActivityCode),
    INDEX IX_WorkflowActionRule_Activity (ActivityCode, IsActive)
);
CREATE TABLE IF NOT EXISTS workflowmasters (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NULL,
    Code VARCHAR(80) NOT NULL,
    Name VARCHAR(180) NOT NULL,
    ResourceType VARCHAR(100) NOT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_Workflow_Client_Code (ClientId,Code)
);
CREATE TABLE IF NOT EXISTS workflowstages (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    WorkflowId INT NOT NULL,
    StageOrder INT NOT NULL,
    Name VARCHAR(180) NOT NULL,
    ApproverType VARCHAR(40) NOT NULL,
    ApproverUserId INT NULL,
    UNIQUE KEY UX_WorkflowStage_Order (WorkflowId,StageOrder)
);
CREATE TABLE IF NOT EXISTS departmentheadassignments (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    Department VARCHAR(100) NOT NULL,
    UserId INT NOT NULL,
    UNIQUE KEY UX_DepartmentHeadAssignment (ClientId,Department)
);
CREATE TABLE IF NOT EXISTS workflowinstances (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    WorkflowId INT NOT NULL,
    ResourceType VARCHAR(100) NOT NULL,
    ResourceId VARCHAR(120) NOT NULL,
    RequestorUserId INT NOT NULL,
    PayloadJson JSON NOT NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Pending',
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CompletedAt DATETIME NULL,
    INDEX IX_WorkflowInstances_Resource (ResourceType,ResourceId)
);
CREATE TABLE IF NOT EXISTS workflowtasks (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    InstanceId BIGINT NOT NULL,
    StageId INT NOT NULL,
    ApproverUserId INT NOT NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Pending',
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    ActionedAt DATETIME NULL,
    Comment VARCHAR(1000),
    INDEX IX_WorkflowTasks_Approver_Status (ApproverUserId,Status)
);
CREATE TABLE IF NOT EXISTS workflowhistory (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    InstanceId BIGINT NOT NULL,
    TaskId BIGINT NULL,
    Action VARCHAR(30) NOT NULL,
    ActorUserId INT NOT NULL,
    Comment VARCHAR(1000),
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_WorkflowHistory_Instance (InstanceId,CreatedAt)
);
CREATE TABLE IF NOT EXISTS ResourceStates (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ResourceType VARCHAR(100) NOT NULL,
    ResourceId VARCHAR(120) NOT NULL,
    CurrentState VARCHAR(60) NOT NULL,
    WorkflowInstanceId BIGINT NULL,
    CreatedBy INT NOT NULL DEFAULT 0,
    CreatedOn DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    ModifiedBy INT NOT NULL DEFAULT 0,
    ModifiedOn DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_ResourceStates_Resource (ResourceType, ResourceId),
    INDEX IX_ResourceStates_State (ResourceType, CurrentState),
    INDEX IX_ResourceStates_Workflow (WorkflowInstanceId)
);");await EnsureColumnAsync(db,"workflow_action_rules","ResourceIdSource","VARCHAR(120) NOT NULL DEFAULT 'route.id' AFTER ResourceType");await EnsureColumnAsync(db,"workflow_action_rules","ClientIdSource","VARCHAR(120) NOT NULL DEFAULT '' AFTER ResourceIdRouteKey");await EnsureColumnAsync(db,"workflow_action_rules","ClientLookupTable","VARCHAR(120) NOT NULL DEFAULT '' AFTER ClientIdSql");await EnsureColumnAsync(db,"workflow_action_rules","ClientLookupKeyColumn","VARCHAR(120) NOT NULL DEFAULT '' AFTER ClientLookupTable");await EnsureColumnAsync(db,"workflow_action_rules","ClientLookupClientColumn","VARCHAR(120) NOT NULL DEFAULT '' AFTER ClientLookupKeyColumn");await db.ExecuteAsync("UPDATE workflow_action_rules SET ResourceIdSource=CONCAT('route.',ResourceIdRouteKey) WHERE (ResourceIdSource IS NULL OR ResourceIdSource='') AND ResourceIdRouteKey<>''");await EnsureDefaultActivitiesAsync(db);await EnsureDefaultActionRulesAsync(db);await EnsureForeignKeyAsync(db,"workflowstages","FK_WorkflowStages_Master","FOREIGN KEY (WorkflowId) REFERENCES workflowmasters(Id) ON DELETE CASCADE");}
    public async Task<IEnumerable<WorkflowMaster>> GetAsync(){await using var db=Db();await db.OpenAsync();var rows=(await db.QueryAsync<WorkflowMaster>("SELECT * FROM workflowmasters ORDER BY Name")).ToList();var stages=await db.QueryAsync<WorkflowStage>("SELECT * FROM workflowstages ORDER BY StageOrder");foreach(var row in rows)row.Stages=stages.Where(x=>x.WorkflowId==row.Id).ToList();return rows;}
    public async Task<IEnumerable<WorkflowActivity>> GetActivitiesAsync(){await using var db=Db();await db.OpenAsync();return await db.QueryAsync<WorkflowActivity>("SELECT * FROM workflowactivities WHERE IsActive=TRUE ORDER BY ModuleCode, DisplayName");}
    public async Task<IEnumerable<WorkflowActivity>> GetActivitiesForSetupAsync(){await using var db=Db();await db.OpenAsync();return await db.QueryAsync<WorkflowActivity>("SELECT * FROM workflowactivities ORDER BY IsActive DESC, ModuleCode, DisplayName");}
    public async Task<IEnumerable<WorkflowActionRule>> GetActionRulesAsync(){await using var db=Db();await db.OpenAsync();return await db.QueryAsync<WorkflowActionRule>("SELECT * FROM workflow_action_rules WHERE IsActive=TRUE ORDER BY HttpMethod, PathPattern, ActivityCode");}
    public async Task<IEnumerable<WorkflowActionRule>> GetActionRulesForSetupAsync(){await using var db=Db();await db.OpenAsync();return await db.QueryAsync<WorkflowActionRule>("SELECT * FROM workflow_action_rules ORDER BY IsActive DESC, ActivityCode, HttpMethod, PathPattern");}
    public async Task<IEnumerable<WorkflowApprover>> GetApproversAsync(){await using var db=Db();await db.OpenAsync();return await db.QueryAsync<WorkflowApprover>("SELECT u.Id,u.DisplayName,u.Email,u.ClientId,COALESCE(c.Name,'All clients') ClientName FROM authusers u LEFT JOIN clients c ON c.Id=u.ClientId WHERE u.IsActive=TRUE ORDER BY u.DisplayName");}
    public async Task<IEnumerable<string>> GetDepartmentsAsync(int clientId){await using var db=Db();await db.OpenAsync();return await db.QueryAsync<string>("SELECT DISTINCT Department FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND Department<>'' ORDER BY Department",new{ClientId=clientId});}
    public async Task<IEnumerable<DepartmentHeadAssignment>> GetDepartmentHeadsAsync(int clientId){await using var db=Db();await db.OpenAsync();return await db.QueryAsync<DepartmentHeadAssignment>("SELECT a.Id,a.ClientId,a.Department,a.UserId,u.DisplayName UserName FROM departmentheadassignments a JOIN authusers u ON u.Id=a.UserId WHERE a.ClientId=@ClientId ORDER BY a.Department",new{ClientId=clientId});}
    public async Task<DepartmentHeadAssignment> SaveDepartmentHeadAsync(SaveDepartmentHeadAssignmentRequest request){await using var db=Db();await db.OpenAsync();await db.ExecuteAsync("INSERT INTO departmentheadassignments (ClientId,Department,UserId) VALUES (@ClientId,@Department,@UserId) ON DUPLICATE KEY UPDATE UserId=VALUES(UserId)",request);return await db.QuerySingleAsync<DepartmentHeadAssignment>("SELECT a.Id,a.ClientId,a.Department,a.UserId,u.DisplayName UserName FROM departmentheadassignments a JOIN authusers u ON u.Id=a.UserId WHERE a.ClientId=@ClientId AND a.Department=@Department",request);}
    public async Task<int?> GetDefaultIdAsync(string resourceType,int? clientId){await using var db=Db();await db.OpenAsync();return await db.ExecuteScalarAsync<int?>("SELECT Id FROM workflowmasters WHERE ResourceType=@ResourceType AND IsActive=TRUE AND (ClientId=@ClientId OR ClientId IS NULL) ORDER BY ClientId IS NULL, Id LIMIT 1",new{ResourceType=resourceType,ClientId=clientId});}
    public async Task<int?> GetDefaultIdForActivityAsync(string activityCode,int? clientId){await using var db=Db();await db.OpenAsync();return await db.ExecuteScalarAsync<int?>("SELECT Id FROM workflowmasters WHERE Code=@ActivityCode AND IsActive=TRUE AND (ClientId=@ClientId OR ClientId IS NULL) ORDER BY ClientId IS NULL, Id LIMIT 1",new{ActivityCode=activityCode,ClientId=clientId});}
    public async Task<ResourceState?> GetResourceStateAsync(string resourceType,string resourceId){await using var db=Db();await db.OpenAsync();return await db.QueryFirstOrDefaultAsync<ResourceState>("SELECT * FROM ResourceStates WHERE ResourceType=@ResourceType AND ResourceId=@ResourceId",new{ResourceType=resourceType,ResourceId=resourceId});}
    public async Task<int?> ResolveClientIdAsync(string sql,Dictionary<string,string> routeValues){if(string.IsNullOrWhiteSpace(sql))return null;await using var db=Db();await db.OpenAsync();var parameters=new DynamicParameters();foreach(var item in routeValues)parameters.Add(item.Key,item.Value);return await db.ExecuteScalarAsync<int?>(sql,parameters);}
    public async Task<int?> ResolveClientIdFromLookupAsync(string tableName,string keyColumn,string clientColumn,string resourceId){if(string.IsNullOrWhiteSpace(tableName)||string.IsNullOrWhiteSpace(keyColumn)||string.IsNullOrWhiteSpace(clientColumn)||string.IsNullOrWhiteSpace(resourceId))return null;if(!IsSafeIdentifier(tableName)||!IsSafeIdentifier(keyColumn)||!IsSafeIdentifier(clientColumn))return null;await using var db=Db();await db.OpenAsync();return await db.ExecuteScalarAsync<int?>($"SELECT `{clientColumn}` FROM `{tableName}` WHERE `{keyColumn}`=@ResourceId LIMIT 1",new{ResourceId=resourceId});}
    public async Task<WorkflowActivity> SaveActivityAsync(SaveWorkflowActivityRequest r){await using var db=Db();await db.OpenAsync();var code=r.ActivityCode.Trim().ToUpperInvariant();var data=new{r.Id,ActivityCode=code,DisplayName=r.DisplayName.Trim(),ModuleCode=r.ModuleCode.Trim(),ResourceType=r.ResourceType.Trim(),Description=r.Description.Trim(),r.IsActive};if(r.Id==0){var id=await db.ExecuteScalarAsync<long>(@"INSERT INTO workflowactivities (ActivityCode,DisplayName,ModuleCode,ResourceType,Description,IsActive)
VALUES (@ActivityCode,@DisplayName,@ModuleCode,@ResourceType,@Description,@IsActive)
ON DUPLICATE KEY UPDATE DisplayName=VALUES(DisplayName),ModuleCode=VALUES(ModuleCode),ResourceType=VALUES(ResourceType),Description=VALUES(Description),IsActive=VALUES(IsActive);
SELECT Id FROM workflowactivities WHERE ActivityCode=@ActivityCode;",data);r.Id=id;}else await db.ExecuteAsync("UPDATE workflowactivities SET ActivityCode=@ActivityCode,DisplayName=@DisplayName,ModuleCode=@ModuleCode,ResourceType=@ResourceType,Description=@Description,IsActive=@IsActive WHERE Id=@Id",data);return await db.QuerySingleAsync<WorkflowActivity>("SELECT * FROM workflowactivities WHERE Id=@Id",new{r.Id});}
    public async Task<WorkflowActionRule> SaveActionRuleAsync(SaveWorkflowActionRuleRequest r){await using var db=Db();await db.OpenAsync();var routeKey=RouteKeyFromSource(r.ResourceIdSource);var data=new{r.Id,r.ActivityCode,HttpMethod=r.HttpMethod.ToUpperInvariant(),r.PathPattern,r.ResourceType,r.ResourceIdSource,ResourceIdRouteKey=routeKey,r.ClientIdSource,r.ClientIdSql,r.ClientLookupTable,r.ClientLookupKeyColumn,r.ClientLookupClientColumn,r.WorkflowId,r.TriggerMode,r.IsActive};if(r.Id==0){var id=await db.ExecuteScalarAsync<long>(@"INSERT INTO workflow_action_rules (ActivityCode,HttpMethod,PathPattern,ResourceType,ResourceIdSource,ResourceIdRouteKey,ClientIdSource,ClientIdSql,ClientLookupTable,ClientLookupKeyColumn,ClientLookupClientColumn,WorkflowId,TriggerMode,IsActive)
VALUES (@ActivityCode,@HttpMethod,@PathPattern,@ResourceType,@ResourceIdSource,@ResourceIdRouteKey,@ClientIdSource,@ClientIdSql,@ClientLookupTable,@ClientLookupKeyColumn,@ClientLookupClientColumn,@WorkflowId,@TriggerMode,@IsActive)
ON DUPLICATE KEY UPDATE ResourceType=VALUES(ResourceType),ResourceIdSource=VALUES(ResourceIdSource),ResourceIdRouteKey=VALUES(ResourceIdRouteKey),ClientIdSource=VALUES(ClientIdSource),ClientIdSql=VALUES(ClientIdSql),ClientLookupTable=VALUES(ClientLookupTable),ClientLookupKeyColumn=VALUES(ClientLookupKeyColumn),ClientLookupClientColumn=VALUES(ClientLookupClientColumn),WorkflowId=VALUES(WorkflowId),TriggerMode=VALUES(TriggerMode),IsActive=VALUES(IsActive);
SELECT Id FROM workflow_action_rules WHERE ActivityCode=@ActivityCode AND HttpMethod=@HttpMethod AND PathPattern=@PathPattern;",data);r.Id=id;}else await db.ExecuteAsync(@"UPDATE workflow_action_rules SET ActivityCode=@ActivityCode,HttpMethod=@HttpMethod,PathPattern=@PathPattern,ResourceType=@ResourceType,ResourceIdSource=@ResourceIdSource,ResourceIdRouteKey=@ResourceIdRouteKey,ClientIdSource=@ClientIdSource,ClientIdSql=@ClientIdSql,ClientLookupTable=@ClientLookupTable,ClientLookupKeyColumn=@ClientLookupKeyColumn,ClientLookupClientColumn=@ClientLookupClientColumn,WorkflowId=@WorkflowId,TriggerMode=@TriggerMode,IsActive=@IsActive WHERE Id=@Id",data);return await db.QuerySingleAsync<WorkflowActionRule>("SELECT * FROM workflow_action_rules WHERE Id=@Id",new{r.Id});}
    public async Task<WorkflowMaster> SaveAsync(SaveWorkflowRequest r){await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync();var id=r.Id;if(id==0)id=(int)await db.ExecuteScalarAsync<long>("INSERT INTO workflowmasters (ClientId,Code,Name,ResourceType,IsActive) VALUES (@ClientId,@Code,@Name,@ResourceType,@IsActive);SELECT LAST_INSERT_ID();",r,tx);else await db.ExecuteAsync("UPDATE workflowmasters SET ClientId=@ClientId,Code=@Code,Name=@Name,ResourceType=@ResourceType,IsActive=@IsActive WHERE Id=@Id",r,tx);await db.ExecuteAsync("DELETE FROM workflowstages WHERE WorkflowId=@Id",new{Id=id},tx);foreach(var s in r.Stages.OrderBy(x=>x.StageOrder))await db.ExecuteAsync("INSERT INTO workflowstages (WorkflowId,StageOrder,Name,ApproverType,ApproverUserId) VALUES (@WorkflowId,@StageOrder,@Name,@ApproverType,@ApproverUserId)",new{WorkflowId=id,s.StageOrder,s.Name,s.ApproverType,s.ApproverUserId},tx);await tx.CommitAsync();return (await GetAsync()).First(x=>x.Id==id);}
    public async Task<WorkflowInstance?> StartAsync(StartWorkflowRequest r,int requestor){await using var db=Db();await db.OpenAsync();var master=await db.QueryFirstOrDefaultAsync<WorkflowMaster>("SELECT * FROM workflowmasters WHERE Id=@WorkflowId AND IsActive=TRUE",r);if(master is null)return null;var stage=await db.QueryFirstOrDefaultAsync<WorkflowStage>("SELECT * FROM workflowstages WHERE WorkflowId=@WorkflowId ORDER BY StageOrder LIMIT 1",r);if(stage is null)return null;var approver=await ResolveAsync(db,stage,requestor);if(approver is null)return null;var id=await db.ExecuteScalarAsync<long>("INSERT INTO workflowinstances (WorkflowId,ResourceType,ResourceId,RequestorUserId,PayloadJson) VALUES (@WorkflowId,@ResourceType,@ResourceId,@Requestor,@PayloadJson);SELECT LAST_INSERT_ID();",new{r.WorkflowId,r.ResourceType,r.ResourceId,Requestor=requestor,r.PayloadJson});var task=await db.ExecuteScalarAsync<long>("INSERT INTO workflowtasks (InstanceId,StageId,ApproverUserId) VALUES (@Id,@StageId,@Approver);SELECT LAST_INSERT_ID();",new{Id=id,StageId=stage.Id,Approver=approver});await db.ExecuteAsync("INSERT INTO workflowhistory (InstanceId,TaskId,Action,ActorUserId,Comment) VALUES (@Id,@Task,'Started',@User,'')",new{Id=id,Task=task,User=requestor});await SetResourceStateAsync(db,r.ResourceType,r.ResourceId,"Pending",id,requestor);return await db.QueryFirstAsync<WorkflowInstance>("SELECT * FROM workflowinstances WHERE Id=@Id",new{Id=id});}
    public async Task<IEnumerable<WorkflowTask>> PendingAsync(int userId){await using var db=Db();await db.OpenAsync();return await db.QueryAsync<WorkflowTask>(@"SELECT t.*,s.Name AS StageName,i.ResourceType,i.ResourceId,i.PayloadJson FROM workflowtasks t JOIN workflowstages s ON s.Id=t.StageId JOIN workflowinstances i ON i.Id=t.InstanceId WHERE t.ApproverUserId=@UserId AND t.Status='Pending' ORDER BY t.CreatedAt",new{UserId=userId});}
    public async Task<IEnumerable<WorkflowTask>> ActionedAsync(int userId,bool all=false){await using var db=Db();await db.OpenAsync();return await db.QueryAsync<WorkflowTask>(@"SELECT t.Id,t.InstanceId,t.StageId,s.Name AS StageName,i.ResourceType,i.ResourceId,i.PayloadJson,t.ApproverUserId,COALESCE(approver.DisplayName,'') ApproverName,COALESCE(actor.DisplayName,approver.DisplayName,'') ActorName,COALESCE(NULLIF(t.Status,''),h.Action,'Actioned') Status,COALESCE(NULLIF(t.Comment,''),h.Comment,'') Comment,t.CreatedAt,COALESCE(t.ActionedAt,h.CreatedAt) ActionedAt
FROM workflowtasks t
JOIN workflowstages s ON s.Id=t.StageId
JOIN workflowinstances i ON i.Id=t.InstanceId
LEFT JOIN (
    SELECT h1.*
    FROM workflowhistory h1
    JOIN (
        SELECT TaskId,MAX(Id) Id
        FROM workflowhistory
        WHERE Action IN ('Approved','Rejected','Sent Back')
        GROUP BY TaskId
    ) latest ON latest.Id=h1.Id
) h ON h.TaskId=t.Id
LEFT JOIN authusers approver ON approver.Id=t.ApproverUserId
LEFT JOIN authusers actor ON actor.Id=h.ActorUserId
WHERE t.Status<>'Pending'
AND (@All=TRUE OR t.ApproverUserId=@UserId OR h.ActorUserId=@UserId)
ORDER BY COALESCE(t.ActionedAt,h.CreatedAt,t.CreatedAt) DESC
LIMIT 250",new{UserId=userId,All=all});}
    public async Task<IEnumerable<WorkflowHistoryItem>> GetInstancesAsync(){await using var db=Db();await db.OpenAsync();return await db.QueryAsync<WorkflowHistoryItem>(@"SELECT i.Id,m.Name WorkflowName,i.ResourceType,i.ResourceId,i.PayloadJson,COALESCE(u.DisplayName,'Unknown') RequestorName,i.Status,i.CreatedAt,i.CompletedAt FROM workflowinstances i JOIN workflowmasters m ON m.Id=i.WorkflowId LEFT JOIN authusers u ON u.Id=i.RequestorUserId ORDER BY i.CreatedAt DESC LIMIT 250");}
    public async Task<IEnumerable<dynamic>> HistoryAsync(long instanceId){await using var db=Db();await db.OpenAsync();return await db.QueryAsync(@"SELECT h.Id,h.Action,h.Comment,h.CreatedAt,u.DisplayName AS Actor FROM workflowhistory h LEFT JOIN authusers u ON u.Id=h.ActorUserId WHERE h.InstanceId=@InstanceId ORDER BY h.CreatedAt",new{InstanceId=instanceId});}
    public async Task<WorkflowInstance?> GetInstanceAsync(long instanceId){await using var db=Db();await db.OpenAsync();return await db.QueryFirstOrDefaultAsync<WorkflowInstance>("SELECT * FROM workflowinstances WHERE Id=@Id",new{Id=instanceId});}
    public async Task<WorkflowInstance?> GetInstanceForTaskAsync(long taskId){await using var db=Db();await db.OpenAsync();return await db.QueryFirstOrDefaultAsync<WorkflowInstance>("SELECT i.* FROM workflowinstances i JOIN workflowtasks t ON t.InstanceId=i.Id WHERE t.Id=@TaskId",new{TaskId=taskId});}
    public async Task<bool> ActionAsync(long taskId,int actor,string action,string comment){await using var db=Db();await db.OpenAsync();var task=await db.QueryFirstOrDefaultAsync<WorkflowTask>("SELECT * FROM workflowtasks WHERE Id=@Id AND ApproverUserId=@Actor AND Status='Pending'",new{Id=taskId,Actor=actor});if(task is null)return false;await db.ExecuteAsync("UPDATE workflowtasks SET Status=@Action,ActionedAt=UTC_TIMESTAMP(),Comment=@Comment WHERE Id=@Id",new{Action=action,Comment=comment,Id=taskId});await db.ExecuteAsync("INSERT INTO workflowhistory (InstanceId,TaskId,Action,ActorUserId,Comment) VALUES (@InstanceId,@TaskId,@Action,@Actor,@Comment)",new{task.InstanceId,TaskId=taskId,Action=action,Actor=actor,Comment=comment});if(action=="Approved")await AdvanceAsync(db,task.InstanceId,task.StageId,actor);else{await db.ExecuteAsync("UPDATE workflowinstances SET Status=@Status,CompletedAt=UTC_TIMESTAMP() WHERE Id=@Id",new{Status=action,Id=task.InstanceId});var instance=await db.QueryFirstOrDefaultAsync<WorkflowInstance>("SELECT * FROM workflowinstances WHERE Id=@Id",new{Id=task.InstanceId});if(instance is not null)await SetResourceStateAsync(db,instance.ResourceType,instance.ResourceId,action,task.InstanceId,actor);}return true;}
    private static async Task<int?> ResolveAsync(MySqlConnection db,WorkflowStage s,int requestor){if(s.ApproverType=="Specific User")return await db.ExecuteScalarAsync<int?>("SELECT Id FROM authusers WHERE Id=@Id AND IsActive=TRUE",new{Id=s.ApproverUserId});if(s.ApproverType=="HR Manager")return await db.ExecuteScalarAsync<int?>("SELECT u.Id FROM authusers u JOIN authuserroles ur ON ur.UserId=u.Id JOIN authroles r ON r.Id=ur.RoleId JOIN authusers requester ON requester.Id=@Requestor LEFT JOIN employees e ON e.Id=requester.EmployeeId WHERE r.Code='hr_manager' AND u.IsActive=TRUE AND (u.ClientId=e.ClientId OR u.ClientId IS NULL) ORDER BY u.ClientId IS NULL LIMIT 1",new{Requestor=requestor});if(s.ApproverType=="Reporting Manager")return await db.ExecuteScalarAsync<int?>(@"SELECT u.Id
FROM authusers requester
JOIN employees employee ON employee.Id=requester.EmployeeId
JOIN authusers u ON u.IsActive=TRUE
LEFT JOIN employees manager ON manager.Id=employee.ReportingManagerId
WHERE requester.Id=@Requestor
  AND (u.Id=employee.ReportingManagerUserId OR (COALESCE(employee.ReportingManagerUserId,0)=0 AND u.EmployeeId=manager.Id))
ORDER BY u.Id
LIMIT 1",new{Requestor=requestor});if(s.ApproverType=="Department Head")return await db.ExecuteScalarAsync<int?>("SELECT u.Id FROM departmentheadassignments a JOIN authusers u ON u.Id=a.UserId AND u.IsActive=TRUE JOIN authusers requester ON requester.Id=@Requestor JOIN employees employee ON employee.Id=requester.EmployeeId WHERE a.ClientId=employee.ClientId AND a.Department=employee.Department",new{Requestor=requestor});return null;}
    private static async Task AdvanceAsync(MySqlConnection db,long instanceId,int stageId,int actor){var next=await db.QueryFirstOrDefaultAsync<WorkflowStage>("SELECT s.* FROM workflowstages s JOIN workflowtasks t ON t.StageId=s.Id WHERE t.InstanceId=@InstanceId AND s.StageOrder>(SELECT StageOrder FROM workflowstages WHERE Id=@StageId) ORDER BY s.StageOrder LIMIT 1",new{InstanceId=instanceId,StageId=stageId});var instance=await db.QueryFirstOrDefaultAsync<WorkflowInstance>("SELECT * FROM workflowinstances WHERE Id=@Id",new{Id=instanceId});if(next is null){await db.ExecuteAsync("UPDATE workflowinstances SET Status='Approved',CompletedAt=UTC_TIMESTAMP() WHERE Id=@Id",new{Id=instanceId});if(instance is not null)await SetResourceStateAsync(db,instance.ResourceType,instance.ResourceId,"Approved",instanceId,actor);return;}var approver=await ResolveAsync(db,next,actor);if(approver is null){await db.ExecuteAsync("UPDATE workflowinstances SET Status='Failed',CompletedAt=UTC_TIMESTAMP() WHERE Id=@Id",new{Id=instanceId});if(instance is not null)await SetResourceStateAsync(db,instance.ResourceType,instance.ResourceId,"Failed",instanceId,actor);return;}await db.ExecuteAsync("INSERT INTO workflowtasks (InstanceId,StageId,ApproverUserId) VALUES (@InstanceId,@StageId,@Approver)",new{InstanceId=instanceId,StageId=next.Id,Approver=approver});if(instance is not null)await SetResourceStateAsync(db,instance.ResourceType,instance.ResourceId,"Pending",instanceId,actor);}
    private static string RouteKeyFromSource(string source){const string prefix="route.";return !string.IsNullOrWhiteSpace(source)&&source.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)?source[prefix.Length..]:"id";}
    private static bool IsSafeIdentifier(string value)=>Regex.IsMatch(value,@"^[A-Za-z_][A-Za-z0-9_]*$");
    private static Task SetResourceStateAsync(MySqlConnection db,string resourceType,string resourceId,string state,long? workflowInstanceId,int actor)=>db.ExecuteAsync(@"INSERT INTO ResourceStates (ResourceType,ResourceId,CurrentState,WorkflowInstanceId,CreatedBy,ModifiedBy)
VALUES (@ResourceType,@ResourceId,@State,@WorkflowInstanceId,@Actor,@Actor)
ON DUPLICATE KEY UPDATE CurrentState=@State, WorkflowInstanceId=@WorkflowInstanceId, ModifiedBy=@Actor, ModifiedOn=CURRENT_TIMESTAMP",new{ResourceType=resourceType,ResourceId=resourceId,State=state,WorkflowInstanceId=workflowInstanceId,Actor=actor});
    private static Task EnsureDefaultActivitiesAsync(MySqlConnection db)=>db.ExecuteAsync(@"INSERT INTO workflowactivities (ActivityCode,DisplayName,ModuleCode,ResourceType,Description,IsActive) VALUES
('PAYRUN.SUBMIT','Submit payroll for approval','Payroll','PayRun','Lock a draft payroll run and route it for approval.',TRUE),
('PAYRUN.RECALL','Recall payroll','Payroll','PayRun','Recall a pending or approved unpaid payroll run.',TRUE),
('LEAVE_REQUEST.SUBMIT','Submit leave request','Leave & Attendance','LeaveRequest','Employee leave request approval.',TRUE),
('TRAVEL_REQUEST.SUBMIT','Submit travel request','Travel & Expense','TravelRequest','Employee travel request approval.',TRUE),
('EXPENSE_CLAIM.SUBMIT','Submit expense claim','Travel & Expense','ExpenseClaim','Employee expense claim reimbursement approval.',TRUE),
('ATTENDANCE_REGULARIZATION.SUBMIT','Submit attendance regularization','Leave & Attendance','AttendanceRegularization','Employee attendance correction approval.',TRUE),
('EMPLOYEE_ACTION.SUBMIT','Submit employee action','Employees','EmployeeAction','Hire, promotion, transfer, demotion, retirement or separation action approval.',TRUE),
('SALARY_REVISION.SUBMIT','Submit salary revision','Employees','SalaryRevision','Employee compensation change approval.',TRUE),
('TAX_PROOF.SUBMIT','Submit tax proof','Tax','TaxProof','Employee proof of investment approval.',TRUE),
('PAYROLL_ADJUSTMENT.SUBMIT','Submit payroll adjustment','Payroll','PayrollAdjustment','Variable earning, deduction, recovery or reimbursement adjustment approval.',TRUE)
ON DUPLICATE KEY UPDATE DisplayName=VALUES(DisplayName),ModuleCode=VALUES(ModuleCode),ResourceType=VALUES(ResourceType),Description=VALUES(Description),IsActive=VALUES(IsActive);");
    private static Task EnsureDefaultActionRulesAsync(MySqlConnection db)=>db.ExecuteAsync(@"INSERT INTO workflow_action_rules (ActivityCode,HttpMethod,PathPattern,ResourceType,ResourceIdSource,ResourceIdRouteKey,ClientIdSource,ClientIdSql,ClientLookupTable,ClientLookupKeyColumn,ClientLookupClientColumn,TriggerMode,IsActive) VALUES
('PAYRUN.SUBMIT','POST','/api/pay-runs/{id}/submit','PayRun','route.id','id','','','payruns','Id','ClientId','AfterSuccess',TRUE),
('LEAVE_REQUEST.SUBMIT','POST','/api/ess/leave/requests','LeaveRequest','response.id','id','','','essleaverequests','Id','ClientId','AfterSuccess',TRUE),
('TRAVEL_REQUEST.SUBMIT','POST','/api/ess/travel/requests/{id}/submit','TravelRequest','route.id','id','','','ess_travel_requests','Id','ClientId','AfterSuccess',TRUE),
('EXPENSE_CLAIM.SUBMIT','POST','/api/ess/expenses/claims/{id}/submit','ExpenseClaim','route.id','id','','','ess_expense_claims','Id','ClientId','AfterSuccess',TRUE)
ON DUPLICATE KEY UPDATE ResourceType=VALUES(ResourceType),ResourceIdSource=VALUES(ResourceIdSource),ResourceIdRouteKey=VALUES(ResourceIdRouteKey),ClientIdSource=VALUES(ClientIdSource),ClientIdSql=VALUES(ClientIdSql),ClientLookupTable=VALUES(ClientLookupTable),ClientLookupKeyColumn=VALUES(ClientLookupKeyColumn),ClientLookupClientColumn=VALUES(ClientLookupClientColumn),TriggerMode=VALUES(TriggerMode),IsActive=VALUES(IsActive);");
    private static async Task EnsureColumnAsync(MySqlConnection db,string tableName,string columnName,string definition){var exists=await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@TableName AND COLUMN_NAME=@ColumnName",new{TableName=tableName,ColumnName=columnName});if(exists==0)await db.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition}");}
    private static async Task EnsureForeignKeyAsync(MySqlConnection db,string tableName,string constraintName,string definition){var exists=await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() AND CONSTRAINT_NAME=@ConstraintName",new{ConstraintName=constraintName});if(exists==0)await db.ExecuteAsync($"ALTER TABLE `{tableName}` ADD CONSTRAINT `{constraintName}` {definition}");}
}
