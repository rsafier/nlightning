namespace FsProbe

type Step = | A | B of int | C of name: string * flags: byte
type Rec = { X: uint64; Steps: Step list }

module Probe =
    let structural () =
        let a = { X = 1UL; Steps = [ A; B 2; C("n", 1uy) ] }
        let b = { X = 1UL; Steps = [ A; B 2; C("n", 1uy) ] }
        a = b && compare a b = 0 && (Set.ofList [ B 3; A; B 1 ] |> Set.count) = 3 && (Map.ofList [ (1, "x") ]).[1] = "x"
    let formatSimple (n: int) = sprintf "n=%d s=%s" n "x"
    let formatAny (r: Rec) = sprintf "%A" r
    let interp (n: int) = $"n={n}"
