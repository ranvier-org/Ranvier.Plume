/// Scans for line-break bytes in UTF-8 text.
module internal Ranvier.Plume.LineScan

open System
#if !FABLE_COMPILER
open System.Numerics
open System.Runtime.Intrinsics
#endif

/// Writes the indexes of the `\n` and `\r` bytes of `bytes` at or after `start` into `positions`, in order.
/// Returns the number written and the index to resume from; the scan is complete when that index is `bytes.Length`.
let scalar (bytes: ReadOnlySpan<byte>) (start: int) (positions: Span<int>) : struct (int * int) =
    let capacity = positions.Length
    let mutable count = 0
    let mutable i = start

    while i < bytes.Length && count < capacity do
        let b = bytes[i]

        if b = 0x0Auy || b = 0x0Duy then
            positions[count] <- i
            count <- count + 1

        i <- i + 1

    struct (count, i)

/// `scalar`, comparing 32 or 16 bytes at a time where the hardware supports it.
let vectorised (bytes: ReadOnlySpan<byte>) (start: int) (positions: Span<int>) : struct (int * int) =
#if FABLE_COMPILER
    scalar bytes start positions
#else
    let capacity = positions.Length
    let mutable count = 0
    let mutable i = start

    if Vector256.IsHardwareAccelerated then
        let lf = Vector256.Create 0x0Auy
        let cr = Vector256.Create 0x0Duy

        while i + 32 <= bytes.Length && count + 32 <= capacity do
            let block = Vector256.Create<byte> (bytes.Slice (i, 32))
            let mutable mask = (Vector256.Equals (block, lf) ||| Vector256.Equals (block, cr)).ExtractMostSignificantBits ()

            while mask <> 0u do
                positions[count] <- i + BitOperations.TrailingZeroCount mask
                count <- count + 1
                mask <- mask &&& (mask - 1u)

            i <- i + 32
    elif Vector128.IsHardwareAccelerated then
        let lf = Vector128.Create 0x0Auy
        let cr = Vector128.Create 0x0Duy

        while i + 16 <= bytes.Length && count + 16 <= capacity do
            let block = Vector128.Create<byte> (bytes.Slice (i, 16))
            let mutable mask = (Vector128.Equals (block, lf) ||| Vector128.Equals (block, cr)).ExtractMostSignificantBits ()

            while mask <> 0u do
                positions[count] <- i + BitOperations.TrailingZeroCount mask
                count <- count + 1
                mask <- mask &&& (mask - 1u)

            i <- i + 16

    while i < bytes.Length && count < capacity do
        let b = bytes[i]

        if b = 0x0Auy || b = 0x0Duy then
            positions[count] <- i
            count <- count + 1

        i <- i + 1

    struct (count, i)
#endif
