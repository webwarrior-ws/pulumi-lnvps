open System
open System.Threading

open Pulumi.Experimental.Provider

open Pulumi.LNVPS

[<EntryPoint>]
let main args =
    let nostrPrivateKey = Environment.GetEnvironmentVariable LNVPSProvider.NostrPrivateKeyEnvVarName
    Provider.Serve(args, LNVPSProvider.Version, (fun _host -> new LNVPSProvider(nostrPrivateKey)), CancellationToken.None)
    |> Async.AwaitTask
    |> Async.RunSynchronously
    0
