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
open Nostr.Client
open Nostr.Client.Messages
open Nostr.Client.Keys

type LNVPSProvider(nostrPrivateKey: string) =
    inherit Pulumi.Experimental.Provider.Provider()

    static let apiBaseUrl = "https://api.lnvps.net"

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

    static member val NostrPrivateKeyEnvVarName = "LNVPS_NOSTR_PRIV_KEY"

    interface IDisposable with
        override self.Dispose (): unit = 
            httpClient.Dispose()

    member self.AsyncSendRequest(relativeUrl: string, method: HttpMethod, content: Json.JsonContent) =
        async {
            let absoluteUrl = apiBaseUrl + relativeUrl
            let event = NostrEvent(
                Kind = NostrKind.HttpAuth,
                CreatedAt = DateTime.UtcNow,
                Content = String.Empty,
                Tags = NostrEventTags(NostrEventTag("u", absoluteUrl), NostrEventTag("method", method.Method))
            )

            let key = NostrPrivateKey.FromHex nostrPrivateKey
            let signedEvent = event.Sign key
            let serializedEvent = Nostr.Client.Json.NostrJson.Serialize signedEvent 
            let base64EncodedEvent = Convert.ToBase64String(Text.Encoding.UTF8.GetBytes serializedEvent)
            httpClient.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Nostr", base64EncodedEvent)

            use message = new HttpRequestMessage(method, absoluteUrl, Content=content)
            return! httpClient.SendAsync message |> Async.AwaitTask
        }
    
    override self.GetSchema (request: GetSchemaRequest, ct: CancellationToken): Task<GetSchemaResponse> =
        raise <| NotImplementedException()

    override self.CheckConfig (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        Task.FromResult <| CheckResponse(Inputs = request.NewInputs)

    override self.DiffConfig (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        Task.FromResult <| DiffResponse()

    override self.Configure (request: ConfigureRequest, ct: CancellationToken): Task<ConfigureResponse> = 
        if String.IsNullOrWhiteSpace nostrPrivateKey then
            failwith $"Environment variable {LNVPSProvider.NostrPrivateKeyEnvVarName} not provided."
        Task.FromResult <| ConfigureResponse()

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
