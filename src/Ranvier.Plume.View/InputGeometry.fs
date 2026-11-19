namespace Ranvier.Plume.View

module internal InputGeometry =
    let rawOffset (raw: string) offset =
        let mutable i = 0
        let mutable n = 0
        while i < raw.Length && n < offset do
            if raw[i] = '\r' && i + 1 < raw.Length && raw[i+1] = '\n' then i <- i + 1
            i <- i + 1
            n <- n + 1
        i

    let nativeOffset (raw: string) limit =
        let mutable n = min limit raw.Length
        for i in 1 .. n - 1 do
            if raw[i-1] = '\r' && raw[i] = '\n' then n <- n - 1
        n

    /// Keep the edit at the selected occurrence when the surrounding text repeats.
    let replacement (before: string) (after: string) from until =
        let mutable left = 0
        let mutable oldEnd = before.Length
        let mutable newEnd = after.Length
        while left < from && left < after.Length && before[left] = after[left] do
            left <- left + 1
        while oldEnd > until && newEnd > left && before[oldEnd-1] = after[newEnd-1] do
            oldEnd <- oldEnd - 1
            newEnd <- newEnd - 1
        struct (left, oldEnd, newEnd)

    let untouched left right editFrom editTo = editTo <= left || right <= editFrom

    let segment left right lineFrom lineTo =
        struct (max left lineFrom, min right lineTo)
