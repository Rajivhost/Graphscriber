namespace Graphscriber.AspNetCore

open System
open System.Net.WebSockets
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Newtonsoft.Json
open FSharp.Data.GraphQL
open FSharp.Data.GraphQL.Execution

[<AutoOpen>]
module internal WebSocketUtils =
    let sendMessage message (converter : JsonConverter) (socket : WebSocket) =
        async {
            let settings =
                converter
                |> Seq.singleton
                |> jsonSerializerSettings
            let json = JsonConvert.SerializeObject(message, settings)
            let buffer = utf8Bytes json
            let segment = ArraySegment<byte>(buffer)
            do! socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None) |> Async.AwaitTask
        } |> Async.StartAsTask :> Task

    let receiveMessage<'T> (converter : JsonConverter) (socket : WebSocket) =
        async {
            let buffer = Array.zeroCreate 4096
            let segment = ArraySegment<byte>(buffer)
            do! socket.ReceiveAsync(segment, CancellationToken.None)
                |> Async.AwaitTask
                |> Async.Ignore
            let message = utf8String buffer
            if isNullOrWhiteSpace message
            then
                return None
            else
                let settings =
                    converter
                    |> Seq.singleton
                    |> jsonSerializerSettings
                return JsonConvert.DeserializeObject<'T>(message, settings) |> Some
        } |> Async.StartAsTask

type IGQLServerSocket =
    inherit IDisposable
    abstract member Subscribe : string * IDisposable -> unit
    abstract member Unsubscribe : string -> unit
    abstract member UnsubscribeAll : unit -> unit
    abstract member Id : Guid
    abstract member SendAsync : GQLServerMessage -> Task
    abstract member ReceiveAsync : unit -> Task<GQLClientMessage option>
    abstract member State : WebSocketState
    abstract member CloseAsync : unit -> Task
    abstract member CloseStatus : WebSocketCloseStatus option
    abstract member CloseStatusDescription : string option

type [<Sealed>] GQLServerSocket (inner : WebSocket) =
    let subscriptions : IDictionary<string, IDisposable> = 
        upcast ConcurrentDictionary<string, IDisposable>()

    let id = System.Guid.NewGuid()

    member __.Subscribe(id : string, unsubscriber : IDisposable) =
        subscriptions.Add(id, unsubscriber)

    member __.Unsubscribe(id : string) =
        match subscriptions.ContainsKey(id) with
        | true ->
            subscriptions.[id].Dispose()
            subscriptions.Remove(id) |> ignore
        | false -> ()

    member __.UnsubscribeAll() =
        subscriptions
        |> Seq.iter (fun x -> x.Value.Dispose())
        subscriptions.Clear()
        
    member __.Id = id

    member __.SendAsync(message: GQLServerMessage) =
        let converter = GQLServerMessageConverter() :> JsonConverter
        sendMessage message converter inner

    member __.ReceiveAsync() =
        let converter = GQLClientMessageConverter() :> JsonConverter
        receiveMessage<GQLClientMessage> converter inner

    member __.Dispose = inner.Dispose

    member __.CloseAsync() = 
        inner.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)

    member __.State = inner.State

    member __.CloseStatus = inner.CloseStatus |> Option.ofNullable

    member __.CloseStatusDescription = inner.CloseStatusDescription |> Option.ofObj

    interface IDisposable with
        member this.Dispose() = this.Dispose()

    interface IGQLServerSocket with
        member this.Subscribe(id, unsubscriber) = this.Subscribe(id, unsubscriber)
        member this.Unsubscribe(id) = this.Unsubscribe(id)
        member this.UnsubscribeAll() = this.UnsubscribeAll()
        member this.Id = this.Id
        member this.SendAsync(message) = this.SendAsync(message)
        member this.ReceiveAsync() = this.ReceiveAsync()
        member this.State = this.State
        member this.CloseAsync() = this.CloseAsync()
        member this.CloseStatus = this.CloseStatus
        member this.CloseStatusDescription = this.CloseStatusDescription

[<AllowNullLiteral>]
type IGQLServerSocketManager<'Root> =
    abstract member StartSocket : IGQLServerSocket * Executor<'Root> * 'Root -> Task

