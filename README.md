# Giraffe.JsonTherapy [![Build Status](https://travis-ci.org/Zaid-Ajaj/Giraffe.JsonTherapy.svg?branch=master)](https://travis-ci.org/Zaid-Ajaj/Giraffe.JsonTherapy) [![Nuget](https://img.shields.io/nuget/v/Giraffe.JsonTherapy.svg?colorB=green)](https://www.nuget.org/packages/Giraffe.JsonTherapy)

Simply extract JSON values from HTTP requests for [Giraffe](https://github.com/giraffe-fsharp/Giraffe) in a type-safe manner without defining intermediate types or decoders. Many times the input JSON is so simple that you just want to get the values so this library is great for rapid developement!

# Install
```bash
# using nuget client
dotnet add package Giraffe.JsonTherapy
# using Paket
.paket/paket.exe add Giraffe.JsonTherapy --project path/to/Your.fsproj
```

The library code is actually only a single-file: `Jsontherapy.fs` so you can add it manually to your project and modify however you want. 

## Basic Usage

Namespace `Giraffe.JsonTherapy` is opened in all examples below

The library has two functions:
 - `Json.parts` 
 - `Json.manyParts`

`Json.parts` allows you to specify the path of the JSON properties and use them directly. For example, given the following JSON for a Todo item:
```json
{ 
  "id": 1, 
  "description": "Learn F#",
  "complete": true
}
```
where both `description` and `complete` are *optional*. You want to update a an existing Todo so you can read the *values* directly as follows:
```fs
let webApp
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
        | None, Some complete -> updateJustComplete id complete
        // input JSON => { id, desc }
        | Some desc, None -> updateJustDescription id desc 
        // input JSON => { id }
        | None, None -> setStatusCode 400 >=> text "Nothing to update"
```
the first arguments (up to 8, the function is overloaded) of `Json.parts` are the names/paths of the JSON properties that you want to read. The last argument is a function that returns a `HttpHandler`. 

Because `desc` and `complete` has been infered (from usage) as optionals, then they can be omitted from the JSON whereas the `id` is infered to be `int` which means it is required to be in the input JSON:

```bash
PUT /todo/update

{ id: 1 }
# 400 Bad Request "Nothing to update" 

{ id: 1, description: "updated description" }
# 200 OK "Updating just description"  

{ id: 1, complete: false }
# 200 OK "Updating just complete" 

{ id: 1, complete: false, description: "updated description" }
# 200 OK "Updating both description and complete" 
```

## Nested properties

`Json.parts` can read nested properties by the path of the nested JSON property that you want to read. Suppose you have this JSON:
```json
{ 
  "player": { 
    "id": 1, 
    "name": "john" 
  }, 
  "role": "goal keeper"  
}
``` 
You can read all the values as follows:
```fs
POST 
  >=> route "/player"
  >=> Json.parts("player.id", "player.name", "role", 
    fun id name role -> 
      text (sprintf "Player(%s, %d) is a %s"))
```
then call the end point:
```bash
POST /player 
{ "player": { "id": 1, "name": "john" }, "role": "goal keeper" }

# 200 OK "Player(john, 1) is a goal keeper
```

## Arrays of objects
Using `Json.manyParts` you extract the properties from objects in an array of such object in JSON, for example:
```json
[
  { "product": "apple", "price": 1.50 },
  { "product": "banana", "price": 3.45 }
]
```
You can read the parts as follows:
```fs
POST 
  >=> route "/total-price"
  >=> Json.manyParts("price", 
    fun prices -> 
      prices
      |> List.sum
      |> sprintf "%.2f"
      |> text)
```
The last argument of `Json.manyParts` is a function that takes in a list of the extracted values from the input JSON value, in the last example we had one value extracted so the value `prices` was infered to be of type `float list`. 

## Multiple parts from arrays of objects
You can also extract multiple parts from the array objects and map them as a tuple in the last argument:
```fs
POST 
  >=> route "/product-names"
  >=> Json.manyParts("product", "price", 
    fun products -> 
      products
      |> List.map (fun (name, price) -> name)
      |> String.concat ", "
      |> text)
```
then
```bash
POST /product-names
[
  { "product": "apple", "price": 1.50 },
  { "product": "banana", "price": 3.45 }
]

# OK 20O "apple, banana"
```

See the tests for examples, you can also open an issue if you have questions. Missing something? PR's are very much welcome!

## Builds

![Build History](https://buildstats.info/travisci/chart/Zaid-Ajaj/Giraffe.JsonTherapy)


### Building


Make sure the following **requirements** are installed in your system:

* [dotnet SDK](https://www.microsoft.com/net/download/core) 2.0 or higher
* [Mono](http://www.mono-project.com/) if you're on Linux or macOS.

```
> build.cmd // on windows
$ ./build.sh  // on unix
```

### Watch Tests

The `WatchTests` target will use [dotnet-watch](https://github.com/aspnet/Docs/blob/master/aspnetcore/tutorials/dotnet-watch.md) to watch for changes in your lib or tests and re-run your tests on all `TargetFrameworks`

```
./build.sh WatchTests
```
