open System
open System.Threading

open Pulumi.Experimental.Provider

open Pulumi.LnVps

[<EntryPoint>]
let main args =
    let nostrPrivateKey = Environment.GetEnvironmentVariable LnVpsProvider.NostrPrivateKeyEnvVarName
    Provider.Serve(args, LnVpsProvider.Version, (fun _host -> new LnVpsProvider(nostrPrivateKey)), CancellationToken.None)
    |> Async.AwaitTask
    |> Async.RunSynchronously
    0
