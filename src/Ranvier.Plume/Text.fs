namespace Ranvier.Plume

open System
open System.Buffers
open System.Text

[<NoEquality; NoComparison>]
type internal LeafData =
    {
        /// The content of every line, without line breaks.
        Utf8Bytes: byte[]
        /// Where each line starts in `Utf8Bytes`; one more entry than lines.
        ByteOffsets: int[]
        EndingKinds: byte[]
        /// Where each line starts in UTF-16 units, line breaks included; one more entry than lines.
        LineOffsets: int[]
        LineCount: int
        CharLength: int
        mutable CachedLines: WeakReference<string[]>
    }

[<NoEquality; NoComparison>]
type internal BranchData =
    {
        Left: Node
        Right: Node
        Height: int
        LineCount: int
        CharLength: int
    }

and [<NoEquality; NoComparison>] internal Node =
    | Leaf of LeafData
    | Branch of BranchData

    member this.Height =
        match this with
        | Leaf _ -> 1
        | Branch b -> b.Height

    member this.LineCount =
        match this with
        | Leaf l -> l.LineCount
        | Branch b -> b.LineCount

    member this.CharLength =
        match this with
        | Leaf l -> l.CharLength
        | Branch b -> b.CharLength

/// The rope: an AVL tree of leaves of up to 128 lines. Every line but the last of a text ends with a line break, and
/// the line structure of a text is the one a parse of its characters gives.
module internal Rope =

    [<Literal>]
    let ChunkLines = 128

    [<Literal>]
    let EndingNone = 0uy

    [<Literal>]
    let EndingLf = 1uy

    [<Literal>]
    let EndingCrLf = 2uy

    [<Literal>]
    let EndingCr = 3uy

    let breakChars = [| '\n'; '\r' |]

    let inline endingCharLen (kind: byte) : int =
        match kind with
        | 1uy -> 1
        | 2uy -> 2
        | 3uy -> 1
        | _ -> 0

    let inline endingString (kind: byte) : string =
        match kind with
        | 1uy -> "\n"
        | 2uy -> "\r\n"
        | 3uy -> "\r"
        | _ -> ""

    // The string arrays of the 64 leaves read most recently stay strongly held, so a line read before an edit
    // elsewhere is the same string instance after it. Other leaves hold their strings weakly.
    let private recentGate = obj ()
    let private recentRing: string[][] = Array.zeroCreate 64
    let mutable private recentCursor = 0

    let private pinLeafArray (lines: string[]) =
        lock recentGate (fun () ->
            recentRing[recentCursor] <- lines
            recentCursor <- (recentCursor + 1) &&& 63)

    let inline leafLineCharLen (leaf: LeafData) (i: int) : int =
        (leaf.LineOffsets[i + 1] - leaf.LineOffsets[i])
        - endingCharLen leaf.EndingKinds[i]

    let private decodeLine (leaf: LeafData) (i: int) : string =
        let start = leaf.ByteOffsets[i]
        let length = leaf.ByteOffsets[i + 1] - start

        if length = 0 then
            ""
        else
            Encoding.UTF8.GetString (leaf.Utf8Bytes, start, length)

    let leafLineText (leaf: LeafData) (i: int) : string =
        let cached = leaf.CachedLines
        let mutable lines: string[] = null

        if
            not (isNull (box cached))
            && cached.TryGetTarget &lines
            && not (isNull lines)
        then
            let existing = lines[i]

            if not (isNull existing) then
                existing
            else
                let line = decodeLine leaf i
                lines[i] <- line
                line
        else
            let fresh = Array.zeroCreate<string> leaf.LineCount
            let line = decodeLine leaf i
            fresh[i] <- line
            leaf.CachedLines <- WeakReference<string[]> fresh
            pinLeafArray fresh
            line

    let makeEmptyLeaf (lastKind: byte) : LeafData =
        let endLength = endingCharLen lastKind

        {
            Utf8Bytes = Array.empty
            ByteOffsets = [| 0; 0 |]
            EndingKinds = [| lastKind |]
            LineOffsets = [| 0; endLength |]
            LineCount = 1
            CharLength = endLength
            CachedLines = Unchecked.defaultof<WeakReference<string[]>>
        }

    /// A leaf of `lines`, which hold no line break. The leaf keeps the array.
    let makeLeafFromStrings (lines: string[]) (endingKinds: byte[]) : LeafData =
        let n = lines.Length
        let mutable totalBytes = 0

        for k = 0 to n - 1 do
            totalBytes <- totalBytes + Encoding.UTF8.GetByteCount lines[k]

        let utf8 =
            if totalBytes = 0 then
                Array.empty
            else
                Array.zeroCreate<byte> totalBytes

        let byteOffsets = Array.zeroCreate<int> (n + 1)
        let lineOffsets = Array.zeroCreate<int> (n + 1)
        let mutable byteAt = 0
        let mutable charAt = 0

        for k = 0 to n - 1 do
            byteOffsets[k] <- byteAt
            lineOffsets[k] <- charAt
            let line = lines[k]

            if line.Length > 0 then
                byteAt <- byteAt + Encoding.UTF8.GetBytes (line.AsSpan (), utf8.AsSpan byteAt)

            charAt <- charAt + line.Length + endingCharLen endingKinds[k]

        byteOffsets[n] <- byteAt
        lineOffsets[n] <- charAt

        {
            Utf8Bytes = utf8
            ByteOffsets = byteOffsets
            EndingKinds = endingKinds
            LineOffsets = lineOffsets
            LineCount = n
            CharLength = charAt
            CachedLines = WeakReference<string[]> lines
        }

    let sliceLeafRange (leaf: LeafData) (first: int) (last: int) : LeafData =
        let count = last - first
        let byteBase = leaf.ByteOffsets[first]
        let byteLength = leaf.ByteOffsets[last] - byteBase

        let utf8 =
            if byteLength = 0 then
                Array.empty
            else
                let target = Array.zeroCreate<byte> byteLength
                Buffer.BlockCopy (leaf.Utf8Bytes, byteBase, target, 0, byteLength)
                target

        let byteOffsets = Array.zeroCreate<int> (count + 1)
        let lineOffsets = Array.zeroCreate<int> (count + 1)
        let endings = Array.zeroCreate<byte> count
        let charBase = leaf.LineOffsets[first]

        for k = 0 to count - 1 do
            byteOffsets[k] <- leaf.ByteOffsets[first + k] - byteBase
            lineOffsets[k] <- leaf.LineOffsets[first + k] - charBase
            endings[k] <- leaf.EndingKinds[first + k]

        byteOffsets[count] <- byteLength
        lineOffsets[count] <- leaf.LineOffsets[last] - charBase

        let mutable held: string[] = null

        let cache =
            if
                not (isNull (box leaf.CachedLines))
                && leaf.CachedLines.TryGetTarget &held
                && not (isNull held)
            then
                let kept = Array.sub held first count
                pinLeafArray kept
                WeakReference<string[]> kept
            else
                Unchecked.defaultof<WeakReference<string[]>>

        {
            Utf8Bytes = utf8
            ByteOffsets = byteOffsets
            EndingKinds = endings
            LineOffsets = lineOffsets
            LineCount = count
            CharLength = lineOffsets[count]
            CachedLines = cache
        }

    let makeBranch (left: Node) (right: Node) : Node =
        Branch
            {
                Left = left
                Right = right
                Height = 1 + max left.Height right.Height
                LineCount = left.LineCount + right.LineCount
                CharLength = left.CharLength + right.CharLength
            }

    let private balanceFactor (node: Node) =
        match node with
        | Leaf _ -> 0
        | Branch b -> b.Left.Height - b.Right.Height

    let private rotateRight (node: Node) : Node =
        match node with
        | Branch y ->
            match y.Left with
            | Branch x -> makeBranch x.Left (makeBranch x.Right y.Right)
            | Leaf _ -> node
        | Leaf _ -> node

    let private rotateLeft (node: Node) : Node =
        match node with
        | Branch x ->
            match x.Right with
            | Branch y -> makeBranch (makeBranch x.Left y.Left) y.Right
            | Leaf _ -> node
        | Leaf _ -> node

    let private rebalance (node: Node) : Node =
        let factor = balanceFactor node

        if factor > 1 then
            match node with
            | Branch b ->
                if balanceFactor b.Left < 0 then
                    makeBranch (rotateLeft b.Left) b.Right |> rotateRight
                else
                    rotateRight node
            | Leaf _ -> node
        elif factor < -1 then
            match node with
            | Branch b ->
                if balanceFactor b.Right > 0 then
                    makeBranch b.Left (rotateRight b.Right) |> rotateLeft
                else
                    rotateLeft node
            | Leaf _ -> node
        else
            node

    /// One leaf of the lines of `a` and then of `b`. Line strings already read stay the same instances.
    let private mergeLeaves (a: LeafData) (b: LeafData) : LeafData =
        let count = a.LineCount + b.LineCount
        let bytes = a.Utf8Bytes.Length
        let utf8 = Array.zeroCreate<byte> (bytes + b.Utf8Bytes.Length)
        Buffer.BlockCopy (a.Utf8Bytes, 0, utf8, 0, bytes)
        Buffer.BlockCopy (b.Utf8Bytes, 0, utf8, bytes, b.Utf8Bytes.Length)
        let byteOffsets = Array.zeroCreate<int> (count + 1)
        let lineOffsets = Array.zeroCreate<int> (count + 1)
        let endings = Array.zeroCreate<byte> count

        for k = 0 to a.LineCount - 1 do
            byteOffsets[k] <- a.ByteOffsets[k]
            lineOffsets[k] <- a.LineOffsets[k]
            endings[k] <- a.EndingKinds[k]

        for k = 0 to b.LineCount do
            byteOffsets[a.LineCount + k] <- b.ByteOffsets[k] + bytes
            lineOffsets[a.LineCount + k] <- b.LineOffsets[k] + a.CharLength

        for k = 0 to b.LineCount - 1 do
            endings[a.LineCount + k] <- b.EndingKinds[k]

        let mutable heldA: string[] = null
        let mutable heldB: string[] = null

        let aliveA =
            not (isNull (box a.CachedLines))
            && a.CachedLines.TryGetTarget &heldA
            && not (isNull heldA)

        let aliveB =
            not (isNull (box b.CachedLines))
            && b.CachedLines.TryGetTarget &heldB
            && not (isNull heldB)

        let cache =
            if aliveA || aliveB then
                let lines = Array.zeroCreate<string> count

                if aliveA then
                    Array.blit heldA 0 lines 0 a.LineCount

                if aliveB then
                    Array.blit heldB 0 lines a.LineCount b.LineCount

                pinLeafArray lines
                WeakReference<string[]> lines
            else
                Unchecked.defaultof<WeakReference<string[]>>

        {
            Utf8Bytes = utf8
            ByteOffsets = byteOffsets
            EndingKinds = endings
            LineOffsets = lineOffsets
            LineCount = count
            CharLength = a.CharLength + b.CharLength
            CachedLines = cache
        }

    let inline private fits (a: LeafData) (b: LeafData) : bool = a.LineCount + b.LineCount <= ChunkLines

    let rec concat (left: Node) (right: Node) : Node =
        let difference = left.Height - right.Height

        if abs difference <= 1 then
            // Two small leaves that meet become one, so edits that add lines leave leaves that fill up.
            match left, right with
            | Leaf a, Leaf b when fits a b -> Leaf (mergeLeaves a b)
            | Branch l, Leaf b ->
                match l.Right with
                | Leaf a when fits a b -> makeBranch l.Left (Leaf (mergeLeaves a b))
                | _ -> makeBranch left right
            | Leaf a, Branch r ->
                match r.Left with
                | Leaf b when fits a b -> makeBranch (Leaf (mergeLeaves a b)) r.Right
                | _ -> makeBranch left right
            | _ -> makeBranch left right
        elif difference > 1 then
            match left with
            | Branch l -> makeBranch l.Left (concat l.Right right) |> rebalance
            | Leaf _ -> makeBranch left right
        else
            match right with
            | Branch r -> makeBranch (concat left r.Left) r.Right |> rebalance
            | Leaf _ -> makeBranch left right

    let concatOpt (left: Node option) (right: Node option) : Node option =
        match left, right with
        | None, None -> None
        | Some _, None -> left
        | None, Some _ -> right
        | Some l, Some r -> Some (concat l r)

    /// The first `line` lines of `node`, and the rest.
    let rec split (line: int) (node: Node) : Node option * Node option =
        if line <= 0 then
            None, Some node
        elif line >= node.LineCount then
            Some node, None
        else
            match node with
            | Leaf leaf -> Some (Leaf (sliceLeafRange leaf 0 line)), Some (Leaf (sliceLeafRange leaf line leaf.LineCount))
            | Branch b ->
                let leftCount = b.Left.LineCount

                if line = leftCount then
                    Some b.Left, Some b.Right
                elif line < leftCount then
                    let first, rest = split line b.Left
                    first, concatOpt rest (Some b.Right)
                else
                    let first, rest = split (line - leftCount) b.Right
                    concatOpt (Some b.Left) first, rest

    let private flushChars (text: string) (spans: ResizeArray<struct (int * int * byte)>) (leaves: ResizeArray<LeafData>) =
        let n = spans.Count

        if n > 0 then
            let mutable totalBytes = 0

            for k = 0 to n - 1 do
                let struct (start, length, _) = spans[k]

                if length > 0 then
                    totalBytes <- totalBytes + Encoding.UTF8.GetByteCount (text.AsSpan (start, length))

            let utf8 =
                if totalBytes = 0 then
                    Array.empty
                else
                    Array.zeroCreate<byte> totalBytes

            let byteOffsets = Array.zeroCreate<int> (n + 1)
            let lineOffsets = Array.zeroCreate<int> (n + 1)
            let endingKinds = Array.zeroCreate<byte> n
            let mutable byteAt = 0
            let mutable charAt = 0

            for k = 0 to n - 1 do
                let struct (start, length, kind) = spans[k]
                byteOffsets[k] <- byteAt
                lineOffsets[k] <- charAt
                endingKinds[k] <- kind

                if length > 0 then
                    byteAt <- byteAt + Encoding.UTF8.GetBytes (text.AsSpan (start, length), utf8.AsSpan byteAt)

                charAt <- charAt + length + endingCharLen kind

            byteOffsets[n] <- byteAt
            lineOffsets[n] <- charAt

            leaves.Add
                {
                    Utf8Bytes = utf8
                    ByteOffsets = byteOffsets
                    EndingKinds = endingKinds
                    LineOffsets = lineOffsets
                    LineCount = n
                    CharLength = charAt
                    CachedLines = Unchecked.defaultof<WeakReference<string[]>>
                }

            spans.Clear ()

    /// The leaves of `text`. The last line takes `lastKind` as its ending.
    let parseTextToLeaves (text: string) (lastKind: byte) (chunkSize: int) : LeafData[] =
        if String.IsNullOrEmpty text then
            [| makeEmptyLeaf lastKind |]
        else
            let leaves = ResizeArray<LeafData> ()
            let spans = ResizeArray<struct (int * int * byte)> (min chunkSize 16)
            let length = text.Length
            let mutable lineStart = 0
            let mutable searching = true

            while searching do
                let at =
                    if lineStart >= length then
                        -1
                    else
                        text.IndexOfAny (breakChars, lineStart)

                if at < 0 then
                    searching <- false
                else
                    let kind =
                        if text[at] = '\n' then EndingLf
                        elif at + 1 < length && text[at + 1] = '\n' then EndingCrLf
                        else EndingCr

                    spans.Add (struct (lineStart, at - lineStart, kind))
                    lineStart <- at + endingCharLen kind

                    if spans.Count >= chunkSize then
                        flushChars text spans leaves

            spans.Add (struct (lineStart, length - lineStart, lastKind))
            flushChars text spans leaves
            leaves.ToArray ()

    /// The leaves of `lines` joined with "\n".
    let parseLinesToLeaves (lines: seq<string>) (chunkSize: int) : LeafData[] =
        let leaves = ResizeArray<LeafData> ()
        let batch = ResizeArray<string> chunkSize
        // Set when a line holds a line break: the lines are then joined and parsed as one string.
        let mutable joined: StringBuilder = null

        for line in lines do
            let line = if isNull line then "" else line

            if not (isNull joined) then
                joined.Append('\n').Append line |> ignore
            elif line.IndexOfAny breakChars < 0 then
                if batch.Count >= chunkSize then
                    leaves.Add (makeLeafFromStrings (batch.ToArray ()) (Array.create batch.Count EndingLf))
                    batch.Clear ()

                batch.Add line
            else
                joined <- StringBuilder ()

                for leaf in leaves do
                    for i = 0 to leaf.LineCount - 1 do
                        joined.Append(leafLineText leaf i).Append '\n' |> ignore

                for held in batch do
                    joined.Append(held).Append '\n' |> ignore

                joined.Append line |> ignore

        if not (isNull joined) then
            parseTextToLeaves (joined.ToString ()) EndingNone chunkSize
        elif batch.Count = 0 then
            [| makeEmptyLeaf EndingNone |]
        else
            let kinds = Array.create batch.Count EndingLf
            kinds[kinds.Length - 1] <- EndingNone
            leaves.Add (makeLeafFromStrings (batch.ToArray ()) kinds)
            leaves.ToArray ()

    let private flushUtf8Spans (bytes: ReadOnlySpan<byte>) (spans: ResizeArray<struct (int * int * byte)>) (leaves: ResizeArray<LeafData>) =
        let n = spans.Count

        if n > 0 then
            let mutable totalBytes = 0

            for k = 0 to n - 1 do
                let struct (_, length, _) = spans[k]
                totalBytes <- totalBytes + length

            let utf8 =
                if totalBytes = 0 then
                    Array.empty
                else
                    Array.zeroCreate<byte> totalBytes

            let byteOffsets = Array.zeroCreate<int> (n + 1)
            let lineOffsets = Array.zeroCreate<int> (n + 1)
            let endingKinds = Array.zeroCreate<byte> n
            let mutable byteAt = 0
            let mutable charAt = 0

            for k = 0 to n - 1 do
                let struct (start, length, kind) = spans[k]
                byteOffsets[k] <- byteAt
                lineOffsets[k] <- charAt
                endingKinds[k] <- kind

                let chars =
                    if length = 0 then
                        0
                    else
                        let slice = bytes.Slice (start, length)
                        slice.CopyTo (utf8.AsSpan (byteAt, length))
                        byteAt <- byteAt + length
                        Encoding.UTF8.GetCharCount slice

                charAt <- charAt + chars + endingCharLen kind

            byteOffsets[n] <- byteAt
            lineOffsets[n] <- charAt

            leaves.Add
                {
                    Utf8Bytes = utf8
                    ByteOffsets = byteOffsets
                    EndingKinds = endingKinds
                    LineOffsets = lineOffsets
                    LineCount = n
                    CharLength = charAt
                    CachedLines = Unchecked.defaultof<WeakReference<string[]>>
                }

            spans.Clear ()

    /// The leaves of UTF-8 `raw`, without its byte order mark.
    let parseUtf8ToLeaves (vectorised: bool) (raw: ReadOnlySpan<byte>) (chunkSize: int) : LeafData[] =
        let bytes =
            if
                raw.Length >= 3
                && raw[0] = 0xEFuy
                && raw[1] = 0xBBuy
                && raw[2] = 0xBFuy
            then
                raw.Slice 3
            else
                raw

        if bytes.Length = 0 then
            [| makeEmptyLeaf EndingNone |]
        else
            let leaves = ResizeArray<LeafData> ()
            let spans = ResizeArray<struct (int * int * byte)> chunkSize
            let positions = ArrayPool<int>.Shared.Rent 2048
            let length = bytes.Length
            let mutable resume = 0
            let mutable lineStart = 0
            // The "\n" of a "\r\n" already taken.
            let mutable skip = -1

            while resume < length do
                let struct (count, next) =
                    if vectorised then
                        LineScan.vectorised bytes resume (Span<int> positions)
                    else
                        LineScan.scalar bytes resume (Span<int> positions)

                resume <- next

                for k = 0 to count - 1 do
                    let at = positions[k]

                    if at <> skip then
                        let kind =
                            if bytes[at] = 0x0Auy then
                                EndingLf
                            elif at + 1 < length && bytes[at + 1] = 0x0Auy then
                                skip <- at + 1
                                EndingCrLf
                            else
                                EndingCr

                        spans.Add (struct (lineStart, at - lineStart, kind))
                        lineStart <- at + endingCharLen kind

                        if spans.Count >= chunkSize then
                            flushUtf8Spans bytes spans leaves

            ArrayPool<int>.Shared.Return positions
            spans.Add (struct (lineStart, length - lineStart, EndingNone))
            flushUtf8Spans bytes spans leaves
            leaves.ToArray ()

    let rec buildFromLeaves (leaves: LeafData[]) (i: int) (j: int) : Node =
        if i > j then
            Leaf (makeEmptyLeaf EndingNone)
        elif i = j then
            Leaf leaves[i]
        elif i + 1 = j then
            makeBranch (Leaf leaves[i]) (Leaf leaves[j])
        else
            let mid = i + (j - i) / 2
            makeBranch (buildFromLeaves leaves i mid) (buildFromLeaves leaves (mid + 1) j)

    let nodeOfString (text: string) (lastKind: byte) : Node =
        let leaves = parseTextToLeaves text lastKind ChunkLines
        buildFromLeaves leaves 0 (leaves.Length - 1)

    let lineNode (content: string) (kind: byte) : Node =
        Leaf (makeLeafFromStrings [| content |] [| kind |])

    let rec getLeafAndLine (line: int) (node: Node) : struct (LeafData * int) =
        match node with
        | Leaf leaf -> struct (leaf, line)
        | Branch b ->
            let leftCount = b.Left.LineCount

            if line < leftCount then
                getLeafAndLine line b.Left
            else
                getLeafAndLine (line - leftCount) b.Right

    /// The start offset of line `line`, its leaf and its index in the leaf.
    let rec locateLine (line: int) (charBase: int) (node: Node) : struct (int * LeafData * int) =
        match node with
        | Leaf leaf -> struct (charBase + leaf.LineOffsets[line], leaf, line)
        | Branch b ->
            let leftCount = b.Left.LineCount

            if line < leftCount then
                locateLine line charBase b.Left
            else
                locateLine (line - leftCount) (charBase + b.Left.CharLength) b.Right

    /// The line holding `offset`: its number, its start offset, its leaf and its index in the leaf.
    let rec locate (offset: int) (lineBase: int) (charBase: int) (node: Node) : struct (int * int * LeafData * int) =
        match node with
        | Leaf leaf ->
            let mutable low = 0
            let mutable high = leaf.LineCount - 1

            while low < high do
                let mid = (low + high + 1) >>> 1

                if leaf.LineOffsets[mid] <= offset then
                    low <- mid
                else
                    high <- mid - 1

            struct (lineBase + low, charBase + leaf.LineOffsets[low], leaf, low)
        | Branch b ->
            let leftLength = b.Left.CharLength

            if offset < leftLength then
                locate offset lineBase charBase b.Left
            else
                locate (offset - leftLength) (lineBase + b.Left.LineCount) (charBase + leftLength) b.Right

    let rec updateSingleLine (target: int) (line: string) (node: Node) : Node =
        match node with
        | Leaf leaf ->
            let n = leaf.LineCount
            let oldStart = leaf.ByteOffsets[target]
            let oldEnd = leaf.ByteOffsets[target + 1]
            let newLength = Encoding.UTF8.GetByteCount line
            let byteDelta = newLength - (oldEnd - oldStart)
            let total = leaf.Utf8Bytes.Length + byteDelta

            let utf8 =
                if total = 0 then
                    Array.empty
                else
                    Array.zeroCreate<byte> total

            if oldStart > 0 then
                Buffer.BlockCopy (leaf.Utf8Bytes, 0, utf8, 0, oldStart)

            if newLength > 0 then
                Encoding.UTF8.GetBytes (line.AsSpan (), utf8.AsSpan (oldStart, newLength))
                |> ignore

            let tail = leaf.Utf8Bytes.Length - oldEnd

            if tail > 0 then
                Buffer.BlockCopy (leaf.Utf8Bytes, oldEnd, utf8, oldStart + newLength, tail)

            let byteOffsets = Array.copy leaf.ByteOffsets

            for k = target + 1 to n do
                byteOffsets[k] <- byteOffsets[k] + byteDelta

            let charDelta = line.Length - leafLineCharLen leaf target
            let lineOffsets = Array.copy leaf.LineOffsets

            for k = target + 1 to n do
                lineOffsets[k] <- lineOffsets[k] + charDelta

            let mutable held: string[] = null

            let cache =
                if
                    not (isNull (box leaf.CachedLines))
                    && leaf.CachedLines.TryGetTarget &held
                    && not (isNull held)
                then
                    let copied = Array.copy held
                    copied[target] <- line
                    pinLeafArray copied
                    WeakReference<string[]> copied
                else
                    Unchecked.defaultof<WeakReference<string[]>>

            Leaf
                { leaf with
                    Utf8Bytes = utf8
                    ByteOffsets = byteOffsets
                    LineOffsets = lineOffsets
                    CharLength = lineOffsets[n]
                    CachedLines = cache
                }
        | Branch b ->
            let leftCount = b.Left.LineCount

            if target < leftCount then
                makeBranch (updateSingleLine target line b.Left) b.Right
            else
                makeBranch b.Left (updateSingleLine (target - leftCount) line b.Right)

    let firstLine (node: Node) : struct (string * byte) =
        let struct (leaf, i) = getLeafAndLine 0 node
        struct (leafLineText leaf i, leaf.EndingKinds[i])

    let lastLine (node: Node) : struct (string * byte) =
        let struct (leaf, i) = getLeafAndLine (node.LineCount - 1) node
        struct (leafLineText leaf i, leaf.EndingKinds[i])

    let dropFirst (node: Node) : Node option = snd (split 1 node)

    let dropLast (node: Node) : Node option =
        fst (split (node.LineCount - 1) node)

    /// `a` followed by `b`. A last line of `a` without a line break continues with the first line of `b`, and a
    /// "\r" ending `a` joins a "\n" starting `b` into one line break.
    let join (a: Node) (b: Node) : Node =
        let struct (aLast, aKind) = lastLine a
        let struct (bFirst, bKind) = firstLine b

        if aKind = EndingNone then
            let init = dropLast a

            let completesBreak =
                aLast.Length = 0
                && bFirst.Length = 0
                && bKind = EndingLf
                && (match init with
                    | Some before ->
                        let struct (_, kind) = lastLine before
                        kind = EndingCr
                    | None -> false)

            if completesBreak then
                let before = init.Value
                let struct (previous, _) = lastLine before

                (concatOpt (dropLast before) (concatOpt (Some (lineNode previous EndingCrLf)) (dropFirst b)))
                    .Value
            else
                (concatOpt init (concatOpt (Some (lineNode (aLast + bFirst) bKind)) (dropFirst b)))
                    .Value
        elif aKind = EndingCr && bFirst.Length = 0 && bKind = EndingLf then
            (concatOpt (dropLast a) (concatOpt (Some (lineNode aLast EndingCrLf)) (dropFirst b)))
                .Value
        else
            concat a b

    let replaceLines (line: int) (count: int) (replacement: Node) (root: Node) : Node =
        let before, rest = split line root

        let after =
            match rest with
            | Some rest -> snd (split count rest)
            | None -> None

        (concatOpt before (concatOpt (Some replacement) after)).Value

    let replace (from: int) (to': int) (insert: Node) (root: Node) : Node =
        let struct (line1, start1, leaf1, i1) = locate from 0 0 root
        let struct (line2, start2, leaf2, i2) = locate to' 0 0 root
        let content1 = leafLineText leaf1 i1

        let content2 =
            if line1 = line2 then
                content1
            else
                leafLineText leaf2 i2

        let column1 = from - start1
        let column2 = to' - start2

        // A line before `from` ending with a lone "\r" is rewritten too: the edit may complete its line break.
        let extends =
            column1 = 0
            && line1 > 0
            && (let struct (leaf, i) = getLeafAndLine (line1 - 1) root
                leaf.EndingKinds[i] = EndingCr)

        if
            not extends
            && line1 = line2
            && insert.LineCount = 1
            && column1 <= content1.Length
            && column2 <= content1.Length
        then
            let struct (inserted, _) = firstLine insert
            let line = String.Concat (content1.AsSpan (0, column1), inserted.AsSpan (), content1.AsSpan column2)
            updateSingleLine line1 line root
        else
            let firstRewritten, prefix =
                if extends then
                    let struct (leaf, i) = getLeafAndLine (line1 - 1) root
                    line1 - 1, leafLineText leaf i + "\r"
                elif column1 <= content1.Length then
                    line1, content1.Substring (0, column1)
                else
                    line1, content1 + "\r"

            let suffix, kindAfter =
                if column2 <= content2.Length then
                    content2.Substring column2, leaf2.EndingKinds[i2]
                else
                    "", EndingLf

            let replacement = join (join (nodeOfString prefix EndingNone) insert) (lineNode suffix kindAfter)
            replaceLines firstRewritten (line2 - firstRewritten + 1) replacement root

    let sliceString (from: int) (to': int) (root: Node) : string =
        if from >= to' then
            ""
        else
            let builder = StringBuilder (to' - from)

            let rec walk (start: int) (node: Node) =
                match node with
                | Leaf leaf ->
                    for i = 0 to leaf.LineCount - 1 do
                        let lineStart = start + leaf.LineOffsets[i]
                        let lineEnd = start + leaf.LineOffsets[i + 1]

                        if lineEnd > from && lineStart < to' then
                            let content = leafLineText leaf i
                            let first = max 0 (from - lineStart)
                            let last = min (lineEnd - lineStart) (to' - lineStart)

                            if first < content.Length then
                                builder.Append (content, first, min last content.Length - first)
                                |> ignore

                            if last > content.Length then
                                let ending = endingString leaf.EndingKinds[i]
                                let endingFirst = max 0 (first - content.Length)

                                builder.Append (ending, endingFirst, last - content.Length - endingFirst)
                                |> ignore
                | Branch b ->
                    let middle = start + b.Left.CharLength

                    if middle > from && start < to' then
                        walk start b.Left

                    if middle + b.Right.CharLength > from && middle < to' then
                        walk middle b.Right

            walk 0 root
            builder.ToString ()

    let slice (from: int) (to': int) (root: Node) : Node =
        if from >= to' then
            Leaf (makeEmptyLeaf EndingNone)
        elif from = 0 && to' = root.CharLength then
            root
        else
            let struct (line1, start1, leaf1, i1) = locate from 0 0 root
            let struct (line2, start2, leaf2, i2) = locate to' 0 0 root

            if line2 - line1 < 2 || to' - from <= 4096 then
                nodeOfString (sliceString from to' root) EndingNone
            else
                let content1 = leafLineText leaf1 i1
                let content2 = leafLineText leaf2 i2
                let column1 = from - start1
                let column2 = to' - start2

                let head =
                    if column1 <= content1.Length then
                        lineNode (content1.Substring column1) leaf1.EndingKinds[i1]
                    else
                        lineNode "" EndingLf

                let tail =
                    if column2 <= content2.Length then
                        lineNode (content2.Substring (0, column2)) EndingNone
                    else
                        nodeOfString (content2 + "\r") EndingNone

                let middle =
                    match snd (split (line1 + 1) root) with
                    | Some rest -> fst (split (line2 - line1 - 1) rest)
                    | None -> None

                (concatOpt (Some head) (concatOpt middle (Some tail))).Value

    let getText (root: Node) : string =
        if root.CharLength = 0 then
            ""
        else
            let builder = StringBuilder root.CharLength
            let buffer = ArrayPool<char>.Shared.Rent 4096

            let rec walk (node: Node) =
                match node with
                | Leaf leaf ->
                    for i = 0 to leaf.LineCount - 1 do
                        let start = leaf.ByteOffsets[i]
                        let length = leaf.ByteOffsets[i + 1] - start

                        if length > 0 then
                            if length <= buffer.Length then
                                let chars = Encoding.UTF8.GetChars (leaf.Utf8Bytes, start, length, buffer, 0)
                                builder.Append (buffer, 0, chars) |> ignore
                            else
                                builder.Append (Encoding.UTF8.GetString (leaf.Utf8Bytes, start, length))
                                |> ignore

                        builder.Append (endingString leaf.EndingKinds[i]) |> ignore
                | Branch b ->
                    walk b.Left
                    walk b.Right

            try
                walk root
                builder.ToString ()
            finally
                ArrayPool<char>.Shared.Return buffer

    let contentEquals (a: Node) (b: Node) : bool =
        if obj.ReferenceEquals (a, b) then
            true
        elif a.CharLength <> b.CharLength || a.LineCount <> b.LineCount then
            false
        else
            let mutable equal = true
            let mutable line = 0

            while equal && line < a.LineCount do
                let struct (leafA, i) = getLeafAndLine line a
                let struct (leafB, j) = getLeafAndLine line b

                let spanA =
                    ReadOnlySpan<byte> (leafA.Utf8Bytes, leafA.ByteOffsets[i], leafA.ByteOffsets[i + 1] - leafA.ByteOffsets[i])

                let spanB =
                    ReadOnlySpan<byte> (leafB.Utf8Bytes, leafB.ByteOffsets[j], leafB.ByteOffsets[j + 1] - leafB.ByteOffsets[j])

                equal <-
                    leafA.EndingKinds[i] = leafB.EndingKinds[j]
                    && spanA.SequenceEqual spanB

                line <- line + 1

            equal

/// A line of a text.
[<Struct>]
type Line =
    {
        /// The line's 0-based number.
        Number: int
        /// The offset of the line's first character.
        From: int
        /// The offset after the line's last character, before its line break.
        To: int
        /// The line's characters, without its line break.
        Text: string
    }

/// An immutable text. An edit shares every leaf it leaves alone with the text it was made from.
/// Offsets are UTF-16 code units and count each line break by its characters. Equality is by reference;
/// <c>Text.contentEquals</c> compares characters.
[<Sealed>]
type Text internal (root: Node) =
    member internal _.Root = root
    /// The number of UTF-16 code units, line breaks included.
    member _.Length = root.CharLength
    /// The number of lines; a text without characters has one.
    member _.Lines = root.LineCount
    override _.ToString() = Rope.getText root

[<RequireQualifiedAccess>]
module Text =

    let empty = Text (Leaf (Rope.makeEmptyLeaf Rope.EndingNone))

    // One-character texts are shared: typing produces one per key.
    let private single: Text[] = Array.zeroCreate 128

    let private ofRoot (root: Node) : Text =
        if root.CharLength = 0 then empty else Text root

    let ofString (text: string) : Text =
        if String.IsNullOrEmpty text then
            empty
        elif
            text.Length = 1
            && text[0] < '\128'
            && text[0] <> '\n'
            && text[0] <> '\r'
        then
            let index = int text[0]
            let held = single[index]

            if isNull (box held) then
                let made = Text (Rope.nodeOfString text Rope.EndingNone)
                single[index] <- made
                made
            else
                held
        else
            Text (Rope.nodeOfString text Rope.EndingNone)

    /// The text of <c>lines</c> joined with "\n".
    let ofLines (lines: seq<string>) : Text =
        let leaves = Rope.parseLinesToLeaves lines Rope.ChunkLines
        ofRoot (Rope.buildFromLeaves leaves 0 (leaves.Length - 1))

    /// The text of UTF-8 <c>bytes</c>, without a byte order mark.
    let ofUtf8 (bytes: ReadOnlySpan<byte>) : Text =
        let leaves = Rope.parseUtf8ToLeaves true bytes Rope.ChunkLines
        ofRoot (Rope.buildFromLeaves leaves 0 (leaves.Length - 1))

    /// <c>ofUtf8</c> with the line breaks found one byte at a time.
    let ofUtf8Scalar (bytes: ReadOnlySpan<byte>) : Text =
        let leaves = Rope.parseUtf8ToLeaves false bytes Rope.ChunkLines
        ofRoot (Rope.buildFromLeaves leaves 0 (leaves.Length - 1))

    let length (text: Text) : int = text.Length
    let lines (text: Text) : int = text.Lines
    let toString (text: Text) : string = Rope.getText text.Root

    let private checkOffset (name: string) (offset: int) (text: Text) =
        if offset < 0 || offset > text.Length then
            raise (ArgumentOutOfRangeException (name, String.Concat ("Offset ", string offset, " is outside [0, ", string text.Length, "].")))

    let private checkLine (number: int) (text: Text) =
        if number < 0 || number >= text.Lines then
            raise (ArgumentOutOfRangeException ("number", String.Concat ("Line ", string number, " is outside [0, ", string text.Lines, ").")))

    /// The line with 0-based number <c>number</c>.
    let line (number: int) (text: Text) : Line =
        checkLine number text
        let struct (start, leaf, index) = Rope.locateLine number 0 text.Root
        let content = Rope.leafLineText leaf index

        {
            Number = number
            From = start
            To = start + content.Length
            Text = content
        }

    /// The line holding <c>offset</c>. An offset inside a line break belongs to the line the break ends.
    let lineAt (offset: int) (text: Text) : Line =
        checkOffset "offset" offset text
        let struct (number, start, leaf, index) = Rope.locate offset 0 0 text.Root
        let content = Rope.leafLineText leaf index

        {
            Number = number
            From = start
            To = start + content.Length
            Text = content
        }

    /// The characters of line <c>number</c>. A line an edit left alone returns the string instance it returned before
    /// the edit, while that instance is alive.
    let lineText (number: int) (text: Text) : string =
        checkLine number text
        let struct (leaf, index) = Rope.getLeafAndLine number text.Root
        Rope.leafLineText leaf index

    /// The characters of [<c>from</c>, <c>to'</c>).
    let sliceString (from: int) (to': int) (text: Text) : string =
        checkOffset "from" from text
        checkOffset "to'" to' text
        Rope.sliceString from to' text.Root

    /// The text of [<c>from</c>, <c>to'</c>). A slice of more than a few lines shares the leaves of whole lines.
    let slice (from: int) (to': int) (text: Text) : Text =
        checkOffset "from" from text
        checkOffset "to'" to' text

        if from >= to' then
            empty
        elif from = 0 && to' = text.Length then
            text
        elif to' - from = 1 then
            ofString (Rope.sliceString from to' text.Root)
        else
            ofRoot (Rope.slice from to' text.Root)

    /// <c>text</c> with [<c>from</c>, <c>to'</c>) replaced by <c>insert</c>.
    let replace (from: int) (to': int) (insert: Text) (text: Text) : Text =
        checkOffset "from" from text
        checkOffset "to'" to' text

        if from > to' then
            raise (ArgumentException ("The range ends before it starts.", "to'"))

        if from = to' && insert.Length = 0 then
            text
        else
            ofRoot (Rope.replace from to' insert.Root text.Root)

    /// <c>a</c> followed by <c>b</c>.
    let append (a: Text) (b: Text) : Text =
        if a.Length = 0 then b
        elif b.Length = 0 then a
        else Text (Rope.join a.Root b.Root)

    /// The characters of lines [<c>fromLine</c>, <c>toLine</c>), without line breaks.
    let iterLines (fromLine: int) (toLine: int) (text: Text) : seq<string> =
        seq {
            for number in max 0 fromLine .. min toLine text.Lines - 1 do
                yield lineText number text
        }

    /// The characters of [<c>from</c>, <c>to'</c>) in order, one string per line, line breaks included.
    let iterRange (from: int) (to': int) (text: Text) : seq<string> =
        checkOffset "from" from text
        checkOffset "to'" to' text

        seq {
            let mutable at = from

            while at < to' do
                let struct (_, start, leaf, index) = Rope.locate at 0 0 text.Root
                let next = min to' (start + leaf.LineOffsets[index + 1] - leaf.LineOffsets[index])
                yield Rope.sliceString at next text.Root
                at <- next
        }

    /// The offset of <c>character</c> in line <c>line</c>. A character beyond the line gives the line's end.
    let offsetOf (line: int) (character: int) (text: Text) : int =
        checkLine line text
        let struct (start, leaf, index) = Rope.locateLine line 0 text.Root
        start + max 0 (min character (Rope.leafLineCharLen leaf index))

    /// The line and character of <c>offset</c>. An offset inside a line break gives the end of the line's characters.
    let positionOf (offset: int) (text: Text) : struct (int * int) =
        checkOffset "offset" offset text
        let struct (number, start, leaf, index) = Rope.locate offset 0 0 text.Root
        struct (number, min (offset - start) (Rope.leafLineCharLen leaf index))

    /// True when <c>a</c> and <c>b</c> hold the same characters.
    let contentEquals (a: Text) (b: Text) : bool = Rope.contentEquals a.Root b.Root
