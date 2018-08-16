namespace Graphscriber.AspNetCore.JsonConverters

open Newtonsoft.Json
open Newtonsoft.Json.Linq
open System.Reflection
open Microsoft.FSharp.Reflection
open FSharp.Data.GraphQL
open FSharp.Data.GraphQL.Types
open FSharp.Data.GraphQL.Types.Patterns
open System
open Graphscriber.AspNetCore

[<Sealed>]
type OptionConverter() =
    inherit JsonConverter()
    
    override __.CanConvert(t) = 
        t.GetTypeInfo().IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>>

    override __.WriteJson(writer, value, serializer) =
        let getFields value =
            let _, fields = FSharpValue.GetUnionFields(value, value.GetType())
            fields.[0]
        let value = 
            match value with
            | null ->null
            | _ -> getFields value
        serializer.Serialize(writer, value)

    override __.ReadJson(_, _, _, _) = raise <| NotSupportedException()

[<Sealed>]
type GQLQueryConverter<'Root>(executor : Executor<'Root>, replacements: Map<string, obj>, ?meta : Metadata) =
    inherit JsonConverter()

    override __.CanConvert(t) = t = typeof<GQLQuery>   
    
    override __.WriteJson(_, _, _) = raise <| NotSupportedException()
    
    override __.ReadJson(reader, _, _, serializer) =
        let jobj = JObject.Load reader
        let query = jobj.Property("query").Value.ToString()
        let plan = 
            match meta with
            | Some meta -> executor.CreateExecutionPlan(query, meta = meta)
            | None -> executor.CreateExecutionPlan(query)
        let varDefs = plan.Variables
        match varDefs with
        | [] -> upcast { ExecutionPlan = plan; Variables = Map.empty }
        | vs ->
            Map.iter(fun path rep -> jobj.SelectToken(path).Replace(JObject.FromObject(rep))) replacements
            let vars = JObject.Parse(jobj.Property("variables").Value.ToString())
            let variables = 
                vs
                |> List.fold (fun (acc: Map<string, obj>)(vdef: VarDef) ->
                    match vars.TryGetValue(vdef.Name) with
                    | true, jval ->
                        let v = 
                            match jval.Type with
                            | JTokenType.Null -> null
                            | JTokenType.String -> jval.ToString() :> obj
                            | _ -> jval.ToObject(vdef.TypeDef.Type, serializer)
                        Map.add (vdef.Name) v acc
                    | false, _  ->
                        match vdef.DefaultValue, vdef.TypeDef with
                        | Some _, _ -> acc
                        | _, Nullable _ -> acc
                        | None, _ -> failwithf "Variable %s has no default value and is missing!" vdef.Name) Map.empty
            upcast { ExecutionPlan = plan; Variables = variables }

[<Sealed>]
type GQLClientMessageConverter<'Root>(executor : Executor<'Root>, replacements: Map<string, obj>, ?meta : Metadata) =
    inherit JsonConverter()

    override __.CanWrite = false
    
    override __.CanConvert(t) = t = typeof<GQLClientMessage>
    
    override __.WriteJson(_, _, _) = raise <| NotSupportedException()
    
    override __.ReadJson(reader, _, _, _) =
        let jobj = JObject.Load reader
        let typ = jobj.Property("type").Value.ToString()
        match typ with
        | "connection_init" -> upcast ConnectionInit
        | "connection_terminate" -> upcast ConnectionTerminate
        | "start" ->
            let id = tryGetJsonProperty jobj "id"
            let payload = tryGetJsonProperty jobj "payload"
            match id, payload with
            | Some id, Some payload ->
                try
                    let settings = JsonSerializerSettings()
                    let queryConverter =
                        match meta with
                        | Some meta -> GQLQueryConverter(executor, replacements, meta) :> JsonConverter
                        | None -> GQLQueryConverter(executor, replacements) :> JsonConverter
                    let optionConverter = OptionConverter() :> JsonConverter
                    settings.Converters <- [| optionConverter; queryConverter |]
                    settings.ContractResolver <- Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                    let req = JsonConvert.DeserializeObject<GQLQuery>(payload, settings)
                    upcast Start(id, req)
                with e -> upcast ParseError(Some id, "Parse Failed with Exception: " + e.Message)
            | None, _ -> upcast ParseError(None, "Malformed GQL_START message, expected id field but found none")
            | _, None -> upcast ParseError(None, "Malformed GQL_START message, expected payload field but found none")
        | "stop" ->
            match tryGetJsonProperty jobj "id" with
            | Some id -> upcast Stop(id)
            | None -> upcast ParseError(None, "Malformed GQL_STOP message, expected id field but found none")
        | typ -> upcast ParseError(None, "Message Type " + typ + " is not supported!")

[<Sealed>]
type GQLServerMessageConverter() =
    inherit JsonConverter()
    
    override __.CanRead = false
    
    override __.CanConvert(t) = t = typedefof<GQLServerMessage> || t.DeclaringType = typedefof<GQLServerMessage>
    
    override __.WriteJson(writer, value, _) =
        let value = value :?> GQLServerMessage
        let jobj = JObject()
        match value with
        | ConnectionAck ->
            jobj.Add(JProperty("type", "connection_ack"))
        | ConnectionError(err) ->
            let errObj = JObject()
            errObj.Add(JProperty("error", err))
            jobj.Add(JProperty("type", "connection_error"))
            jobj.Add(JProperty("payload", errObj))
        | Error(id, err) ->
            let errObj = JObject()
            errObj.Add(JProperty("error", err))
            jobj.Add(JProperty("type", "error"))
            jobj.Add(JProperty("payload", errObj))
            jobj.Add(JProperty("id", id))
        | Data(id, result) ->
            jobj.Add(JProperty("type", "data"))
            jobj.Add(JProperty("id", id))
            jobj.Add(JProperty("payload", JObject.FromObject(result)))
        | Complete(id) ->
            jobj.Add(JProperty("type", "complete"))
            jobj.Add(JProperty("id", id))
        jobj.WriteTo(writer)
    
    override __.ReadJson(_, _, _, _) = raise <| NotSupportedException()
