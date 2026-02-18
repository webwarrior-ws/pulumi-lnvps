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

            let status = PropertyValue(vmStatusJson.GetProperty("status").GetString())
            let ipAssignments =
                vmStatusJson.GetProperty("ip_assignments").EnumerateArray()
                |> Seq.map (fun jsonObject -> 
                    let ip = jsonObject.GetProperty("ip").GetString()
                    ImmutableDictionary.CreateRange(Seq.singleton <| KeyValuePair("ip", PropertyValue ip))
                    |> PropertyValue)
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
            while stopWatch.Elapsed < maxWaitTime do
                printfn "Waiting for VM to be running..."
                do! Async.Sleep timeBetweenRetires
                let! currentVmStatus = self.AsyncGetVMStatus vmId
                match currentVmStatus.["status"].TryGetString() with
                | false, _ -> failwith "Could not get 'status' field from VM status object"
                | true, "error" -> failwith "VM status is 'error'"
                | true, "unknown" -> failwith "VM status is 'unknown'"
                | true, "stopped" -> 
                    do! self.AsyncSendRequest($"/api/v1/vm/{vmId}/start", HttpMethod.Patch) |> Async.Ignore
                | true, "pending" -> ()
                | true, "running" ->
                    printfn "VM is ready"
                    return ()
                | true, unknownStatus -> failwith $"Unknown VM status: '{unknownStatus}'"
            return failwith $"VM is still not runninng after {maxWaitTime}."
        }

    member private self.AsyncWaitForPayment (maxWaitTime: TimeSpan) (timeBetweenRetires: TimeSpan) (paymentId: string) =
        async {
            let stopWatch = Diagnostics.Stopwatch()
            stopWatch.Start()
            while stopWatch.Elapsed < maxWaitTime do
                printfn "Waiting for payment to be accepted..."
                do! Async.Sleep timeBetweenRetires
                let! response = self.AsyncSendRequest($"/api/v1/payment/{paymentId}", HttpMethod.Get)
                let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                let responseJson = JsonDocument.Parse(responseBody).RootElement.GetProperty "data"
                if responseJson.GetProperty("is_paid").GetBoolean() then
                    return ()
            return failwith $"Pyment id={paymentId} is still not paid after {maxWaitTime}."
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
            printfn $"Re-installing VM with Id={vmId}..."
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
                                        "type": "object",
                                        "$ref": "#/resources/%s"
                                    },
                                    "description": "VM IP addresses."
                                }
                }"""
                vmSshKeyProperty
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
                let message = $"[Automated Message]\
Invoice for VM '{request.Name}' ({amount} sats):\
```\
{invoice}\
```"
                printfn "Current directory: %s" Environment.CurrentDirectory
                let sendTgScriptPath = 
                    IO.Path.Combine(Environment.CurrentDirectory, "TravelBudsFrontend", "scripts", "sendTelegramMessage.fsx")
                let sendTgResult =
                    Execute(
                        { ProcessDetails.Command = "dotnet"; Arguments = $"fsi {sendTgScriptPath} \"{message}\"" }, 
                        Echo.All
                    )
                match sendTgResult.Result with
                | Error (_, _) -> return failwith "Sending Telegram message with invoice failed"
                | _ -> ()
                
                do! self.AsyncWaitForPayment (TimeSpan.FromMinutes 10.0) (TimeSpan.FromSeconds 5.0) paymentId
                do! self.AsyncWaitForVMToBeRunning (TimeSpan.FromMinutes 5.0) (TimeSpan.FromSeconds 5.0) vmId

                let! updatedProperties = self.AsyncUpdateVM vmId request.Properties
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
                let properties = request.Olds.AddRange request.News
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
