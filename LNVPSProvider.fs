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

    static let sshKeyResourceName = "lnvps:index:SshKey"
    static let vmResourceName = "lnvps:index:VM"
    static let ipAssignmentResourceName = "lnvps:index:IpAssignment"
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

    member self.AsyncSendRequest(relativeUrl: string, method: HttpMethod, ?content: Json.JsonContent) =
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

            use message = new HttpRequestMessage(method, absoluteUrl)
            match content with
            | Some jsonContent -> message.Content <- jsonContent
            | None -> ()

            let! response = httpClient.SendAsync message |> Async.AwaitTask
            if response.IsSuccessStatusCode then
                return response
            else
                let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                let contentString =
                    match content with
                    | Some jsonContent -> $"(content = {jsonContent.Value})"
                    | None -> String.Empty
                return failwith $"""Request {method} {relativeUrl} {contentString} failed with code {response.StatusCode}.
Response: {responseBody}"""
        }
    
    override self.GetSchema (request: GetSchemaRequest, ct: CancellationToken): Task<GetSchemaResponse> =
        let sshKeyProperties = 
            """{
                                "name": {
                                    "type": "string",
                                    "description": "Name of the SSH key."
                                }
            }"""

        let sshKeyInputProperties = 
            """{
                                "name": {
                                    "type": "string",
                                    "description": "Name of the SSH key."
                                },
                                "key_data": {
                                    "type": "string",
                                    "description": "SSH key itself."
                                }
            }"""
                
        let vmInputProperties = 
            sprintf
                """{
                                "vmId": {
                                    "type": "integer",
                                    "description": "VM Id in LNVPS."
                                },
                                "ssh_key": {
                                    "type": "object",
                                    "$ref": "#/resources/%s",
                                    "description": "SSH key installed on VM."
                                }
                }"""
                sshKeyResourceName

        let vmProperties = 
            sprintf
                """{
%s,
                                "status": {
                                    "type": "string",
                                    "description": "VM status."
                                },
                                "ip_assignments": {
                                    "type": "array",
                                    "items": {
                                        "type": "object",
                                        "$ref": "#/resources/%s"
                                    },
                                    "description": "VM IP addresses."
                                }
                }"""
                (vmInputProperties.Trim('{', '}', ' '))
                ipAssignmentResourceName

        let ipAssignmentProperties =
            """{
                                "ip": {
                                    "type": "string",
                                    "description": "IP address with CIDR notation."
                                }
            }"""

        let schema =
            sprintf
                """{
                    "name": "lnvps",
                    "version": "%s",
                    "resources": {
                        "%s" : {
                            "properties": %s,
                            "inputProperties": %s
                        },
                        "%s" : {
                            "properties": %s
                        },
                        "%s" : {
                            "properties": %s,
                            "inputProperties": %s
                        }
                    },
                    "provider": {
                    }
                }"""
                LNVPSProvider.Version
                sshKeyResourceName
                sshKeyProperties
                sshKeyInputProperties
                ipAssignmentResourceName
                ipAssignmentProperties
                vmResourceName
                vmProperties
                vmInputProperties
        
        Task.FromResult <| GetSchemaResponse(Schema = schema)

    override self.CheckConfig (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        Task.FromResult <| CheckResponse(Inputs = request.NewInputs)

    override self.DiffConfig (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        Task.FromResult <| DiffResponse()

    override self.Configure (request: ConfigureRequest, ct: CancellationToken): Task<ConfigureResponse> = 
        if String.IsNullOrWhiteSpace nostrPrivateKey then
            failwith $"Environment variable {LNVPSProvider.NostrPrivateKeyEnvVarName} not provided."
        Task.FromResult <| ConfigureResponse()

    override self.Check (request: CheckRequest, ct: CancellationToken): Task<CheckResponse> = 
        if request.Type = sshKeyResourceName then
            Task.FromResult <| CheckResponse(Inputs = request.NewInputs)
        else
            failwith $"Unknown resource type '{request.Type}'"

    override self.Diff (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        if request.Type = sshKeyResourceName then
            let diff = request.NewInputs.Except request.OldInputs 
            let replaces = diff |> Seq.map (fun pair -> pair.Key) |> Seq.toArray
            Task.FromResult <| DiffResponse(Changes = (replaces.Length > 0), Replaces = replaces)
        else
            failwith $"Unknown resource type '{request.Type}'"

    member private self.AsyncCreate(request: CreateRequest): Async<CreateResponse> =
        async {
            if request.Type = sshKeyResourceName then
                let createSshKey = {| name = request.Properties.["name"]; key_data = request.Properties.["key_data"] |}
                let! response = self.AsyncSendRequest("/api/v1/ssh-key", HttpMethod.Post, Json.JsonContent.Create createSshKey)
                let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                let json = JsonDocument.Parse(responseBody).RootElement
                let id = json.GetProperty("id").GetUInt64().ToString()
                return CreateResponse(Id = id, Properties = request.Properties)
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Create (request: CreateRequest, ct: CancellationToken): Task<CreateResponse> = 
        Async.StartAsTask(self.AsyncCreate request, TaskCreationOptions.None, ct)

    member private self.AsyncUpdate(request: UpdateRequest): Async<UpdateResponse> =
        async {
            if request.Type = sshKeyResourceName then
                return failwith $"Resource {sshKeyResourceName} does not support updating."
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Update (request: UpdateRequest, ct: CancellationToken): Task<UpdateResponse> = 
        Async.StartAsTask(self.AsyncUpdate request, TaskCreationOptions.None, ct)
    
    member private self.AsyncDelete(request: DeleteRequest): Async<unit> =
        async {
            if request.Type = sshKeyResourceName then
                return failwith $"Resource {sshKeyResourceName} does not support deletion."
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Delete (request: DeleteRequest, ct: CancellationToken): Task = 
        Async.StartAsTask(self.AsyncDelete request, TaskCreationOptions.None, ct)

    member private self.AsyncRead (request: ReadRequest) : Async<ReadResponse> =
        async {
            if request.Type = sshKeyResourceName then
                let! response = self.AsyncSendRequest("/api/v1/ssh-key", HttpMethod.Get)
                let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                let json = JsonDocument.Parse(responseBody).RootElement
                let maybeUserSshKeyObject = 
                    json.EnumerateArray()
                    |> Seq.tryFind (fun object -> object.GetProperty("id").GetUInt64().ToString() = request.Id)
                match maybeUserSshKeyObject with
                | Some userSshKeyObject ->
                    let properties = dict [ "name", PropertyValue(userSshKeyObject.GetProperty("name").GetString()) ]
                    return ReadResponse(Id = request.Id, Properties = properties, Inputs = request.Inputs)
                | None ->
                    return failwith $"{sshKeyResourceName} with Id={request.Id} not found"
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Read (request: ReadRequest, ct: CancellationToken): Task<ReadResponse> = 
        Async.StartAsTask(self.AsyncRead request, TaskCreationOptions.None, ct)
