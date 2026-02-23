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
open Fsdk.Process

type LNVPSProvider(nostrPrivateKey: string) =
    inherit Pulumi.Experimental.Provider.Provider()

    static let sshKeyResourceName = "lnvps:index:SshKey"
    static let vmResourceName = "lnvps:index:VM"
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

    member private self.GetPropertyString(dict: ImmutableDictionary<string, PropertyValue>, name: string, ?lineNumber: string) =
        let lineNumberString =
            match lineNumber with
            | Some line -> $" at line {line}"
            | None -> ""
        match dict.TryGetValue name with
        | true, propertyValue ->
            match propertyValue.TryGetString() with
            | true, value -> value
            | false, _ -> failwith $"Value of property {name} ({propertyValue}) is not a string{lineNumberString}"
        | false, _ -> failwith $"No {name} property in dictionary{lineNumberString}"

    member private self.GetPropertyNumber(dict: ImmutableDictionary<string, PropertyValue>, name: string, ?lineNumber: string) =
        let lineNumberString =
            match lineNumber with
            | Some line -> $" at line {line}"
            | None -> ""
        match dict.TryGetValue name with
        | true, propertyValue ->
            match propertyValue.TryGetNumber() with
            | true, value -> value
            | false, _ -> failwith $"Value of property {name} ({propertyValue}) is not a number{lineNumberString}"
        | false, _ -> failwith $"No {name} property in dictionary{lineNumberString}"

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
            let vmStatusJson = JsonDocument.Parse(responseBody).RootElement.GetProperty "data"

            let status = PropertyValue(vmStatusJson.GetProperty("status").GetProperty("state").GetString())
            let ipAssignments =
                vmStatusJson.GetProperty("ip_assignments").EnumerateArray()
                |> Seq.map 
                    (fun jsonObject -> 
                        let ipWithSubnetMask = jsonObject.GetProperty("ip").GetString()
                        let ip = ipWithSubnetMask.Split('/').[0]
                        PropertyValue ip)
                |> ImmutableArray.CreateRange
                |> PropertyValue
            let sshKeyId =
                vmStatusJson.GetProperty("ssh_key").GetProperty("id").GetUInt64().ToString()
                |> PropertyValue
                    
            return dict [ "status", status; "ip_assignments", ipAssignments; "ssh_key_id", sshKeyId ]
        }

    member private self.AsyncWaitForVMToBeRunning (maxWaitTime: TimeSpan) (timeBetweenRetires: TimeSpan) (vmId: uint64) =
        async {
            let stopWatch = Diagnostics.Stopwatch()
            stopWatch.Start()

            let rec loop () =
                async {
                    if stopWatch.Elapsed >= maxWaitTime then
                        return failwith $"VM is still not runninng after {maxWaitTime}."
                    else
                        do! Async.Sleep timeBetweenRetires
                        let! currentVmStatus = self.AsyncGetVMStatus vmId
                        match currentVmStatus.["status"].TryGetString() with
                        | false, _ -> failwith "Could not get 'status' field from VM status object"
                        | true, "error" -> failwith "VM status is 'error'"
                        | true, "unknown" -> failwith "VM status is 'unknown'"
                        | true, "stopped" -> 
                            do! self.AsyncSendRequest($"/api/v1/vm/{vmId}/start", HttpMethod.Patch) |> Async.Ignore
                        | true, "pending" -> return! loop ()
                        | true, "running" ->
                            return ()
                        | true, unknownStatus -> failwith $"Unknown VM status: '{unknownStatus}'"
                }

            return! loop ()
        }

    member private self.AsyncWaitForPayment (maxWaitTime: TimeSpan) (timeBetweenRetries: TimeSpan) (paymentId: string) =
        async {
            let stopWatch = Diagnostics.Stopwatch()
            stopWatch.Start()

            let rec loop () =
                async {
                    if stopWatch.Elapsed >= maxWaitTime then
                        return failwith $"Payment id={paymentId} is still not paid after {maxWaitTime}."
                    else
                        do! Async.Sleep timeBetweenRetries
                        let! response = self.AsyncSendRequest($"/api/v1/payment/{paymentId}", HttpMethod.Get)
                        let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                        let responseJson = JsonDocument.Parse(responseBody).RootElement.GetProperty "data"
                        if responseJson.GetProperty("is_paid").GetBoolean() then
                            return ()
                        else
                            return! loop ()
                }

            return! loop ()
        }

    member private self.AsyncUpdateVM (vmId: uint64) (requestProperties: ImmutableDictionary<string, PropertyValue>) =
        async {
            let! vmStatus = self.AsyncGetVMStatus vmId

            let _, vmSshKeyId = vmStatus.["ssh_key_id"].TryGetString()
            let requestSshKeyId = self.GetPropertyString(requestProperties, "ssh_key_id", __LINE__)

            if vmSshKeyId <> requestSshKeyId then
                let vmPatchRequestBody = {| ssh_key_id = uint64 requestSshKeyId |}
                do! 
                    self.AsyncSendRequest($"/api/v1/vm/{vmId}", HttpMethod.Patch, Json.JsonContent.Create vmPatchRequestBody)
                    |> Async.Ignore

            do! self.AsyncSendRequest($"/api/v1/vm/{vmId}/re-install", HttpMethod.Patch) |> Async.Ignore
            do! self.AsyncWaitForVMToBeRunning (TimeSpan.FromMinutes 5.0) (TimeSpan.FromSeconds 5.0) vmId
            
            let! currentVmStatus = self.AsyncGetVMStatus vmId
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
                
        let vmSshKeyProperty =
            """
                                "ssh_key_id": {
                                    "type": "string",
                                    "description": "ID of SSH key installed on VM."
                                }
            """

        let vmInputProperties = 
            sprintf
                """{
%s,
                                "template_id": {
                                    "type": "number",
                                    "description": "VM template Id"
                                },
                                "image_id": {
                                    "type": "number",
                                    "description": "VM image Id"
                                }
                }"""
                vmSshKeyProperty

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
                                        "type": "string"
                                    },
                                    "description": "VM IP addresses."
                                }
                }"""
                vmSshKeyProperty

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
        let diff = request.NewInputs.Except request.OldState 
        let changedPropertiesNames = diff |> Seq.map (fun pair -> pair.Key) |> Seq.toArray
        let hasChanges = changedPropertiesNames.Length > 0

        if request.Type = sshKeyResourceName then
            Task.FromResult <| DiffResponse(Changes = hasChanges, Replaces = changedPropertiesNames)
        elif request.Type = vmResourceName then
            Task.FromResult <| DiffResponse(Changes = hasChanges, Diffs = changedPropertiesNames)
        else
            failwith $"Unknown resource type '{request.Type}'"

    member private self.AsyncCreate(request: CreateRequest): Async<CreateResponse> =
        async {
            if request.Type = sshKeyResourceName then
                let createSshKey = 
                    let name = self.GetPropertyString(request.Properties, "name", __LINE__)
                    let key_data = self.GetPropertyString(request.Properties, "key_data", __LINE__)
                    {| name = name; key_data = key_data |}
                let! response = self.AsyncSendRequest("/api/v1/ssh-key", HttpMethod.Post, Json.JsonContent.Create createSshKey)
                let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                let json = JsonDocument.Parse(responseBody).RootElement.GetProperty "data"
                let id = json.GetProperty("id").GetUInt64().ToString()
                return CreateResponse(Id = id, Properties = request.Properties)
            elif request.Type = vmResourceName then
                let createVmRequestObject =
                    let templateId = self.GetPropertyNumber(request.Properties, "template_id", __LINE__)
                    let imageId = self.GetPropertyNumber(request.Properties, "image_id", __LINE__)
                    let sshKeyId = self.GetPropertyString(request.Properties, "ssh_key_id", __LINE__)
                    {|
                        template_id = templateId
                        image_id = imageId
                        ssh_key_id = uint64 sshKeyId
                    |}
                let! response = self.AsyncSendRequest("/api/v1/vm", HttpMethod.Post, Json.JsonContent.Create createVmRequestObject)
                let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                let vmStatus = JsonDocument.Parse(responseBody).RootElement.GetProperty "data"
                let vmId = vmStatus.GetProperty("id").GetUInt64()
                
                // Send invoice via Tg
                let! invoiceResponse = self.AsyncSendRequest($"/api/v1/vm/{vmId}/renew?method=lightning", HttpMethod.Get)
                let! invoiceResponseBody = invoiceResponse.Content.ReadAsStringAsync() |> Async.AwaitTask
                let invoiceData = JsonDocument.Parse(invoiceResponseBody).RootElement.GetProperty "data"
                let paymentId = invoiceData.GetProperty("id").GetString()
                let invoice = invoiceData.GetProperty("data").GetProperty("lightning").GetString()
                let amount = invoiceData.GetProperty("amount").GetUInt64()
                let tgMessage = $"[Automated Message]
Invoice for VM '{request.Name}' ({amount} sats):"
                let tgMessageWithInvoice = invoice

                let sendTgScriptPath = 
                    IO.Path.Combine(Environment.CurrentDirectory, "..", "TravelBudsFrontend", "scripts", "sendTelegramMessage.fsx")
                for message in [ tgMessage; tgMessageWithInvoice ] do
                    let sendTgResult =
                        Execute(
                            { ProcessDetails.Command = "dotnet"; Arguments = $"fsi {sendTgScriptPath} \"{message}\"" }, 
                            Echo.Off
                        )
                    match sendTgResult.Result with
                    | Error (errorCode, output) -> 
                        return failwith $"""Sending Telegram message with invoice failed with code {errorCode}.
