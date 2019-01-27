module Tests

open Expecto
open Giraffe
open Giraffe.JsonTherapy
open System
open System.IO
open System.Linq
open System.Net.Http
open System.Collections.Generic
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open System.Net

let updateDescription (id: int) (desc: string) = 
  text "Updating just the description"

let updateComplete (id: int) (complete: bool) = 
  text "updating todo complete" 

let updateDescAndComplete (id: int) (desc: string) (complete: bool) = 
  text "updating description and complete"

let testWebApp : HttpHandler =
  choose [
    GET >=> route "/" >=> text "Index"
    PUT 
      >=> route "/todo/update" 
      >=> Json.parts("id", "description", "complete", 
        // id: int -> required 
        // desc: string option -> optional
        // complete: bool option -> optional
        fun id desc complete  ->
          match desc, complete with 
          // input JSON => { id, desc, complete }
          | Some desc, Some complete -> updateDescAndComplete id desc complete 
          // input JSON => { id, complete }
          | None, Some complete -> updateComplete id complete
          // input JSON => { id, desc }
          | Some desc, None -> updateDescription id desc 
          // input JSON => { id }
          | None, None -> setStatusCode 400 >=> text "Nothing to update"
      )
 
    setStatusCode 404 >=> text "Not Found"
  ]

let pass() = Expect.isTrue true "Passed"
let fail() = Expect.isTrue false "Failed"

let rnd = System.Random()

let appBuilder (app: IApplicationBuilder) =
  app.UseGiraffe testWebApp

let configureServices (services: IServiceCollection) =
  services.AddGiraffe()
  |> ignore

let createHost() =
    WebHostBuilder()
        .UseContentRoot(Directory.GetCurrentDirectory())
        .Configure(Action<IApplicationBuilder> (appBuilder))
        .ConfigureServices(Action<IServiceCollection> configureServices)

let withClientFor (webApp: HttpHandler) (map: HttpClient -> unit) =
    let host = 
      WebHostBuilder()
        .UseContentRoot(Directory.GetCurrentDirectory())
        .Configure(Action<IApplicationBuilder> (fun app -> app.UseGiraffe webApp))
        .ConfigureServices(Action<IServiceCollection> configureServices)
    
    use server = new TestServer(host)
    use client = server.CreateClient()
    map client

let runTask task =
    task
    |> Async.AwaitTask
    |> Async.RunSynchronously

let httpGet (path : string) (client : HttpClient) =
    path
    |> client.GetAsync
    |> runTask

let put (path: string) (content: string) (client: HttpClient) = 
  let httpContent = new StringContent(content)
  client.PutAsync(path, httpContent)
  |> runTask

let post (path: string) (content: string) (client: HttpClient) = 
  let httpContent = new StringContent(content)
  client.PostAsync(path, httpContent)
  |> runTask

let isStatus (code : HttpStatusCode) (response : HttpResponseMessage) =
    Expect.equal response.StatusCode code "Status code is wrong"
    response

let ensureSuccess (response : HttpResponseMessage) =
    if not response.IsSuccessStatusCode
    then response.Content.ReadAsStringAsync() |> runTask |> failwithf "%A"
    else response

let readText (response : HttpResponseMessage) =
    response.Content.ReadAsStringAsync()
    |> runTask

let readTextEqual content (response : HttpResponseMessage) =
    response.Content.ReadAsStringAsync()
    |> runTask
    |> fun result -> Expect.equal result content "The expected and actual response content are not equal"


