module Ranvier.Plume.Tests.Helpers

open System
open System.Text
open Expecto
open Ranvier.Plume

/// Runs `check` with a seeded generator for seeds 1 to `count`; a failure names its seed.
let forSeeds (count: int) (check: Random -> unit) =
    for seed in 1..count do
        try
            check (Random seed)
        with
        | :? AssertException as failure -> failtestf "seed %d: %s" seed failure.Message
        | failure -> failtestf "seed %d raised %s: %s" seed (failure.GetType().Name) failure.Message

/// A string of 0 to `maxLength` characters drawn from `alphabet`.
let randomString (rng: Random) (alphabet: string) (maxLength: int) : string =
    let length = rng.Next (0, maxLength + 1)
    let builder = StringBuilder length

    for _ in 1..length do
        builder.Append alphabet[rng.Next alphabet.Length] |> ignore

    builder.ToString ()

/// A text holds the characters of `expected` and the line structure a parse of them gives.
let expectText (expected: string) (actual: Text) (message: string) =
    Expect.equal (Text.toString actual) expected message
    Expect.equal (Text.length actual) expected.Length (message + ": length")
    Expect.equal (Text.lines actual) (Text.lines (Text.ofString expected)) (message + ": lines")
    Expect.isTrue (Text.contentEquals actual (Text.ofString expected)) (message + ": line structure")
