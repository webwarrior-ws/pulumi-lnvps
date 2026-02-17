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
    
    member private self.AsyncGetVMStatus(vmId: uint64) =
        async {
            let! response = self.AsyncSendRequest($"/api/v1/vm/{vmId}", HttpMethod.Get)
            let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            let vmStatusJson = JsonDocument.Parse(responseBody).RootElement

            let status = PropertyValue(vmStatusJson.GetProperty("status").GetString())
            let ipAssignments =
                vmStatusJson.GetProperty("ip_assignments").EnumerateArray()
                |> Seq.map (fun jsonObject -> 
                    let ip = jsonObject.GetProperty("ip").GetString()
                    ImmutableDictionary.CreateRange(Seq.singleton <| KeyValuePair("ip", PropertyValue ip))
                    |> PropertyValue)
                |> ImmutableArray.CreateRange
                |> PropertyValue
            let sshKey =
                ResourceReference(
                    Urn sshKeyResourceName,
                    PropertyValue(vmStatusJson.GetProperty("ssh_key").GetProperty("id").GetUInt64().ToString()),
                    LNVPSProvider.Version
                )
                |> PropertyValue
                    
            return dict [ "status", status; "ip_assignments", ipAssignments; "ssh_key", sshKey ]
        }

    member private self.AsyncUpdateVM (vmId: uint64) (requestProperties: ImmutableDictionary<string, PropertyValue>) =
        async {
            let! vmStatus = self.AsyncGetVMStatus (uint64 vmId)

            match vmStatus.["ssh_key"].TryGetResource(), requestProperties.["ssh_key"].TryGetResource() with
            | (true, vmSshKeyResource), (true, requestSshKeyResource) when vmSshKeyResource.Id <> requestSshKeyResource.Id ->
                let vmPatchRequestBody = 
                    match requestSshKeyResource.Id.TryGetString() with
                    | true, id -> {| ssh_key_id = uint64 id |}
                    | _ -> failwith "Could not get SSH key Id from request."
                do! 
                    self.AsyncSendRequest($"/api/v1/vm/{vmId}", HttpMethod.Patch, Json.JsonContent.Create vmPatchRequestBody)
                    |> Async.Ignore
            | _ -> ()

            do! self.AsyncSendRequest($"/api/v1/vm/{vmId}/re-install", HttpMethod.Patch) |> Async.Ignore
            printfn $"Re-installing VM with Id={vmId}..."
            // wait for operation to complete
            let maxWaitTime = TimeSpan.FromMinutes 5.0
            let stopWatch = Diagnostics.Stopwatch()
            stopWatch.Start()
            let mutable repeat = true
            while repeat && stopWatch.Elapsed < maxWaitTime do
                printfn "Waiting for re-install to finish..."
                do! Async.Sleep(TimeSpan.FromSeconds 5.0)
                let! currentVmStatus = self.AsyncGetVMStatus (uint64 vmId)
                match currentVmStatus.["status"].TryGetString() with
                | false, _ -> failwith "Could not get 'status' field from VM status object"
                | true, "error" -> failwith "VM status is 'error' after re-install"
                | true, "unknown" -> failwith "VM status is 'unknown' after re-install"
                | true, "stopped" -> 
                    do! self.AsyncSendRequest($"/api/v1/vm/{vmId}/start", HttpMethod.Patch) |> Async.Ignore
                | true, "pending" -> ()
                | true, "running" ->
                    repeat <- false
                    printfn "VM is ready"
                | true, unknownStatus -> failwith $"Unknown VM status: '{unknownStatus}' after re-install"

            let! currentVmStatus = self.AsyncGetVMStatus (uint64 vmId)
            return requestProperties.SetItems currentVmStatus
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
        if request.Type = sshKeyResourceName || request.Type = vmResourceName then
            Task.FromResult <| CheckResponse(Inputs = request.NewInputs)
        else
            failwith $"Unknown resource type '{request.Type}'"

    override self.Diff (request: DiffRequest, ct: CancellationToken): Task<DiffResponse> = 
        if request.Type = sshKeyResourceName || request.Type = vmResourceName then
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
            elif request.Type = vmResourceName then
                match request.Properties.["vmId"].TryGetNumber() with
                | true, vmId ->
                    let! updatedProperties = self.AsyncUpdateVM (uint64 vmId) request.Properties
                    return CreateResponse(Properties = updatedProperties)
                | false, _ ->
                    return failwith $"Property vmId not present in '{request.Type}'"
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Create (request: CreateRequest, ct: CancellationToken): Task<CreateResponse> = 
        Async.StartAsTask(self.AsyncCreate request, TaskCreationOptions.None, ct)

    member private self.AsyncUpdate(request: UpdateRequest): Async<UpdateResponse> =
        async {
            if request.Type = sshKeyResourceName then
                return failwith $"Resource {sshKeyResourceName} does not support updating."
            elif request.Type = vmResourceName then
                let properties = request.Olds.AddRange request.News
                match properties.["vmId"].TryGetNumber() with
                | true, vmId ->
                    let! updatedProperties = self.AsyncUpdateVM (uint64 vmId) properties
                    return UpdateResponse(Properties = updatedProperties)
                | false, _ ->
                    return failwith $"Property vmId not present in '{request.Type}'"
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Update (request: UpdateRequest, ct: CancellationToken): Task<UpdateResponse> = 
        Async.StartAsTask(self.AsyncUpdate request, TaskCreationOptions.None, ct)
    
    member private self.AsyncDelete(request: DeleteRequest): Async<unit> =
        async {
            if request.Type = sshKeyResourceName then
                return failwith $"Resource {sshKeyResourceName} does not support deletion."
            elif request.Type = vmResourceName then
                ()
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
            elif request.Type = vmResourceName then
                match request.Inputs.["vmId"].TryGetNumber() with
                | true, vmId ->
                    let! vmProperties = self.AsyncGetVMStatus (uint64 vmId)
                    return ReadResponse(Id = request.Id, Properties = vmProperties, Inputs = request.Inputs)
                | false, _ ->
                    return failwith $"Property vmId not present in '{request.Type}'"
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Read (request: ReadRequest, ct: CancellationToken): Task<ReadResponse> = 
        Async.StartAsTask(self.AsyncRead request, TaskCreationOptions.None, ct)