[<AllowNullLiteral>]
type GQLServerSocketManager<'Root>() =
    let sockets : IDictionary<Guid, IGQLServerSocket> = 
        upcast ConcurrentDictionary<Guid, IGQLServerSocket>()

    let disposeSocket (socket : IGQLServerSocket) =
        sockets.Remove(socket.Id) |> ignore
        socket.Dispose()

    let sendMessage (socket : IGQLServerSocket) (message : GQLServerMessage) = 
        async {
            if socket.State = WebSocketState.Open then
                do! socket.SendAsync(message) |> Async.AwaitTask
            else
                disposeSocket socket
        }

    let receiveMessage (socket : IGQLServerSocket) =
        socket.ReceiveAsync() |> Async.AwaitTask

    let handleMessages (executor : Executor<'Root>) (root : 'Root) (socket : IGQLServerSocket) = async {
        let send id output =
            Data (id, output)
            |> sendMessage socket
            |> Async.RunSynchronously
        let handle id =
            function
            | Stream output ->
                let unsubscriber = output |> Observable.subscribe (fun o -> send id o)
                socket.Subscribe(id, unsubscriber)
            | Deferred (data, _, output) ->
                send id data
                let unsubscriber = output |> Observable.subscribe (fun o -> send id o)
                socket.Subscribe(id, unsubscriber)
            | Direct (data, _) ->
                send id data
        try
            let mutable loop = true
            while loop do
                let! message = socket |> receiveMessage
                match message with
                | Some ConnectionInit ->
                    do! sendMessage socket ConnectionAck
                | Some (Start (id, payload)) ->
                    executor.AsyncExecute(payload.Query, root, payload.Variables)
                    |> Async.RunSynchronously
                    |> handle id
                    do! Data (id, Dictionary<string, obj>()) |> sendMessage socket
                | Some ConnectionTerminate ->
                    do! socket.CloseAsync() |> Async.AwaitTask
                    disposeSocket socket
                    loop <- false
                | Some (ParseError (id, _)) ->
                    do! Error (id, "Socket message parsing failed.") |> sendMessage socket
                | Some (Stop id) ->
                    socket.Unsubscribe(id)
                    do! Complete id |> sendMessage socket
                | None -> ()
        with
        | _ -> disposeSocket socket
    }

    member __.StartSocket(socket : IGQLServerSocket, executor : Executor<'Root>, root : 'Root) =
        sockets.Add(socket.Id, socket)
        handleMessages executor root socket |> Async.StartAsTask :> Task

    interface IGQLServerSocketManager<'Root> with
        member this.StartSocket(socket, executor, root) = this.StartSocket(socket, executor, root)

type IGQLClientSocket =
    inherit IDisposable
    abstract member SendAsync : GQLClientMessage -> Task
    abstract member ReceiveAsync : unit -> Task<GQLServerMessage option>
    abstract member State : WebSocketState
    abstract member CloseAsync : unit -> Task
    abstract member CloseStatus : WebSocketCloseStatus option
    abstract member CloseStatusDescription : string option

type [<Sealed>] GQLClientSocket (inner : WebSocket) =
    member __.SendAsync(message: GQLClientMessage) =
        let converter = GQLClientMessageConverter() :> JsonConverter
        sendMessage message converter inner

    member __.ReceiveAsync() =
        let converter = GQLServerMessageConverter() :> JsonConverter
        receiveMessage<GQLServerMessage> converter inner

    member __.Dispose = inner.Dispose

    member __.CloseAsync() = 
        inner.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)

    member __.State = inner.State

    member __.CloseStatus = inner.CloseStatus |> Option.ofNullable

    member __.CloseStatusDescription = inner.CloseStatusDescription |> Option.ofObj

    interface IDisposable with
        member this.Dispose() = this.Dispose()

    interface IGQLClientSocket with
        member this.SendAsync(message) = this.SendAsync(message)
        member this.ReceiveAsync() = this.ReceiveAsync()
        member this.State = this.State
        member this.CloseAsync() = this.CloseAsync()
        member this.CloseStatus = this.CloseStatus
        member this.CloseStatusDescription = this.CloseStatusDescription