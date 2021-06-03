namespace Giraffe.JsonTherapy

open System
open Giraffe
open Microsoft.AspNetCore.Http
open Newtonsoft.Json.Linq
open System.Collections.Generic
open System.IO
open Microsoft.Extensions.Primitives
open System.Threading.Tasks
open FSharp.Control.Tasks

[<AutoOpen>]
module Types =

    /// A type representing Javascript Object Notation
    type Json =
        | JNumber of float
        | JString of string
        | JBool of bool
        | JNull
        | JArray of Json list
        | JObject of Map<string, Json>

[<RequireQualifiedAccess>]
module Extensions =
    open Newtonsoft.Json

    let internal request (requestMap : HttpRequest -> Task<HttpHandler>) : HttpHandler =
        fun (next : HttpFunc) (ctx : HttpContext) ->
            task {
                let! createdHandler = requestMap ctx.Request
                return! createdHandler next ctx
            }

    let internal isOption (typeInfo: Type) = typeInfo.FullName.StartsWith("Microsoft.FSharp.Core.FSharpOption`1")

    /// Parses the input string as structured JSON
    let parse (input: string) =
        let settings = JsonSerializerSettings(DateParseHandling = DateParseHandling.None)
        let token = JsonConvert.DeserializeObject<JToken>(input, settings)
        let rec fromJToken (token: JToken) =
            match token.Type with
            | JTokenType.Float -> JNumber (token.Value<float>())
            | JTokenType.Integer -> JNumber (token.Value<float>())
            | JTokenType.Boolean -> JBool (token.Value<bool>())
            | JTokenType.String -> JString (token.Value<string>())
            | JTokenType.Guid -> JString (token.Value<System.Guid>().ToString())
            | JTokenType.Null -> JNull
            | JTokenType.Array ->
                token.Values<JToken>()
                |> Seq.map fromJToken
                |> List.ofSeq
                |> Json.JArray
            | JTokenType.Object ->
                token.Value<IDictionary<string, JToken>>()
                |> Seq.map (fun pair -> pair.Key, fromJToken pair.Value)
                |> List.ofSeq
                |> Map.ofList
                |> Json.JObject
            | _ -> failwithf "JSON token type '%s' was not recognised" (token.Type.ToString())

        fromJToken token

    let rec convertJToken = function
        | JNull -> JValue.CreateNull() :> JToken
        | JBool value -> JToken.op_Implicit(value)
        | JString value -> JToken.op_Implicit(value)
        | JNumber value -> JToken.op_Implicit(value)
        | JArray values ->
            let output = Newtonsoft.Json.Linq.JArray()
            for value in values do
                output.Add(convertJToken value)
            output :> JToken
        | JObject dict ->
            let output = Newtonsoft.Json.Linq.JObject()
            for (key, value) in Map.toSeq dict do
                output.Add(JProperty(key, convertJToken value))
            output :> JToken

    /// Tries to parse the input string as structured data
    let tryParse input =
        try Ok (parse input)
        with | ex -> Error ex.Message

    let getJPath (inputPath: string) = List.ofArray (inputPath.Split('.'))

    let rec readPath (keys: string list) (input: Json) =
        match keys, input with
        | [ ], _ -> None
        | [ key ], JObject dict -> Map.tryFind key dict
        | firstKey :: rest, JObject dict ->
            match Map.tryFind firstKey dict with
            | Some (JObject nextDict) -> readPath rest (JObject nextDict)
            | _ -> None
        | _ -> None

    let parseInt (input: string) : Option<int> =
        match Int32.TryParse(input) with
        | true, value -> Some value
        | _, _ -> None

    let parseFloat (input: string) : Option<double> =
        match Double.TryParse input with
        | true, value -> Some value
        | _ -> None

    let parseGuid (input: string) : Option<Guid> =
        match Guid.TryParse input with
        | true, value -> Some value
        | _ -> None

    let extractValue (inputPath: string) (inputJson: Json) (typeInfo: Type) : Result<obj, string> =
        match typeInfo.FullName, readPath (getJPath inputPath) inputJson with
        | "System.Boolean", Some (JBool value) -> Ok (box value)
        | "System.Boolean", Some (JNumber 1.0) -> Ok (box true)
        | "System.Boolean", Some (JNumber 0.0) -> Ok (box false)
        | "System.Boolean", Some (JString ("true"|"True")) -> Ok (box true)
        | "System.Boolean", Some (JString ("false"|"False")) -> Ok (box false)
        | "System.Int32", Some (JNumber value) -> Ok (box (int (Math.Floor value)))
        | "System.Int32", Some (JString value) ->
            match parseInt value with
            | Some number -> Ok (box number)
            | None -> Error (sprintf "Could not parse value at path '%s' as an integer" inputPath)
        | "System.Double", Some (JNumber value) -> Ok (box value)
        | "System.Double", Some (JString value) ->
            match parseFloat value with
            | Some number -> Ok (box number)
            | None -> Error (sprintf "Could not parse value at path '%s' as a number" inputPath)
        | "System.String", Some (JString value) -> Ok (box value)
        | "System.String", Some (JNumber value) -> Ok (box (string value))
        | "System.String", Some (JNull) -> Error (sprintf "String value at path '%s' was null" inputPath)
        | "System.Guid", Some (JString value) ->
            match parseGuid value with
            | Some value -> Ok (box value)
            | None -> Error (sprintf "Could not parse value at path '%s' as valid GUID" inputPath)
        | "Giraffe.JsonTherapy.Types.Json", jsonValue -> Ok (box jsonValue)
        | name, None when not (isOption typeInfo) ->
            Error (sprintf "No value was found at path '%s' within the JSON" inputPath)
        | name, Some value when not (isOption typeInfo) ->
            // try parse the value automatically
            let originalJToken = convertJToken value
            Ok (originalJToken.ToObject(typeInfo))
        // no value was found for an optional value <-> return None
        | name, None when isOption typeInfo -> Ok (box None)
        | name, Some value when isOption typeInfo ->
            // option has one generic argument
            let innerType = typeInfo.GetGenericArguments().[0]
            match innerType.FullName, value with
            | "System.Boolean", JNull -> Ok (box Option<bool>.None)
            | "System.Boolean", JBool value -> Ok (box (Some value))
            | "System.Boolean", (JString ("true"|"True") | JNumber 1.0) -> Ok (box (Some true))
            | "System.Boolean", (JString ("false"|"False") | JNumber 0.0) -> Ok (box (Some false))
            | "System.Int32", JNull -> Ok (box Option<int>.None)
            | "System.Int32", JNumber n -> Ok (box (Some (int (Math.Floor(n)))))
            | "System.Int32", JString value ->
                match parseInt value with
                | Some number -> Ok (box (Some number))
                | None -> Error (sprintf "Could not parse value at path '%s' as an integer" inputPath)
            | "System.String", JString value -> Ok (box (Some value))
            | "System.String", JNumber value -> Ok (box (Some (string value)))
            | "System.String", JNull -> Ok (box Option<string>.None)
            | "System.Double", JNull -> Ok (box Option<float>.None)
            | "System.Double", JNumber value -> Ok (box (Some value))
            | "System.Double", JString value ->
                match parseFloat value with
                | Some value -> Ok (box (Some value))
                | None -> Error (sprintf "Could not parse value at path '%s' as a number" inputPath)
            | "System.Guid", JNull -> Ok (box Option<Guid>.None)
            | "System.Guid", JString value ->
                match parseGuid value with
                | Some value -> Ok (box (Some value))
                | None -> Error (sprintf "Could not parse value at path '%s' as valid GUID" inputPath)
            | "Giraffe.QueryReader.Types.Json", jsonValue -> Ok (box (Some jsonValue))
            | name, anyJson ->
                try
                    let originalToken = convertJToken anyJson
                    Ok (box (Some (originalToken.ToObject(innerType))))
                with
                | ex ->
                    let originalToken = convertJToken anyJson
                    let stringified = originalToken.ToString()
                    Error (sprintf "Could not convert %s to type %s using default deserializer" stringified name)
        | name, None -> Error (sprintf "Could not parse value at path '%s' as type %s" inputPath name)
        | name, Some anyJson ->
            try
                let originalToken = convertJToken anyJson
                Ok (box (Some (originalToken.ToObject(typeInfo))))
            with
            | ex ->
                let originalToken = convertJToken anyJson
                let stringified = originalToken.ToString()
                Error (sprintf "Could not convert %s to type %s using default deserializer" stringified name)

    let badRequest (msg: string) =
        setStatusCode 400
        >=> json (dict [ "message", msg ])

