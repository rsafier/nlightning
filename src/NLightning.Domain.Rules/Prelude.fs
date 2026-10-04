namespace NLightning.Domain.Rules

/// Early return for rule chains: the first Error ends the computation.
[<AutoOpen>]
module internal Prelude =
    type ResultBuilder() =
        member _.Return x = Ok x
        member _.ReturnFrom(r: Result<_, _>) = r
        member _.Bind(r, f) = Result.bind f r

    let result = ResultBuilder()

    let inline unwrap (r: Result<'T, 'T>) =
        match r with
        | Ok v
        | Error v -> v
