open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Threading.Tasks

let appRoot = Path.GetFullPath(@"D:\NewHrms\hrms\Payroll.API")
let outputRoot = Path.Combine(appRoot, ".codex-build")

AssemblyLoadContext.Default.add_Resolving(fun context name ->
    let dependencyPath = Path.Combine(outputRoot, name.Name + ".dll")
    if File.Exists(dependencyPath) then
        context.LoadFromAssemblyPath(dependencyPath)
    else
        null)

Directory.SetCurrentDirectory(appRoot)
AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", outputRoot + string Path.DirectorySeparatorChar)
Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://localhost:5062")
Environment.SetEnvironmentVariable("OutboundDelivery__Suppressed", "true")

let application = Assembly.Load(File.ReadAllBytes(Path.Combine(outputRoot, "Payroll.API.dll")))
let result = application.EntryPoint.Invoke(null, [| box [| "prod"; "--urls"; "http://localhost:5062" |] |])

match result with
| :? Task as task -> task.GetAwaiter().GetResult()
| _ -> ()