[<Tests>]
let tests =
  testList "Giraffe.QueryReader" [
    testCase "Nested properties can be extracted from JSON" <| fun _ ->
      let inputJson = """ { "player": { "id": 1, "name": "john" } }  """
      let playerId = "player.id"
      let playerName = "player.name"
      let json = Extensions.parse inputJson
      match Extensions.readPath (Extensions.getJPath playerId) json with 
      | Some (JNumber 1.0) -> pass() 
      | otherwise -> fail()

      match Extensions.readPath (Extensions.getJPath playerName) json with 
      | Some (JString "john") -> pass() 
      | otherwise -> fail()

    testCase "Root path / returns 'Index' as text" <| fun _ ->
      use server = new TestServer(createHost())
      use client = server.CreateClient()

      client
      |> httpGet "/"
      |> isStatus HttpStatusCode.OK
      |> readTextEqual "Index"

    testCase "Unknown path returns status 404 not found " <| fun _ ->
      use server = new TestServer(createHost())
      use client = server.CreateClient()

      client
      |> httpGet "/non-existent-path"
      |> isStatus HttpStatusCode.NotFound
      |> readTextEqual "Not Found"

    testCase "Basic use cases" <| fun _ ->
      use server = new TestServer(createHost())
      use client = server.CreateClient()

      client 
      |> put "/todo/update" "{ \"id\": 1 }"
      |> isStatus HttpStatusCode.BadRequest
      |> readTextEqual "Nothing to update"

      client 
      |> put "/todo/update" "{ \"id\": 1, \"description\": \"description\" }"
      |> ensureSuccess
      |> readTextEqual "Updating just the description" 

      client 
      |> put "/todo/update" "{ \"id\": 1, \"complete\": true }"
      |> ensureSuccess
      |> readTextEqual "updating todo complete" 

      client 
      |> put "/todo/update" "{ \"id\": 1, \"complete\": true, \"description\": \"description\" }"
      |> ensureSuccess 
      |> readTextEqual "updating description and complete"

    testCase "Nested properties can be extracted" <| fun _ ->
      let webApp = 
        POST
        >=> route "/extract" 
        >=> Json.parts("player.id", "player.name", "role", 
          fun id name role -> text (sprintf "Player(%d, %s) is a %s" id name role)) 
       
      withClientFor webApp <| fun client ->
        let inputJson = """ { "player": { "id": 1, "name": "john" }, "role": "goal keeper" }  """
        client
        |> post "/extract" inputJson
        |> readTextEqual "Player(1, john) is a goal keeper"

    testCase "Json.manyParts works for single paramters" <| fun _ ->
        let webApp =
          POST
          >=> route "/extract"
          >=> Json.manyParts("value", List.sum >> sprintf "%d" >> text)

        withClientFor webApp (post "/extract" "[{ \"value\": 10 }, { \"value\": 5 }]" >> readTextEqual "15")

    testCase "Json.manyParts works for single paramters with nested properties" <| fun _ ->
        let webApp =
          POST
          >=> route "/extract"
          >=> Json.manyParts("value.role", String.concat ", " >> text)

        withClientFor webApp (post "/extract" "[{ \"value\": { \"role\": \"admin\" } }, { \"value\": { \"role\": \"user\" } }]" >> readTextEqual "admin, user")

    testCase "Json.parts works with simple use cases" <| fun _ ->
        let webApp =
            POST
            >=> route "/extract"
            >=> Json.parts("value", "id", fun value id -> text (sprintf "Value(%s) = %d" value id))

        withClientFor webApp (post "/extract" "{ \"value\": \"one\", \"id\": 1 }" >> readTextEqual "Value(one) = 1")

    testCase "Json.manyParts works with multi parameters" <| fun _ ->
        let webApp =
          POST
          >=> route "/extract"
          >=> Json.manyParts("value", "id", function
            | [ ("hello", 1); ("there", 2) ] -> text "pass"
            | otheriwse -> failwith "does not work")

        withClientFor webApp (post "/extract" "[{ \"value\": \"hello\", \"id\": 1 }, { \"value\": \"there\", \"id\": 2 }]" >> readTextEqual "pass")


    testCase "Null string in JSON becomes None" <| fun _ ->
      let webApp = 
        POST 
        >=> route "/extract"
        >=> Json.parts("name", 
              function
              | None -> text "Name was null or missing"
              | Some name -> text name)

      withClientFor webApp (post "/extract" "{ \"name\": null }" >> readTextEqual "Name was null or missing")
      withClientFor webApp (post "/extract" "{ \"name\": \"non-empty\" }" >> readTextEqual "non-empty")
      withClientFor webApp (post "/extract" "{ }" >> readTextEqual "Name was null or missing" )

    testCase "array of strings can be extracted" <| fun _ ->
      let webApp = Json.parts("roles", Seq.ofArray >> String.concat ", " >> text)
      withClientFor webApp (post "/" "{ \"roles\": [\"one\", \"two\"] }" >> readTextEqual "one, two")

    testCase "list of strings can be extracted" <| fun _ ->
      let webApp = Json.parts("roles", Seq.ofList >> String.concat ", " >> text)
      withClientFor webApp (post "/" "{ \"roles\": [\"one\", \"two\"] }" >> readTextEqual "one, two")

    testCase "list of ints can be extracted" <| fun _ ->
      let webApp = Json.parts("numbers", Seq.ofList >> Seq.sum >> sprintf "%d" >> text)
      withClientFor webApp (post "/" "{ \"numbers\": [1,2,3,4,5] }" >> readTextEqual "15")
  ]
