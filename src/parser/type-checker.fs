namespace aleph.parser

open aleph.parser.ast
open aleph.parser.ast.typed

module TypeChecker =

    let (==>) (input: Result<'a,'b>) ok  =
        Result.bind ok input

    let any_expression e = e |> Ok

    // For a list of typed Expressions (E), returns two list: one with just the expression
    // elements and a second with just the Types.
    // All must be Classic expressions.
    let unzip_classic values =
        let rec next items =
            match items with
            | head :: tail ->
                match head with
                | Classic (e,t) ->
                    next tail
                    ==> fun (expressions, types) ->
                        (e :: expressions, t::types) |> Ok
                | _ ->
                    $"Invalid element: {head}. Expecting a classic type." |> Error
            | [] -> ([], []) |> Ok
        next values

    // Gets a typed expression (E), and returns
    // its quantum equivalent. If the expression is already quantum returns
    // itself, otherwise return the corresponding elements for a quantum Literal.
    let make_q e =
        match e with 
        | Quantum (l, t) -> (l, t) |> Ok
        | Classic (l, Type.Set (Type.Tuple t)) -> (Literal l, QType.Ket t) |> Ok
        | Classic (l, Type.Set t) -> (Literal l, QType.Ket [t]) |> Ok
        | Classic (l, Type.Int) -> (Literal (C.Set [l]), QType.Ket [Type.Int]) |> Ok
        | Classic (l, Type.Bool) -> (Literal (C.Set [l]), QType.Ket [Type.Bool]) |> Ok
        | Classic (l, Type.Tuple t) -> (Literal (C.Set [l]), QType.Ket t) |> Ok
        | _ -> "Cannot create quantum literal from {e}" |> Error 

    type TypeContext = Map<Id, AnyType>

    let QInt = QType.Ket [Type.Int]
    let QBool = QType.Ket [Type.Bool]

    let add_to_context (ctx:TypeContext) (arguments: (Id * AnyType) list) : TypeContext =
        arguments 
        |> List.fold (fun state arg -> state.Add arg) ctx

    let rec typecheck (e: Expression, ctx: TypeContext) =
        match e with 
        | Expression.Var id -> typecheck_var (id, ctx)
        | Expression.Bool b -> typecheck_bool (b, ctx)
        | Expression.Int i  -> typecheck_int (i, ctx)
        | Expression.Tuple values -> typecheck_tuple (values, ctx)
        | Expression.Set values -> typecheck_set (values, ctx)

        | Expression.Not value -> typecheck_not (value, ctx)
        | Expression.Or (left, right) -> (C.Or, Q.Or) |> typecheck_bool_expression (left, right, ctx)
        | Expression.And (left, right) -> (C.And, Q.And) |> typecheck_bool_expression (left, right, ctx) 

        | Expression.Add (left, right) -> typecheck_add (left, right, ctx)
        | Expression.Multiply (left, right) -> typecheck_multiply (left, right, ctx)

        | Expression.Range (start, stop) -> typecheck_range (start, stop, ctx)

        | Expression.Method (arguments, body) -> typecheck_method (arguments, body, ctx)

        | Expression.Ket values -> typecheck_ket (values,ctx)
        | Expression.KetAll size -> typecheck_ketall (size, ctx)

        | Expression.CallMethod (method, args) -> typecheck_callmethod (method, args, ctx)

        | Expression.Equals (left, right) -> typecheck_equals (left, right, ctx)
        | Expression.LessThan (left, right) -> typecheck_lessthan (left, right, ctx)

        | Expression.Join (left, right) -> typecheck_join (left, right, ctx)

        | Expression.Project (value, index) -> typecheck_project (value, index, ctx)
        | Expression.Block (stmts, r) -> typecheck_block (stmts, r, ctx)
        | Expression.If (c, t, f) -> typecheck_if (c, t, f, ctx)
        | Expression.Summarize  (id, enumeration, aggregation, body) -> typecheck_summarize (id, enumeration, aggregation, body, ctx)

        | Expression.Solve (ket, cond) -> typecheck_solve (ket, cond, ctx)        
        | Expression.Prepare ket -> typecheck_prepare (ket, ctx)
        | Expression.Sample universe -> typecheck_sample (universe, ctx)        

    // Typechecks an untyped expression list. Receives a callback such that
    // each expression can be validated. If all the expressions are valid
    // it returns the list of corresponding typed expressions.
    and typecheck_expression_list is_valid_expression (values, ctx) =
        let rec next (items, ctx) =
            match items with
            | head :: tail ->
                typecheck (head, ctx)
                ==> fun (head, ctx) ->
                    is_valid_expression head
                    ==> fun head ->
                        next (tail, ctx)
                        ==> fun (tail, ctx) ->
                            (head :: tail, ctx) |> Ok
            | [] -> ([], ctx) |> Ok
        next (values, ctx)

    and typecheck_var (id, ctx) =
        match ctx.TryFind id with
        | Some t -> 
            match t with
            | Type t -> (Classic (C.Var id, t), ctx) |> Ok
            | QType t -> (Quantum (Q.Var id, t), ctx) |> Ok
            | UType t -> (Universe (U.Var id, t), ctx) |> Ok
        | None -> $"Unknown variable: {id}" |> Error

    and typecheck_bool (b, ctx) =
        (Classic (BoolLiteral b, Type.Bool), ctx) |> Ok

    and typecheck_int (i, ctx) =
        (Classic (IntLiteral i, Type.Int), ctx) |> Ok

    and typecheck_tuple (values, ctx) =
        let is_bool_or_int msg e = 
            match e with 
            | Classic (_, Type.Bool)
            | Classic (_, Type.Int) -> e |> Ok
            | Classic (e, t) -> msg + $"Expected bool or int expression, got: ({e}:{t})" |> Error
            | Quantum (e, t) -> msg + $"Expected bool or int expression, got: ({e}:{t})" |> Error
            | Universe (e, t) -> msg + $"Expected bool or int expression, got: ({e}:{t})" |> Error

        typecheck_expression_list (is_bool_or_int  "Invalid tuple element. ") (values, ctx)
        ==> fun (values, ctx) ->
            unzip_classic values
            ==> fun (exps, types) ->
                (Classic (C.Tuple exps, Type.Tuple types), ctx) |> Ok

    and typecheck_set (values, ctx) =
        let is_set_element msg e =
            match e with 
            | Classic (_, Type.Int _)
            | Classic (_, Type.Bool _)
            | Classic (_, Type.Tuple _) -> e |> Ok
            | Classic (e, t) -> msg + $"Expected int, bool or tuple expression, got: ({e}:{t})" |> Error
            | Quantum (e, t) -> msg + $"Expected int, bool or tuple expression, got: ({e}:{t})" |> Error
            | Universe (e, t) -> msg + $"Expected int, bool or tuple expression, got: ({e}:{t})" |> Error

        match values with
        | [] -> (Classic (C.Set [], Type.Set (Type.Tuple [])), ctx) |> Ok
        | _ ->
            typecheck_expression_list (is_set_element "Invalid set element. ") (values, ctx)
            ==> fun (values, ctx) ->
                unzip_classic values
                ==> fun (exps, types) ->
                    let head = types.Head
                    let allEqual = types.Tail |> List.forall (fun t -> t = head)
                    if allEqual then
                        (Classic (C.Set exps, Type.Set head), ctx) |> Ok
                    else
                        "All elements in a set must be of the same type." |> Error

    and typecheck_bool_expression (left: Expression, right: Expression, ctx) (classic: C * C -> C, quantum: Q * Q -> Q) =
        typecheck(left, ctx)
        ==> fun (left, ctx) ->
            typecheck (right, ctx)
            ==> fun (right, ctx) ->
                match (left, right) with
                | Classic _, Classic _ -> typecheck_bool_classic(left, right, classic, ctx)
                | _ -> typecheck_bool_quantum (left, right, quantum, ctx)

    and typecheck_bool_classic (left, right, op, ctx) =
        match (left, right) with
        | Classic (l, Type.Bool), Classic (r, Type.Bool) -> (Classic (op (l, r), Type.Bool), ctx) |> Ok
        | Classic (_, lt), Classic (_, rt) ->  $"Boolean expressions require boolean arguments, got {lt} == {rt}" |> Error
        | _ -> 
            assert false // we should never be here.
            $"Expecting only classical expressions" |> Error

    and typecheck_bool_quantum (left, right, op, ctx) =
        make_q left
        ==> fun (l, lt) ->
            make_q right
            ==> fun (r, rt) ->
                match (lt, rt) with
                | QType.Ket [Type.Bool], QType.Ket[Type.Bool] -> (Quantum (op (l, r), QType.Ket [Type.Bool]), ctx) |> Ok
                | QType.Ket lt, QType.Ket rt ->  $"Boolean expressions require boolean arguments, got {lt} == {rt}" |> Error
                

    and typecheck_not (value, ctx) =
        typecheck(value, ctx)
        ==> fun (value, ctx) ->
            match value with 
            | Classic (v, Type.Bool) -> (Classic (C.Not v, Type.Bool), ctx) |> Ok
            | Quantum (v, QType.Ket [Type.Bool]) -> (Quantum (Q.Not v, QType.Ket [Type.Bool]), ctx) |> Ok
            | Classic (_, t) -> $"Not must be applied to a boolean expression, got: {t}" |> Error
            | Quantum (_, t) -> $"Not must be applied to a boolean expression, got: {t}" |> Error
            | Universe (_, t) -> $"Not must be applied to a boolean expression, got: {t}" |> Error

    and typecheck_range (start, stop, ctx) =
        typecheck(start, ctx)
        ==> fun (start, ctx) ->
            match start with 
            | Classic (start, Type.Int) ->
                typecheck (stop, ctx)
                ==> fun (stop, ctx) ->
                match stop with 
                | Classic (stop, Type.Int) ->
                    (Classic (C.Range (start, stop), Type.Set Type.Int), ctx) |> Ok
                | _ ->
                    $"Stop must be an int expression, got: {stop}" |> Error
            | _ ->
                $"Start must be an int expression, got: {start}" |> Error

    and typecheck_method (arguments, body, ctx) =
        let ctx =
            arguments 
            |> add_to_context ctx
        typecheck (body, ctx)
        ==> fun (body, ctx) ->
            let argNames = arguments |> List.map (fun a -> (fst a))
            let argTypes = arguments |> List.map (fun a -> (snd a))
            match body with
            | Classic (_, t) ->
                (Classic (C.Method (argNames, body), Type.Method (argTypes, AnyType.Type t)), ctx) |> Ok
            | Quantum (_, qt) ->
                (Classic (C.Method (argNames, body), Type.Method (argTypes, AnyType.QType qt)), ctx) |> Ok
            | Universe (_, t) ->
                (Classic (C.Method (argNames, body), Type.Method (argTypes, AnyType.UType t)), ctx) |> Ok

    and typecheck_add (left, right, ctx) =
        typecheck(left, ctx)
        ==> fun (left, ctx) ->
            typecheck (right, ctx)
            ==> fun (right, ctx) ->
                match (left, right) with
                | Classic _, Classic _ -> typecheck_add_classic (left, right, ctx)
                | _ -> typecheck_add_quantum (left, right, ctx)

    and typecheck_add_classic (left, right, ctx) =
        unzip_classic [left; right]
        ==> fun (values, types) ->
            match (types) with
            | [Type.Int;  Type.Int] -> (Classic (C.Add (values.[0], values.[1]), Type.Int), ctx) |> Ok
            | _ -> $"Add can only be applied to int expressions" |> Error

    and typecheck_add_quantum (left, right, ctx) =
        make_q left
        ==> fun (l, lt) ->
            make_q right
            ==> fun (r, rt) ->
                match (lt, rt) with
                | QType.Ket [Type.Int],  QType.Ket[Type.Int] -> (Quantum (Q.Add (l, r), QType.Ket [Type.Int]), ctx) |> Ok
                | _ -> $"Quantum addition can only be applied to int Kets, got {lt} + {rt}" |> Error

    and typecheck_multiply (left, right, ctx) =
        typecheck(left, ctx)
        ==> fun (left, ctx) ->
            typecheck (right, ctx)
            ==> fun (right, ctx) ->
                match (left, right) with
                | Classic _, Classic _ -> typecheck_multiply_classic (left, right, ctx)
                | _ -> typecheck_multiply_quantum (left, right, ctx)

    and typecheck_multiply_classic (left, right, ctx) =
        unzip_classic [left; right]
        ==> fun (values, types) ->
            match (types) with
            | [Type.Int;  Type.Int] -> (Classic (C.Multiply (values.[0], values.[1]), Type.Int), ctx) |> Ok
            | _ -> $"Multiply can only be applied to int expressions" |> Error

    and typecheck_multiply_quantum (left, right, ctx) =
        make_q left
        ==> fun (l, lt) ->
            make_q right
            ==> fun (r, rt) ->
                match (lt, rt) with
                | QType.Ket [Type.Int],  QType.Ket[Type.Int] -> (Quantum (Q.Multiply (l, r), QType.Ket [Type.Int]), ctx) |> Ok
                | _ -> $"Quantum multiplication can only be applied to int Kets" |> Error


    and typecheck_equals (left, right, ctx) =
        typecheck(left, ctx)
        ==> fun (left, ctx) ->
            typecheck (right, ctx)
            ==> fun (right, ctx) ->
                match (left, right) with
                | Classic _, Classic _ -> typecheck_equals_classic (left, right, ctx)
                | _ -> typecheck_equals_quantum (left, right, ctx)

    and typecheck_equals_classic (left, right, ctx) =
        unzip_classic [left; right]
        ==> fun (values, types) ->
            match (types) with
            | [Type.Int;  Type.Int] -> (Classic (C.Equals (values.[0], values.[1]), Type.Bool), ctx) |> Ok
            | _ -> $"== can only be applied to int expressions" |> Error

    and typecheck_equals_quantum (left, right, ctx) =
        make_q left
        ==> fun (l, lt) ->
            make_q right
            ==> fun (r, rt) ->
                match (lt, rt) with
                | QType.Ket [Type.Int],  QType.Ket[Type.Int] -> (Quantum (Q.Equals (l, r), QType.Ket [Type.Bool]), ctx) |> Ok
                | _ -> $"Quantum == can only be applied to int Kets" |> Error

    and typecheck_lessthan (left, right, ctx) =
        // TODO: quantum < ?
        typecheck(left, ctx)
        ==> fun (left, ctx) ->
            typecheck (right, ctx)
            ==> fun (right, ctx) ->
                match (left, right) with
                | Classic (left, Type.Int), Classic (right, Type.Int) ->
                    (Classic (C.LessThan (left, right), Type.Bool), ctx) |> Ok
                | Classic (_, lt), Classic (_, rt) ->
                     $"Both expressions for < must be int. Got {lt} < {rt}" |> Error
                | Quantum (_, lt), Classic (_, rt) ->
                     $"Both expressions for < must be int. Got {lt} < {rt}" |> Error
                | Quantum (_, lt), Quantum (_, rt) ->
                     $"Both expressions for < must be int. Got {lt} < {rt}" |> Error
                | Classic (_, lt), Quantum (_, rt) ->
                     $"Both expressions for < must be int. Got {lt} < {rt}" |> Error
                | _ ->
                     $"Both expressions for < must be int. Got {left} < {right}" |> Error

    and typecheck_join (left, right, ctx) =
        typecheck(left, ctx)
        ==> fun (left, ctx) ->
            typecheck (right, ctx)
            ==> fun (right, ctx) ->
                match (left, right) with
                | Classic (left, Type.Tuple lt), Classic (right, Type.Tuple rt) ->
                    (Classic (C.Join (left, right), Type.Tuple (lt @ rt)), ctx) |> Ok
                | Quantum (left, QType.Ket lt), Quantum (right, QType.Ket rt) ->
                    (Quantum (Q.Join (left, right), QType.Ket (lt @ rt)), ctx) |> Ok
                | _ -> $"Join is only supported on tuples and kets, got: {left} , {right}" |> Error

    and typecheck_callmethod (method, args, ctx) =
        let any_type e = e |> Ok

        // typecheck the method name:
        typecheck(method, ctx)
        ==> fun (method, ctx) ->
            // If the expression is associated with a method:
            match method with
            | Classic (m, Type.Method (argTypes, rType)) ->
                // typecheck the list of arguments to pass:
                typecheck_expression_list any_type (args, ctx)
                ==> fun (args, ctx) ->
                    // verify that the types of the arguments we'll be passing 
                    // match the expected type of the arguments
                    let argTypes' = args |> List.map (function | Classic (_, t) -> AnyType.Type t | Quantum (_, t) -> AnyType.QType t | Universe (_, t) -> AnyType.UType t)
                    if argTypes = argTypes' then
                        // Everything matches. Check the return type
                        // to decide if it's a classical or a quantum method:
                        match rType with
                        | AnyType.Type t -> 
                            (Classic (C.CallMethod (m, args), t), ctx) |> Ok
                        | AnyType.QType qt -> 
                            (Quantum (Q.CallMethod (m, args), qt), ctx) |> Ok
                        | AnyType.UType ut -> 
                            (Universe (U.CallMethod (m, args), ut), ctx) |> Ok
                    else
                        $"Arguments type missmatch. Expected {argTypes} got {argTypes'}" |> Error
            | _ -> $"Invalid expression: {method}" |> Error

    and typecheck_ket (value, ctx) =
        typecheck (Expression.Set value, ctx)
        ==> fun (value, ctx) ->
            make_q value
            ==> fun value -> (Quantum value, ctx) |> Ok

    and typecheck_ketall (size, ctx) =
        typecheck (size, ctx)
        ==> fun (size, ctx) ->
            match size with 
            | Classic (v, Type.Int) ->
                (Quantum (Q.KetAll v, QType.Ket [Type.Int]), ctx) |> Ok
            | _ ->
                $"Ket size must be an int expression, got: {size}" |> Error

    and typecheck_project (value, index, ctx) =
        // we received an int literal as index,
        // thus we return a:
        // [C|Q].Project expression
        // Notice that index is modular...
        let use_project (value, idx, ctx) =
            match value with
            | Classic (left, Type.Tuple types) ->
                let i = idx % types.Length
                (Classic (C.Project (left, i), types.[i]), ctx) |> Ok
            | Quantum (left, QType.Ket types) ->
                let i = idx % types.Length
                (Quantum (Q.Project (left, i), QType.Ket [types.[i]]), ctx) |> Ok
            | _ -> $"Project is only supported on tuples and kets" |> Error
        // we received a list of non-literal ints as indices
        // this checks that the types in a Tuple or Ket are all the same and if so it returns
        // [C|Q].Index expression
        let use_index (value, (index: C), ctx) =
            let all_the_same (types: Type list) = 
                let head = types.Head
                types.Tail |> List.forall (fun t -> t = head)
            match value with
            | Classic (left, Type.Tuple types) ->
                if (all_the_same types) then
                    (Classic (C.Index (left, index), types.Head), ctx) |> Ok
                else
                    "Indexing of tuples is only available on tuples of a single type" |> Error
            | Quantum (left, QType.Ket types) ->
                if (all_the_same types) then
                    (Quantum (Q.Index (left, index), QType.Ket [types.Head]), ctx) |> Ok
                else
                    "Indexing of kets is only available on kets of a single type" |> Error
            | _ -> $"Project is only supported on tuples and kets" |> Error

        typecheck(value, ctx)
        ==> fun (value, ctx) ->
            typecheck (index, ctx)
            ==> fun (index, ctx) ->
                match index with 
                | Classic ((C.IntLiteral i), Type.Int) -> use_project (value, i, ctx)
                | Classic (i, Type.Int) -> use_index (value, i, ctx)
                | _ -> $"Invalid projection index. Expected int expression, got: {index}" |> Error
        
    and typecheck_block (stmts, r, ctx') =
        let as_typed_stmt previous (next:ast.Statement) =
            previous 
            ==> fun (previous, ctx) ->
                match next with
                | ast.Statement.Let (id, value) ->
                    typecheck (value, ctx)
                    ==> fun (value, ctx) ->
                        let t = value |> function | Classic (_, t) -> AnyType.Type t | Quantum (_, t) -> AnyType.QType t | Universe (_, t) -> AnyType.UType t
                        let ctx = ctx.Add (id, t)
                        (previous @ [typed.Statement.Let (id, value)], ctx) |> Ok
                | ast.Statement.Print (msg, value) ->
                    typecheck_expression_list any_expression (value, ctx)
                    ==> fun (value, ctx) ->
                        (previous @ [typed.Statement.Print (msg, value)], ctx) |> Ok
        stmts
        |> List.fold as_typed_stmt (([], ctx') |> Ok)
        ==> fun (stmts, ctx) ->
            typecheck (r, ctx)
            ==> fun (r, ctx) ->
                match r with
                | Classic (e, t) ->
                    (Classic (C.Block (stmts, e), t), ctx) |> Ok
                | Quantum (e, t) ->
                    (Quantum (Q.Block (stmts, e), t), ctx) |> Ok
                | Universe (e, t) ->
                    (Universe (U.Block (stmts, e), t), ctx) |> Ok

    and typecheck_if (c, t, f, ctx) =
        typecheck_expression_list any_expression ([c; t; f], ctx)
        ==> fun (exps, ctx) ->
            match exps with
            | [Classic _; Classic _; Classic _] -> typecheck_if_classic (exps.[0], exps[1], exps[2], ctx)
            | _ -> typecheck_if_quantum (exps.[0], exps[1], exps[2], ctx)
            
    and typecheck_if_classic (c, t, f, ctx) =
        match (c, t, f) with 
        | Classic (c, Type.Bool), Classic (t, tt), Classic (f, tf) when tt = tf ->
            (Classic (C.If (c, t, f), tt), ctx) |> Ok
        | Classic (c, Type.Bool), Classic (_, tt), Classic (_, ft) ->
            $"Both branches of if statement must be of the same type, got {tt} and {ft}" |> Error
        | Classic (c, t), Classic _, Classic _ -> 
            $"If condition must be a boolean, got {t}" |> Error
        | _ ->
            assert false // we should never be here.
            $"Expecting only classical expressions" |> Error

    // We get here if any of the expressions (condition, true, false) are quantum.
    // If that's the case, the return value will be quantum, so the first thing is to 
    // make the t and f quantum; we check that their types are the same.
    // Then, we check if the condition is classical or quantum, as the
    // language offers condition expressions for both: Q.IfClassic and Q.IfQuantum.
    and typecheck_if_quantum (c, t, f, ctx) =
        make_q t ==> fun (t, tt) -> make_q f ==> fun (f, ft) ->
            if tt = ft then     // Cheks the result types are the same.
                match c with
                | Classic (c, Type.Bool) ->
                    (Quantum (Q.IfClassic (c, t, f), tt), ctx) |> Ok
                | Quantum (c, QType.Ket [Type.Bool]) ->
                    (Quantum (Q.IfQuantum (c, t, f), tt), ctx) |> Ok
                | Classic (_, t) -> $"If condition must be a boolean, got {t}" |> Error
                | Quantum (_, t) -> $"If condition must be a boolean, got {t}" |> Error
                | Universe (_, t) -> $"If condition must be a boolean, got {t}" |> Error
            else 
                $"Both branches of if statement must be of the same type, got {tt} and {ft}" |> Error

    and typecheck_summarize (varId, enumeration, aggregation, body, ctx) =
        typecheck (enumeration, ctx)
        ==> fun (enumeration, ctx) ->
            // enumeration must be a set ...
            match enumeration with
            | Classic (values, (Type.Set t)) ->
                // and the variable to the context, and typecheck the body:
                let ctx = add_to_context ctx [varId, AnyType.Type t]
                typecheck (body, ctx)
                ==> fun (body, ctx) ->
                    // the body must match the aggregation:
                    match aggregation with
                    | Aggregation.And
                    | Aggregation.Or ->
                        match body with
                        | Classic (body, Type.Bool) ->
                            (Classic (C.Summarize (varId, values, aggregation, body), Type.Bool), ctx) |> Ok
                        | Quantum (body, QType.Ket [Type.Bool]) ->
                            (Quantum (Q.Summarize (varId, values, aggregation, body), QType.Ket [Type.Bool]), ctx) |> Ok
                        | Classic (_, t) -> $"Summarize body must be a boolean expression when aggregation is 'and' | 'or', got {t}" |> Error
                        | Quantum (_, t) -> $"Summarize body must be a boolean expression when aggregation is 'and' | 'or', got {t}" |> Error
                        | Universe (_, t) -> $"Summarize body must be a boolean expression when aggregation is 'and' | 'or', got {t}" |> Error
                    | Aggregation.Sum ->
                        match body with
                        | Classic (body, Type.Int) ->
                            (Classic (C.Summarize (varId, values, aggregation, body), Type.Int), ctx) |> Ok
                        | Quantum (body, QType.Ket [Type.Int]) ->
                            (Quantum (Q.Summarize (varId, values, aggregation, body), QType.Ket [Type.Int]), ctx) |> Ok
                        | Classic (_, t) -> $"Summarize body must be an Int expression when aggregation is 'sum', got {t}" |> Error
                        | Quantum (_, t) -> $"Summarize body must be an Int expression when aggregation is 'sum', got {t}" |> Error
                        | Universe (_, t) -> $"Summarize body must be an Int expression when aggregation is 'sum', got {t}" |> Error
            | Classic (_, t) -> $"Summarize expects a classic set of values, got: {t}" |> Error
            | Quantum (_, t) -> $"Summarize expects a classic set of values, got: {t}" |> Error
            | Universe (_, t) -> $"Summarize expects a classic set of values, got: {t}" |> Error

    and typecheck_solve (ket, cond, ctx) =
        typecheck (ket, ctx)
        ==> fun (ket, ctx) ->
            match ket with
            | Quantum (ket, QType.Ket t) ->
                typecheck (cond, ctx)
                ==> fun (cond, ctx) ->
                    match cond with
                    | Quantum (cond, QType.Ket [Type.Bool]) ->
                        (Quantum (Q.Solve (ket, cond), QType.Ket t), ctx) |> Ok
                    | Quantum (_, t) -> $"Solve condition must be a quantum boolean expression, got: {t}" |> Error
                    | Classic (_, t) -> $"Solve condition must be a quantum boolean expression, got: {t}" |> Error
                    | Universe (_, t) -> $"Solve condition must be a quantum boolean expression, got: {t}" |> Error
            | Classic (_, t) -> $"Solve argument must be a quantum ket, got: {t}" |> Error
            | Universe (_, t) -> $"Solve argument must be a quantum ket, got: {t}" |> Error

    and typecheck_prepare (ket, ctx) =
        typecheck (ket, ctx)
        ==> fun (ket, ctx) ->
            match ket with
            | Quantum (ket, QType.Ket t) -> (Universe (U.Prepare ket, UType.Universe t), ctx) |> Ok
            | Classic (_, t) -> $"Prepare argument must be a quantum ket, got: {t}" |> Error
            | Universe (_, t) -> $"Prepare argument must be a quantum ket, got: {t}" |> Error

    and typecheck_sample (universe, ctx) =
        typecheck (universe, ctx)
        ==> fun (universe, ctx) ->
            match universe with
            | Universe (u, UType.Universe t) -> (Classic (C.Sample u, Type.Tuple t), ctx) |> Ok
            | Quantum (_, t) -> $"Sample argument must be a quantum ket, got: {t}" |> Error
            | Classic (_, t) -> $"Sample argument must be a quantum ket, got: {t}" |> Error
