/// The files the editor opens: dummy F# source repeated to the requested length.
module EditorShowcase.Sample

open System

/// A file of the explorer: its name, its number of lines, and its lines, built when first read.
type SampleFile(name: string, count: int, build: int -> ResizeArray<string>) =
    let lines = lazy (build count)
    member _.Name = name
    member _.Lines = lines.Value
    member _.Count = count

/// One section of dummy source. `@@N` is replaced by the section number and `@@M` by a number derived from it,
/// so a few lines of every section are unique and the rest are shared string instances.
let private section =
    """// ==== Section @@N: orbital propagation kernels ==================================
namespace Ranvier.Samples.Section@@N

open System
open System.Numerics

/// A body in the simulation. Mu is the gravitational parameter in km^3/s^2.
[<Struct; CustomEquality; NoComparison>]
type Body =
    { Name: string
      Mu: float
      Radius: float<km>
      mutable Epoch: DateTime }

type Integrator =
    | Rk4 of step: float
    | DormandPrince of tolerance: float * maxStep: float
    | Symplectic of order: int

module Kepler@@N =
    [<Literal>]
    let MaxIterations = @@M

    let private twoPi = 2.0 * Math.PI
    let inline sq (x: float) = x * x

    /// Solves Kepler's equation M = E - e sin E by Newton iteration.
    let rec solve (e: float) (m: float) (guess: float) (n: int) : float =
        let f = guess - e * sin guess - m
        let slope = 1.0 - e * cos guess
        let next = guess - f / slope

        if abs (next - guess) < 1e-12 || n >= MaxIterations then next
        else solve e m next (n + 1)

    let period (body: Body) (a: float) = twoPi * sqrt (a ** 3.0 / body.Mu)

    let describe (body: Body) =
        match body.Name with
        | "Earth" | "Terra" -> $"home, mu = {body.Mu:F3}"
        | name when name.StartsWith "L" -> sprintf "libration point %s" name
        | _ -> "unclassified body \"" + body.Name + "\""

    let stateAt (body: Body) (elements: float[]) (t: float) : Vector3 * Vector3 =
        let a, e, i = elements[0], elements[1], elements[2]
        let m = (t / period body a) * twoPi % twoPi (* mean anomaly *)
        let ea = solve e m m 0
        let r = a * (1.0 - e * cos ea)
        let x = r * cos ea |> float32
        let y = r * sin ea * cos i |> float32
        Vector3(x, y, 0.0f), Vector3(-y, x, 0.0f) * 0.5f

    // TODO(@@N): replace the fixed step with an adaptive controller
    let propagate (integrator: Integrator) (bodies: Body list) (dt: float) =
        let scale =
            match integrator with
            | Rk4 step -> min step dt
            | DormandPrince(tolerance, maxStep) -> min maxStep (dt * tolerance ** 0.2)
            | Symplectic order -> dt / float (max 1 order)

        bodies
        |> List.map (fun b -> { b with Epoch = b.Epoch.AddSeconds scale })
        |> List.filter (fun b -> b.Mu > 0.0 && b.Radius <> 0.0<km>)
        |> List.sortBy (fun b -> b.Name)

    let checksum (values: byte[]) =
        let mutable hash = 0x811C9DC5u
        for value in values do
            hash <- (hash ^^^ uint32 value) * 16777619u
        hash &&& 0xFFFF_FFFFu, values.Length, '\n', "section @@N"
"""

let private template =
    section.Replace("\r", "").Split '\n'
    |> Array.map (fun line -> line, line.Contains "@@")

/// `count` lines made of numbered copies of the dummy section.
let private generate (count: int) : ResizeArray<string> =
    let lines = ResizeArray<string>(count)
    let mutable number = 1

    while lines.Count < count do
        let label = string number
        let derived = string (20 + number % 80)

        for text, varies in template do
            if lines.Count < count then
                lines.Add(if varies then text.Replace("@@N", label).Replace("@@M", derived) else text)

        number <- number + 1

    lines

/// The three files of the explorer, largest first.
let files () : SampleFile[] =
    [| SampleFile("Ephemeris.fs", 1_000_000, generate)
       SampleFile("Integrators.fs", 20_000, generate)
       SampleFile("Program.fs", 300, generate) |]

/// A count with thousands separators, the same on every machine.
let grouped (value: int) = value.ToString("N0", Globalization.CultureInfo.InvariantCulture)