type Json() =
    static member parts<'t>(path: string, mapper: 't -> HttpHandler) =
        Extensions.request <| fun req ->
            task {
                use reader = new StreamReader(req.Body)
                let! content = reader.ReadToEndAsync()
                let inputJson = Extensions.parse content
                let typeInfo = typeof<'t>
                match Extensions.extractValue path inputJson typeInfo with
                | Error errorMsg -> return Extensions.badRequest errorMsg
                | Ok value -> return mapper (unbox<'t> value)
            }


    static member manyParts<'t>(path: string, mapper: 't list -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let typeInfo = typeof<'t>
            match inputJson with
            | JArray values ->
                let result =
                    values
                    |> List.choose (fun value ->
                        match Extensions.extractValue path value typeInfo with
                        | Error erroMsg -> None
                        | Ok part -> Some (unbox<'t> part))
                    |> mapper
                return result

            | otherwise -> return Extensions.badRequest "Expected input as JSON array"
        }

    static member manyParts<'t, 'u>(fstPath: string, sndPath: string, mapper: ('t * 'u) list -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let fstType = typeof<'t>
            let sndType = typeof<'u>
            match inputJson with
            | JArray values ->
                return
                    values
                    |> List.choose (fun value ->
                        match Extensions.extractValue fstPath value fstType with
                        | Error erroMsg -> None
                        | Ok first ->
                            match Extensions.extractValue sndPath value sndType with
                            | Error errorMsg -> None
                            | Ok second -> Some (unbox<'t> first, unbox<'u> second))
                    |> mapper

            | otherwise -> return Extensions.badRequest "Expected input as JSON array"
        }

    static member manyParts<'t, 'u, 'v>(fstPath: string, sndPath: string, thirdPath: string,  mapper: ('t * 'u * 'v) list -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let fstType = typeof<'t>
            let sndType = typeof<'u>
            let thirdType = typeof<'v>
            match inputJson with
            | JArray values ->
                return
                    values
                    |> List.choose (fun value ->
                        match Extensions.extractValue fstPath value fstType with
                        | Error erroMsg -> None
                        | Ok first ->
                            match Extensions.extractValue sndPath value sndType with
                            | Error errorMsg -> None
                            | Ok second ->
                                match Extensions.extractValue thirdPath value thirdType with
                                | Error errMsg -> None
                                | Ok third -> Some (unbox<'t> first, unbox<'u> second, unbox<'v> third))
                    |> mapper

            | otherwise -> return Extensions.badRequest "Expected input as JSON array"
        }

    static member manyParts<'t, 'u, 'v, 'q>(fstPath: string, sndPath: string, thirdPath: string, forthPath: string, mapper: ('t * 'u * 'v * 'q) list -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let fstType = typeof<'t>
            let sndType = typeof<'u>
            let thirdType = typeof<'v>
            let forthType = typeof<'q>
            match inputJson with
            | JArray values ->
                return
                    values
                    |> List.choose (fun value ->
                        match Extensions.extractValue fstPath value fstType with
                        | Error erroMsg -> None
                        | Ok first ->
                            match Extensions.extractValue sndPath value sndType with
                            | Error errorMsg -> None
                            | Ok second ->
                                match Extensions.extractValue thirdPath value thirdType with
                                | Error errMsg -> None
                                | Ok third ->
                                    match Extensions.extractValue forthPath value forthType with
                                    | Error _ -> None
                                    | Ok forth -> Some (unbox<'t> first, unbox<'u> second, unbox<'v> third, unbox<'q> forth))
                    |> mapper

            | otherwise -> return Extensions.badRequest "Expected input as JSON array"
        }

    static member manyParts<'t, 'u, 'v, 'q, 'w>(fstPath: string, sndPath: string, thirdPath: string, forthPath: string, fifthPath: string, mapper: ('t * 'u * 'v * 'q * 'w) list -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let fstType = typeof<'t>
            let sndType = typeof<'u>
            let thirdType = typeof<'v>
            let forthType = typeof<'q>
            let fifthType = typeof<'w>
            match inputJson with
            | JArray values ->
                return
                    values
                    |> List.choose (fun value ->
                        match Extensions.extractValue fstPath value fstType with
                        | Error erroMsg -> None
                        | Ok first ->
                            match Extensions.extractValue sndPath value sndType with
                            | Error errorMsg -> None
                            | Ok second ->
                                match Extensions.extractValue thirdPath value thirdType with
                                | Error errMsg -> None
                                | Ok third ->
                                    match Extensions.extractValue forthPath value forthType with
                                    | Error _ -> None
                                    | Ok forth ->
                                        match Extensions.extractValue fifthPath value fifthType with
                                        | Error _ -> None
                                        | Ok fifth ->  Some (unbox<'t> first, unbox<'u> second, unbox<'v> third, unbox<'q> forth, unbox<'w> fifth))

                    |> mapper

            | otherwise -> return Extensions.badRequest "Expected input as JSON array"
        }

    static member manyParts<'t, 'u, 'v, 'q, 'w, 'z>(fstPath: string, sndPath: string, thirdPath: string, forthPath: string, fifthPath: string, sixthPath: string, mapper: ('t * 'u * 'v * 'q * 'w * 'z) list -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let fstType = typeof<'t>
            let sndType = typeof<'u>
            let thirdType = typeof<'v>
            let forthType = typeof<'q>
            let fifthType = typeof<'w>
            let sixthType = typeof<'z>
            match inputJson with
            | JArray values ->
                return
                    values
                    |> List.choose (fun value ->
                        match Extensions.extractValue fstPath value fstType with
                        | Error erroMsg -> None
                        | Ok first ->
                            match Extensions.extractValue sndPath value sndType with
                            | Error errorMsg -> None
                            | Ok second ->
                                match Extensions.extractValue thirdPath value thirdType with
                                | Error errMsg -> None
                                | Ok third ->
                                    match Extensions.extractValue forthPath value forthType with
                                    | Error _ -> None
                                    | Ok forth ->
                                        match Extensions.extractValue fifthPath value fifthType with
                                        | Error _ -> None
                                        | Ok fifth ->
                                            match Extensions.extractValue sixthPath value sixthType with
                                            | Error _ -> None
                                            | Ok sixth -> Some (unbox<'t> first, unbox<'u> second, unbox<'v> third, unbox<'q> forth, unbox<'w> fifth, unbox<'z> sixth))
                    |> mapper

            | otherwise -> return Extensions.badRequest "Expected input as JSON array"
        }

    static member manyParts<'t, 'u, 'v, 'q, 'w, 'z, 'p>(fstPath: string, sndPath: string, thirdPath: string, forthPath: string, fifthPath: string, sixthPath: string, seventhPath: string, mapper: ('t * 'u * 'v * 'q * 'w * 'z * 'p) list -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let fstType = typeof<'t>
            let sndType = typeof<'u>
            let thirdType = typeof<'v>
            let forthType = typeof<'q>
            let fifthType = typeof<'w>
            let sixthType = typeof<'z>
            let seventhType = typeof<'p>
            match inputJson with
            | JArray values ->
                return
                    values
                    |> List.choose (fun value ->
                        match Extensions.extractValue fstPath value fstType with
                        | Error erroMsg -> None
                        | Ok first ->
                            match Extensions.extractValue sndPath value sndType with
                            | Error errorMsg -> None
                            | Ok second ->
                                match Extensions.extractValue thirdPath value thirdType with
                                | Error errMsg -> None
                                | Ok third ->
                                    match Extensions.extractValue forthPath value forthType with
                                    | Error _ -> None
                                    | Ok forth ->
                                        match Extensions.extractValue fifthPath value fifthType with
                                        | Error _ -> None
                                        | Ok fifth ->
                                            match Extensions.extractValue sixthPath value sixthType with
                                            | Error _ -> None
                                            | Ok sixth ->
                                                match Extensions.extractValue seventhPath value seventhType with
                                                | Error _ -> None
                                                | Ok seventh -> Some (unbox<'t> first, unbox<'u> second, unbox<'v> third, unbox<'q> forth, unbox<'w> fifth, unbox<'z> sixth, unbox<'p> seventh))
                    |> mapper
            | otherwise ->
                return Extensions.badRequest "Expected input as JSON array"
        }

    static member manyParts<'t, 'u, 'v, 'q, 'w, 'z, 'p, 'r>(fstPath: string, sndPath: string, thirdPath: string, forthPath: string, fifthPath: string, sixthPath: string, seventhPath: string, eighthPath: string, mapper: ('t * 'u * 'v * 'q * 'w * 'z * 'p * 'r) list -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let fstType = typeof<'t>
            let sndType = typeof<'u>
            let thirdType = typeof<'v>
            let forthType = typeof<'q>
            let fifthType = typeof<'w>
            let sixthType = typeof<'z>
            let seventhType = typeof<'p>
            let eighthType = typeof<'r>
            match inputJson with
            | JArray values ->
                return
                    values
                    |> List.choose (fun value ->
                        match Extensions.extractValue fstPath value fstType with
                        | Error erroMsg -> None
                        | Ok first ->
                            match Extensions.extractValue sndPath value sndType with
                            | Error errorMsg -> None
                            | Ok second ->
                                match Extensions.extractValue thirdPath value thirdType with
                                | Error errMsg -> None
                                | Ok third ->
                                    match Extensions.extractValue forthPath value forthType with
                                    | Error _ -> None
                                    | Ok forth ->
                                        match Extensions.extractValue fifthPath value fifthType with
                                        | Error _ -> None
                                        | Ok fifth ->
                                            match Extensions.extractValue sixthPath value sixthType with
                                            | Error _ -> None
                                            | Ok sixth ->
                                                match Extensions.extractValue seventhPath value seventhType with
                                                | Error _ -> None
                                                | Ok seventh ->
                                                    match Extensions.extractValue eighthPath value eighthType with
                                                    | Error _ -> None
                                                    | Ok eighth -> Some (unbox<'t> first, unbox<'u> second, unbox<'v> third, unbox<'q> forth, unbox<'w> fifth, unbox<'z> sixth, unbox<'p> seventh, unbox<'r> eighth))
                    |> mapper
            | otherwise ->
                return Extensions.badRequest "Expected input as JSON array"
        }
    static member parts<'t, 'u>(fstPath: string, sndPath: string, mapper: 't -> 'u -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let first = typeof<'t>
            let second = typeof<'u>
            match Extensions.extractValue fstPath inputJson first with
            | Error errorMsg -> return Extensions.badRequest errorMsg
            | Ok first ->
                match Extensions.extractValue sndPath inputJson second with
                | Error errorMsg -> return Extensions.badRequest errorMsg
                | Ok second -> return mapper (unbox<'t> first) (unbox<'u> second)
        }

    static member parts<'t, 'u, 'v>(fstPath: string, sndPath: string, thirdPath: string, mapper: 't -> 'u -> 'v -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let firstType = typeof<'t>
            let secondType = typeof<'u>
            let thirdType = typeof<'v>
            match Extensions.extractValue fstPath inputJson firstType with
            | Error errorMsg -> return Extensions.badRequest errorMsg
            | Ok first ->
                match Extensions.extractValue sndPath inputJson secondType with
                | Error errorMsg -> return Extensions.badRequest errorMsg
                | Ok second ->
                    match Extensions.extractValue thirdPath inputJson thirdType with
                    | Error errorMsg -> return Extensions.badRequest errorMsg
                    | Ok third -> return mapper (unbox<'t> first) (unbox<'u> second) (unbox<'v> third)
        }
    static member parts<'t, 'u, 'v, 'w>(fstPath: string, sndPath: string, thirdPath: string, forthPath: string, mapper: 't -> 'u -> 'v -> 'w -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let firstType = typeof<'t>
            let secondType = typeof<'u>
            let thirdType = typeof<'v>
            let forthType = typeof<'w>
            match Extensions.extractValue fstPath inputJson firstType with
            | Error errorMsg -> return Extensions.badRequest errorMsg
            | Ok first ->
                match Extensions.extractValue sndPath inputJson secondType with
                | Error errorMsg -> return Extensions.badRequest errorMsg
                | Ok second ->
                    match Extensions.extractValue thirdPath inputJson thirdType with
                    | Error errorMsg -> return Extensions.badRequest errorMsg
                    | Ok third ->
                        match Extensions.extractValue forthPath inputJson forthType  with
                        | Error errorMsg -> return Extensions.badRequest errorMsg
                        | Ok forth -> return mapper (unbox<'t> first) (unbox<'u> second) (unbox<'v> third) (unbox<'w> forth)
        }

    static member parts<'t, 'u, 'v, 'w, 'q>(fstPath: string, sndPath: string, thirdPath: string, forthPath: string, fifthPath: string, mapper: 't -> 'u -> 'v -> 'w -> 'q -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let firstType = typeof<'t>
            let secondType = typeof<'u>
            let thirdType = typeof<'v>
            let forthType = typeof<'w>
            let fifthType = typeof<'q>
            match Extensions.extractValue fstPath inputJson firstType with
            | Error errorMsg -> return Extensions.badRequest errorMsg
            | Ok first ->
                match Extensions.extractValue sndPath inputJson secondType with
                | Error errorMsg -> return Extensions.badRequest errorMsg
                | Ok second ->
                    match Extensions.extractValue thirdPath inputJson thirdType with
                    | Error errorMsg -> return Extensions.badRequest errorMsg
                    | Ok third ->
                        match Extensions.extractValue forthPath inputJson forthType  with
                        | Error errorMsg -> return Extensions.badRequest errorMsg
                        | Ok forth ->
                            match Extensions.extractValue fifthPath inputJson fifthType with
                            | Error errorMsg -> return Extensions.badRequest errorMsg
                            | Ok fifth -> return mapper (unbox<'t> first) (unbox<'u> second) (unbox<'v> third) (unbox<'w> forth) (unbox<'q> fifth)
        }

    static member parts<'t, 'u, 'v, 'w, 'q, 'y>(fstPath: string, sndPath: string, thirdPath: string, forthPath: string, fifthPath: string, sixthPath: string, mapper: 't -> 'u -> 'v -> 'w -> 'q -> 'y -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let firstType = typeof<'t>
            let secondType = typeof<'u>
            let thirdType = typeof<'v>
            let forthType = typeof<'w>
            let fifthType = typeof<'q>
            let sixthType = typeof<'y>
            match Extensions.extractValue fstPath inputJson firstType with
            | Error errorMsg -> return Extensions.badRequest errorMsg
            | Ok first ->
                match Extensions.extractValue sndPath inputJson secondType with
                | Error errorMsg -> return Extensions.badRequest errorMsg
                | Ok second ->
                    match Extensions.extractValue thirdPath inputJson thirdType with
                    | Error errorMsg -> return Extensions.badRequest errorMsg
                    | Ok third ->
                        match Extensions.extractValue forthPath inputJson forthType  with
                        | Error errorMsg -> return Extensions.badRequest errorMsg
                        | Ok forth ->
                            match Extensions.extractValue fifthPath inputJson fifthType with
                            | Error errorMsg -> return Extensions.badRequest errorMsg
                            | Ok fifth ->
                                match Extensions.extractValue sixthPath inputJson sixthType with
                                | Error errorMsg -> return Extensions.badRequest errorMsg
                                | Ok sixth -> return mapper (unbox<'t> first) (unbox<'u> second) (unbox<'v> third) (unbox<'w> forth) (unbox<'q> fifth) (unbox<'y> sixth)
        }
    static member parts<'t, 'u, 'v, 'w, 'q, 'y, 'r>(fstPath: string, sndPath: string, thirdPath: string, forthPath: string, fifthPath: string, sixthPath: string, seventhPath: string,  mapper: 't -> 'u -> 'v -> 'w -> 'q -> 'y -> 'r -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let firstType = typeof<'t>
            let secondType = typeof<'u>
            let thirdType = typeof<'v>
            let forthType = typeof<'w>
            let fifthType = typeof<'q>
            let sixthType = typeof<'y>
            let seventhType = typeof<'r>
            match Extensions.extractValue fstPath inputJson firstType with
            | Error errorMsg -> return Extensions.badRequest errorMsg
            | Ok first ->
                match Extensions.extractValue sndPath inputJson secondType with
                | Error errorMsg -> return Extensions.badRequest errorMsg
                | Ok second ->
                    match Extensions.extractValue thirdPath inputJson thirdType with
                    | Error errorMsg -> return Extensions.badRequest errorMsg
                    | Ok third ->
                        match Extensions.extractValue forthPath inputJson forthType  with
                        | Error errorMsg -> return Extensions.badRequest errorMsg
                        | Ok forth ->
                            match Extensions.extractValue fifthPath inputJson fifthType with
                            | Error errorMsg -> return Extensions.badRequest errorMsg
                            | Ok fifth ->
                                match Extensions.extractValue sixthPath inputJson sixthType with
                                | Error errorMsg -> return Extensions.badRequest errorMsg
                                | Ok sixth ->
                                    match Extensions.extractValue seventhPath inputJson seventhType with
                                    | Error errorMsg -> return Extensions.badRequest errorMsg
                                    | Ok seventh -> return mapper (unbox<'t> first) (unbox<'u> second) (unbox<'v> third) (unbox<'w> forth) (unbox<'q> fifth) (unbox<'y> sixth) (unbox<'r> seventh)
        }
    static member parts<'t, 'u, 'v, 'w, 'q, 'y, 'r, 'z>(fstPath: string, sndPath: string, thirdPath: string, forthPath: string, fifthPath: string, sixthPath: string, seventhPath: string, eighthPath: string,  mapper: 't -> 'u -> 'v -> 'w -> 'q -> 'y -> 'r -> 'z -> HttpHandler) =
        Extensions.request <| fun req -> task {
            use reader = new StreamReader(req.Body)
            let! content = reader.ReadToEndAsync()
            let inputJson = Extensions.parse content
            let firstType = typeof<'t>
            let secondType = typeof<'u>
            let thirdType = typeof<'v>
            let forthType = typeof<'w>
            let fifthType = typeof<'q>
            let sixthType = typeof<'y>
            let seventhType = typeof<'r>
            let eighthType = typeof<'z>
            match Extensions.extractValue fstPath inputJson firstType with
            | Error errorMsg -> return Extensions.badRequest errorMsg
            | Ok first ->
                match Extensions.extractValue sndPath inputJson secondType with
                | Error errorMsg -> return Extensions.badRequest errorMsg
                | Ok second ->
                    match Extensions.extractValue thirdPath inputJson thirdType with
                    | Error errorMsg -> return Extensions.badRequest errorMsg
                    | Ok third ->
                        match Extensions.extractValue forthPath inputJson forthType  with
                        | Error errorMsg -> return Extensions.badRequest errorMsg
                        | Ok forth ->
                            match Extensions.extractValue fifthPath inputJson fifthType with
                            | Error errorMsg -> return Extensions.badRequest errorMsg
                            | Ok fifth ->
                                match Extensions.extractValue sixthPath inputJson sixthType with
                                | Error errorMsg -> return Extensions.badRequest errorMsg
                                | Ok sixth ->
                                    match Extensions.extractValue seventhPath inputJson seventhType with
                                    | Error errorMsg -> return Extensions.badRequest errorMsg
                                    | Ok seventh ->
                                        match Extensions.extractValue eighthPath inputJson eighthType with
                                        | Error errorMsg -> return Extensions.badRequest errorMsg
                                        | Ok eighth -> return mapper (unbox<'t> first) (unbox<'u> second) (unbox<'v> third) (unbox<'w> forth) (unbox<'q> fifth) (unbox<'y> sixth) (unbox<'r> seventh) (unbox<'z> eighth)
        }
