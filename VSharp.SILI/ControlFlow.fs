﻿namespace VSharp

type StatementResultNode =
    | NoResult
    | Break
    | Continue
    | Return of Term
    | Throw of Term
    | Guarded of (Term * StatementResult) list

and
    [<CustomEquality;NoComparison>]
    StatementResult =
        {result : StatementResultNode; metadata : TermMetadata}
        override x.ToString() =
            x.result.ToString()
        override x.GetHashCode() =
            x.result.GetHashCode()
        override x.Equals(o : obj) =
            match o with
            | :? StatementResult as other -> x.result.Equals(other.result)
            | _ -> false

[<AutoOpen>]
module internal ControlFlowConstructors =
    let NoResult metadata = { result = NoResult; metadata = metadata }
    let Break metadata = { result = Break; metadata = metadata }
    let Continue metadata = { result = Continue; metadata = metadata }
    let Return metadata term = { result = Return term; metadata = metadata }
    let Throw metadata term = { result = Throw term; metadata = metadata }
    let Guarded metadata grs = { result = Guarded grs; metadata = metadata }

module internal ControlFlow =

    let rec internal merge2Results condition1 condition2 thenRes elseRes =
        let metadata = Metadata.combine thenRes.metadata elseRes.metadata in
        match thenRes.result, elseRes.result with
        | _, _ when thenRes = elseRes -> thenRes
        | Return thenVal, Return elseVal -> Return metadata (Merging.merge2Terms condition1 condition2 thenVal elseVal)
        | Throw thenVal, Throw elseVal -> Throw metadata (Merging.merge2Terms condition1 condition2 thenVal elseVal)
        | Guarded gvs1, Guarded gvs2 ->
            gvs1
                |> List.map (fun (g1, v1) -> mergeGuarded gvs2 condition1 condition2 ((&&&) g1) v1 fst snd)
                |> List.concat
                |> Guarded metadata
        | Guarded gvs1, _ -> mergeGuarded gvs1 condition1 condition2 id elseRes fst snd |> Merging.mergeSame |> Guarded metadata
        | _, Guarded gvs2 -> mergeGuarded gvs2 condition1 condition2 id thenRes snd fst |> Merging.mergeSame |> Guarded metadata
        | _, _ -> Guarded metadata [(condition1, thenRes); (condition2, elseRes)]

    and private mergeGuarded gvs cond1 cond2 guard other thenArg elseArg =
        let mergeOne (g, v) =
            let merged = merge2Results cond1 cond2 (thenArg (v, other)) (elseArg (v, other)) in
            match merged.result with
            | Guarded gvs -> gvs |> List.map (fun (g2, v2) -> (guard (g &&& g2), v2))
            | _ -> List.singleton (guard(g), merged)
        gvs |> List.map mergeOne |> List.concat |> List.filter (fst >> Terms.IsFalse >> not)

    let rec private createImplicitPathCondition (statement : Option<JetBrains.Decompiler.Ast.IStatement>) accTerm (term, statementResult) =
        match statementResult.result with
        | NoResult -> term ||| accTerm
        | Continue ->
            match statement with
            | Some(statement) -> if Transformations.isContinueConsumer statement then term ||| accTerm else accTerm
            | None -> accTerm
        | Guarded gvs ->
            List.fold (createImplicitPathCondition statement) accTerm gvs
        | _ -> accTerm

    let internal currentCalculationPathCondition (statement : Option<JetBrains.Decompiler.Ast.IStatement>) statementResult =
         createImplicitPathCondition statement False (True, statementResult)

    let rec internal consumeContinue result =
        match result.result with
        | Continue -> NoResult result.metadata
        | Guarded gvs -> gvs |> List.map (fun (g, v) -> (g, consumeContinue v)) |> Guarded result.metadata
        | _ -> result

    let rec internal consumeBreak result =
        match result.result with
        | Break -> NoResult result.metadata
        | Guarded gvs -> gvs |> List.map (fun (g, v) -> (g, consumeBreak v)) |> Guarded result.metadata
        | _ -> result

    let rec internal throwOrIgnore term =
        match term.term with
        | Error t -> Throw term.metadata t
        | GuardedValues(gs, vs) -> vs |> List.map throwOrIgnore |> List.zip gs |> Guarded term.metadata
        | _ -> NoResult term.metadata

    let rec internal throwOrReturn term =
        match term.term with
        | Error t -> Throw term.metadata t
        | GuardedValues(gs, vs) -> vs |> List.map throwOrReturn |> List.zip gs |> Guarded term.metadata
        | Nop -> NoResult term.metadata
        | _ -> Return term.metadata term

    let rec internal consumeErrorOrReturn consumer term =
        match term.term with
        | Error t -> consumer t
        | Nop -> NoResult term.metadata
        | Terms.GuardedValues(gs, vs) -> vs |> List.map (consumeErrorOrReturn consumer) |> List.zip gs |> Guarded term.metadata
        | _ -> Return term.metadata term

    let rec internal composeSequentially oldRes newRes oldState newState =
        let calculationDone result =
            match result.result with
            | NoResult -> false
            | _ -> true
        let rec composeFlat newRes oldRes =
            match oldRes.result with
            | NoResult -> newRes
            | _ -> oldRes
        match oldRes.result, newRes.result with
        | NoResult, _ ->
            newRes, newState
        | Break, _
        | Continue, _
        | Throw _, _
        | Return _, _ -> oldRes, oldState
        | Guarded gvs, _ ->
            let conservativeGuard = List.fold (fun acc (g, v) -> if calculationDone v then acc ||| g else acc) False gvs in
            let result =
                match newRes.result with
                | Guarded gvs' ->
                    let composeOne (g, v) =
                        List.map (fun (g', v') -> (g &&& g', composeFlat v' v)) gvs' in
                    gvs |> List.map composeOne |> List.concat |> List.filter (fst >> Terms.IsFalse >> not) |> Merging.mergeSame
                | _ ->
                    let gs, vs = List.unzip gvs in
                    List.zip gs (List.map (composeFlat newRes) vs)
            in
            let commonMetadata = Metadata.combine oldRes.metadata newRes.metadata in
            Guarded commonMetadata result, Merging.merge2States conservativeGuard !!conservativeGuard oldState newState

    let rec internal resultToTerm result =
        match result.result with
        | Return term -> { term = term.term; metadata = result.metadata }
        | Throw err -> Error err result.metadata
        | Guarded gvs -> Merging.guardedMap resultToTerm gvs
        | _ -> Nop

    let internal pickOutExceptions result =
        let gvs =
            match result.result with
            | Throw e -> [(True, result)]
            | Guarded gvs -> gvs
            | _ -> [(True, result)]
        in
        let pickThrown (g, result) =
            match result.result with
            | Throw e -> Some(g, e)
            | _ -> None
        in
        let thrown, normal = List.mappedPartition pickThrown gvs in
        match thrown with
        | [] -> None, normal
        | gvs ->
            let gs, vs = List.unzip gvs in
            let mergedGuard = disjunction result.metadata gs in
            let mergedValue = Merging.merge gvs in
            Some(mergedGuard, mergedValue), normal

    let internal mergeResults grs =
        Merging.guardedMap resultToTerm grs |> throwOrReturn

    let internal unguardResults gvs =
        let unguard gres =
            match gres with
            | g, {result = Guarded gvs} -> gvs  |> List.map (fun (g', v) -> g &&& g', v)
            | _ -> [gres]
        in
        gvs |> List.map unguard |> List.concat
