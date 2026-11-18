module Ranvier.Plume.View.Tests.Main

open Expecto

[<EntryPoint>]
let main argv =
    runTestsInAssemblyWithCLIArgs [ Sequenced ] argv
