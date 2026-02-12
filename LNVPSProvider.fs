namespace Pulumi.LNVPS

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Linq
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Web

open Pulumi
open Pulumi.Experimental
open Pulumi.Experimental.Provider

type LNVPSProvider(nostrAuthEvent: string) =
    inherit Pulumi.Experimental.Provider.Provider()

    let base64EncodedAuthEvent = Convert.ToBase64String(Text.Encoding.UTF8.GetBytes nostrAuthEvent)
    static let apiBaseUrl = "https://api.lnvps.com"

    let httpClient = new HttpClient()

    // Provider has to advertise its version when outputting schema, e.g. for SDK generation.
    // In pulumi-lnvps, we have Pulumi generate the terraform bridge, and it automatically pulls version from the tag.
    // Use sdk/dotnet/version.txt as source of version number.
    // WARNING: that file is deleted when SDK is generated using `pulumi package gen-sdk` command; it has to be re-created.
    static member val Version = 
        let assembly = System.Reflection.Assembly.GetExecutingAssembly()
        let resourceName = 
            assembly.GetManifestResourceNames()
            |> Seq.find (fun str -> str.EndsWith "version.txt")
        use stream = assembly.GetManifestResourceStream resourceName
        use reader = new System.IO.StreamReader(stream)
        reader.ReadToEnd().Trim()

    static member val NostrEventEnvVarName = "LNVPS_NOSTR_AUTH_EVENT"

    interface IDisposable with
        override self.Dispose (): unit = 
            httpClient.Dispose()
    
    override self.GetSchema (request: GetSchemaRequest, ct: CancellationToken): Task<GetSchemaResponse> =
        raise <| NotImplementedException()

    override self.CheckConfig (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        Task.FromResult <| CheckResponse(Inputs = request.NewInputs)

    override self.DiffConfig (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        Task.FromResult <| DiffResponse()

    override self.Configure (request: ConfigureRequest, ct: CancellationToken): Task<ConfigureResponse> = 
        raise <| NotImplementedException()

    override self.Check (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        raise <| NotImplementedException()

    override self.Diff (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        raise <| NotImplementedException()

    member private self.AsyncCreate(request: CreateRequest): Async<CreateResponse> =
        async {
            return raise <| NotImplementedException()
        }

    override self.Create (request: CreateRequest, ct: CancellationToken): Task<CreateResponse> = 
        Async.StartAsTask(self.AsyncCreate request, TaskCreationOptions.None, ct)

    member private self.AsyncUpdate(request: UpdateRequest): Async<UpdateResponse> =
        async {
            return raise <| NotImplementedException()
        }

    override self.Update (request: UpdateRequest, ct: CancellationToken): Task<UpdateResponse> = 
        Async.StartAsTask(self.AsyncUpdate request, TaskCreationOptions.None, ct)
    
    member private self.AsyncDelete(request: DeleteRequest): Async<unit> =
        async {
            return raise <| NotImplementedException()
        }

    override self.Delete (request: DeleteRequest, ct: CancellationToken): Task = 
        Async.StartAsTask(self.AsyncDelete request, TaskCreationOptions.None, ct)

    member private self.AsyncRead (request: ReadRequest) : Async<ReadResponse> =
        async {
            return raise <| NotImplementedException()
        }

    override self.Read (request: ReadRequest, ct: CancellationToken): Task<ReadResponse> = 
        Async.StartAsTask(self.AsyncRead request, TaskCreationOptions.None, ct)
