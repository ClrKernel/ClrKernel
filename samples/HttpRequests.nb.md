# HTTP requests in ClrKernel

ClrKernel can run **HTTP request cells** written in the
[VS Code REST Client](https://marketplace.visualstudio.com/items?itemName=humao.rest-client)
`.http` syntax. In a notebook, set a cell's language to **HTTP** (or start it with
the `#!http` selector); in a plain `.nb.md` file, use a ` ```http ` fenced block
like the ones below. Each request runs and renders a rich response card — status,
timing, size, collapsible headers, and a pretty-printed, highlighted JSON body.

## A simple GET

```http
GET https://httpbin.org/json
Accept: application/json
```

## Variables and system variables

`@name = value` defines a variable; `{{name}}` interpolates it. System variables
like `{{$guid}}`, `{{$timestamp}}`, and `{{$randomInt 1 100}}` are built in.

```http
@host = https://httpbin.org

POST {{host}}/anything
Content-Type: application/json

{
  "requestId": "{{$guid}}",
  "at": {{$timestamp}},
  "roll": {{$randomInt 1 100}}
}
```

## Request chaining

Name a request with `# @name`, then reference an earlier response anywhere with
`{{name.response.body.$.json.path}}` or `{{name.response.headers.HeaderName}}`.
Here the second request reuses a value returned by the first.

```http
# @name create
POST https://httpbin.org/anything
Content-Type: application/json

{ "token": "{{$guid}}" }

###

GET https://httpbin.org/anything
X-Echo-Token: {{create.response.body.$.json.token}}
```

## Multiple requests in one cell

Separate requests with `###`. Each produces its own response card.

```http
GET https://httpbin.org/status/200
###
GET https://httpbin.org/status/404
###
GET https://httpbin.org/status/503
```
