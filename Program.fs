open System
open System.Threading

open Pulumi.Experimental.Provider

open Pulumi.LNVPS

[<EntryPoint>]
let main args =
    let nostrAuthEvent = Environment.GetEnvironmentVariable LNVPSProvider.NostrEventEnvVarName
    Provider.Serve(args, LNVPSProvider.Version, (fun _host -> new LNVPSProvider(nostrAuthEvent)), CancellationToken.None)
    |> Async.AwaitTask
    |> Async.RunSynchronously
    0