STDOUT: {output.StdOut}
STDERR: {output.StdErr}
Working directory: {Environment.CurrentDirectory}
"""
                    | _ -> ()
                
                do!
                    self.AsyncWaitForPayment
                        (TimeSpan.FromMinutes 10.0)
                        (TimeSpan.FromSeconds 5.0)
                        paymentId
                do!
                    self.AsyncWaitForVMToBeRunning
                        (TimeSpan.FromMinutes 5.0)
                        (TimeSpan.FromSeconds 5.0)
                        vmId

                let! currentVmStatus = self.AsyncGetVMStatus vmId
                let updatedProperties = request.Properties.SetItems currentVmStatus
                return CreateResponse(Id = string vmId, Properties = updatedProperties)
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
                let properties = request.Olds.SetItems request.News
                let! updatedProperties = self.AsyncUpdateVM (uint64 request.Id) properties
                return UpdateResponse(Properties = updatedProperties)
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
                return failwith $"Resource {vmResourceName} does not support deletion."
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
                let json = JsonDocument.Parse(responseBody).RootElement.GetProperty "data"
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
                let! vmProperties = self.AsyncGetVMStatus (uint64 request.Id)
                return ReadResponse(Id = request.Id, Properties = vmProperties, Inputs = request.Inputs)
            else
                return failwith $"Unknown resource type '{request.Type}'"
        }

    override self.Read (request: ReadRequest, ct: CancellationToken): Task<ReadResponse> = 
        Async.StartAsTask(self.AsyncRead request, TaskCreationOptions.None, ct)
