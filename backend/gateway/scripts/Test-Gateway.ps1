[CmdletBinding()]
param([string]$BaseUrl = '')

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
Push-Location $repoRoot
try {
    $config = docker compose --profile gateway config --format json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Compose configuration is invalid.' }
    if (-not $BaseUrl) { $BaseUrl = 'http://127.0.0.1:' + $config.services.gateway.ports[0].published }
    $BaseUrl = $BaseUrl.TrimEnd('/')
    function Check([string]$Path, [int]$Status = 200, [hashtable]$Headers = @{}, [string]$Method = 'GET', [string]$Body = '') {
        $request = @{ Uri = $BaseUrl + $Path; Headers = $Headers; Method = $Method; UseBasicParsing = $true; TimeoutSec = 20 }
        if ($Body) { $request.Body = $Body; $request.ContentType = 'application/json' }
        try { $response = Invoke-WebRequest @request }
        catch {
            if ($null -eq $_.Exception.Response) { throw }
            $actual = [int]$_.Exception.Response.StatusCode
            if ($actual -ne $Status) { throw "$Path returned $actual; expected $Status." }
            return $null
        }
        if ([int]$response.StatusCode -ne $Status) { throw "$Path returned $($response.StatusCode); expected $Status." }
        return $response
    }
    $parser = @{ 'X-Parser-Key' = $config.services.parser.environment.Parser__ApiKey }
    $user = @{ 'X-Interaction-Key' = $config.services.interaction.environment.Interaction__ApiKey; 'X-User-Id' = 'gateway:read-only-check' }
    $null = Check '/ping'
    $null = Check '/parser/health/ready'
    $null = Check '/interaction/health/ready'
    $null = Check '/search/health'
    $null = Check '/ai/health'
    $null = Check '/api/v1/parser/events?pageSize=1' 401
    $parserPage = (Check '/api/v1/parser/events?pageSize=1' 200 $parser).Content | ConvertFrom-Json
    $null = Check '/api/v1/interaction/me/subscriptions' 401
    $subscriptions = (Check '/api/v1/interaction/me/subscriptions' 200 $user).Content | ConvertFrom-Json
    $null = Check '/api/v1/interaction/admin/feedback' 401 $user
    $null = Check '/api/v1/interaction/deliveries/claim' 405
    $searchPage = (Check '/search/events?page=1&page_size=1').Content | ConvertFrom-Json
    $docs = Check '/search/docs'
    if ($docs.Content -notmatch '/search/openapi.json') { throw 'Swagger uses the wrong OpenAPI URL.' }
    $schema = (Check '/search/openapi.json').Content | ConvertFrom-Json
    if ('/search' -notin @($schema.servers | ForEach-Object { $_.url })) { throw 'Search OpenAPI server prefix is incorrect.' }
    $agentTools = (Check '/ai/api/tools').Content | ConvertFrom-Json
    if (@($agentTools).Count -ne 6 -or 'search_events' -notin @($agentTools | ForEach-Object { $_.name })) {
        throw 'AI tool catalog is incomplete.'
    }
    $null = Check '/ai/api/chat' 405
    $null = Check '/ai/api/chat' 422 @{} 'POST' '{}'
    $agentDocs = Check '/ai/docs'
    if ($agentDocs.Content -notmatch '/ai/openapi.json') { throw 'AI Swagger uses the wrong OpenAPI URL.' }
    $agentSchema = (Check '/ai/openapi.json').Content | ConvertFrom-Json
    if ('/ai' -notin @($agentSchema.servers | ForEach-Object { $_.url })) { throw 'AI OpenAPI server prefix is incorrect.' }
    if (-not $agentSchema.paths.'/api/chat'.post -or -not $agentSchema.paths.'/api/tools'.get) {
        throw 'AI OpenAPI routes are missing.'
    }
    $agentRedoc = Check '/ai/redoc'
    if ($agentRedoc.Content -notmatch '/ai/openapi.json') { throw 'AI ReDoc uses the wrong OpenAPI URL.' }
    $null = Check '/ai/docs/oauth2-redirect'
    $null = Check '/parser/openapi/v1.json'
    $null = Check '/interaction/openapi/v1.json'
    $null = Check '/api/v1/parser-other/events' 404
    $null = Check '/search-other/events' 404
    $null = Check '/ai-other/api/tools' 404
    $null = Check '/api/chat' 404
    $null = Check '/openapi.json' 404
    [pscustomobject]@{ Gateway = $BaseUrl; ParserEvents = $parserPage.total; SearchEvents = $searchPage.total;
        Subscriptions = $subscriptions.total; AiTools = @($agentTools).Count; Authorization = 'preserved'; Swagger = 'passed'; Routing = 'passed' }
} finally { Pop-Location }
