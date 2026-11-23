<#
.SYNOPSIS
  Proves the NuGet packages work for a consumer: packs the libraries, builds a brand-new app OUTSIDE the repo that
  references only the .nupkg files, publishes it, runs the published output and smoke-tests API, admin and static assets.

.PARAMETER ConnectionString
  Database for the smoke test (created and migrated by the app in Development). Defaults to a throwaway LocalDB database,
  which is dropped afterwards.
#>
[CmdletBinding()]
param(
    [string]$ConnectionString,
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) "piranha-pkg-verify-$([guid]::NewGuid().ToString('N').Substring(0,8))"),
    [switch]$KeepWorkDir,
    # Negative test: build the host WITHOUT the RequiresAspNetWebAssets property and expect the startup warning + missing script.
    [switch]$OmitWebAssetsProperty
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$packages = Join-Path $repo 'artifacts/packages'
$ownsDb = -not $ConnectionString
if ($ownsDb) {
    $dbName = "PiranhaPkgVerify_$([guid]::NewGuid().ToString('N'))"
    $ConnectionString = "Server=(localdb)\MSSQLLocalDB;Database=$dbName;Trusted_Connection=True;TrustServerCertificate=True"
}

function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Run([string]$exe, [string[]]$arguments, [string]$cwd = $PWD.Path) {
    Push-Location $cwd
    try { & $exe @arguments; if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed ($LASTEXITCODE)" } } finally { Pop-Location }
}

Step 'Pack libraries'
Run dotnet @('pack', (Join-Path $repo 'PiranhaCommerce.slnx'), '-c', 'Release', '-o', $packages, '--nologo', '-v', 'q') $repo
$version = ((Get-ChildItem $packages -Filter 'PiranhaCommerce.Core.*.nupkg' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).Name `
    -replace '^PiranhaCommerce\.Core\.', '' -replace '\.nupkg$', '')
Write-Host "Package version: $version"

Step "Create consumer app in $WorkDir"
New-Item -ItemType Directory -Force $WorkDir | Out-Null
# Isolated package cache: the consumer must restore the freshly packed .nupkg files, not stale copies of the same
# version from ~/.nuget/packages (versions are not bumped between local runs).
$env:NUGET_PACKAGES = Join-Path $WorkDir 'nuget-cache'
$app = Join-Path $WorkDir 'ConsumerApp'
Run dotnet @('new', 'web', '-n', 'ConsumerApp', '-o', $app, '-f', 'net10.0', '--no-restore') $WorkDir | Out-Null

# Only the local package folder + nuget.org: proves the packages (and their dependencies) restore without project references.
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content (Join-Path $app 'nuget.config') -Encoding utf8

# What a real consumer writes: its own Program.cs (SQL Server here) using only the packages. Config + migrations come
# from the sample host (the host owns its migrations).
$sample = Join-Path $repo 'samples/PiranhaCommerce.Web'
@'
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Admin;
using PiranhaCommerce.Api;
using PiranhaCommerce.Audit.Admin;
using PiranhaCommerce.Webhooks.Admin;
using PiranhaCommerce.Core.DependencyInjection;
using PiranhaCommerce.Customers.Admin;
using PiranhaCommerce.Data;
using PiranhaCommerce.Data.SqlServer;
using PiranhaCommerce.Ecommerce;
using PiranhaCommerce.Ecommerce.Admin;
using PiranhaCommerce.Email.Smtp.Admin;
using PiranhaCommerce.Example.Warranty;
using PiranhaCommerce.Imaging.Skia;
using PiranhaCommerce.Storage.LocalDisk;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPiranhaCore()
    .AddPiranhaSqlServer(builder.Configuration.GetConnectionString("Piranha")!, typeof(Program).Assembly)
    .AddPiranhaApi()
    .AddPiranhaAdmin()
    .AddPlugin<EcommercePlugin>()
    .AddPlugin<AuditAdminPlugin>()
    .AddPlugin<WebhooksAdminPlugin>()
    .AddPlugin<CustomersAdminPlugin>()
    .AddPlugin<EcommerceAdminPlugin>()
    .AddPlugin<SmtpEmailAdminPlugin>()
    .AddPlugin<LocalDiskStoragePlugin>()
    .AddPlugin<SkiaImagingPlugin>()
    .AddPlugin<WarrantyPlugin>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<PiranhaDbContext>().Database.MigrateAsync();
var seed = app.Configuration.GetSection("Piranha:SeedAdmin");
await app.Services.EnsureAdministratorAsync(seed["Email"]!, seed["Password"]!);

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapPiranhaApi();
app.MapPiranhaAdmin();
app.Run();
'@ | Set-Content (Join-Path $app 'Program.cs') -Encoding utf8
Copy-Item (Join-Path $sample 'appsettings.json') $app -Force
Copy-Item (Join-Path $sample 'appsettings.Development.json') $app -Force
Copy-Item (Join-Path $sample 'Migrations') (Join-Path $app 'Migrations') -Recurse -Force
# The third-party example plugin is compiled into the consumer from source, against the packages only (it is not itself a package).
$warranty = Join-Path $repo 'samples/PiranhaCommerce.Example.Warranty'
Get-ChildItem $warranty -Include *.cs,*.razor -Recurse | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | ForEach-Object { Copy-Item $_.FullName $app -Force }

# (RequiresAspNetWebAssets is a documented host requirement, see README.)
# Reference ONLY the two top-level plugin packages + the provider; everything else must arrive transitively.
foreach ($id in 'PiranhaCommerce.Audit.Admin', 'PiranhaCommerce.Webhooks.Admin', 'PiranhaCommerce.Ecommerce.Admin', 'PiranhaCommerce.Customers.Admin', 'PiranhaCommerce.Email.Smtp.Admin', 'PiranhaCommerce.Storage.LocalDisk', 'PiranhaCommerce.Imaging.Skia', 'PiranhaCommerce.Data.SqlServer') {
    Run dotnet @('add', 'package', $id, '--version', $version, '--no-restore') $app | Out-Null
}
Run dotnet @('add', 'package', 'Microsoft.EntityFrameworkCore.Design', '--version', '10.0.15', '--no-restore') $app | Out-Null
$csproj = Join-Path $app 'ConsumerApp.csproj'
$extra = if ($OmitWebAssetsProperty) { '' } else { '<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>' }
(Get-Content $csproj -Raw).Replace('<Nullable>enable</Nullable>', "<Nullable>enable</Nullable><TreatWarningsAsErrors>false</TreatWarningsAsErrors>$extra") | Set-Content $csproj -Encoding utf8

Step 'Publish consumer (Release)'
$publish = Join-Path $WorkDir 'publish'
Run dotnet @('publish', $csproj, '-c', 'Release', '-o', $publish, '--nologo', '-v', 'q') $app

Step 'Run published output'
$port = (& { $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); $p })
$base = "http://localhost:$port"
$env:ASPNETCORE_ENVIRONMENT = 'Development'          # applies migrations + seeds admin in the throwaway DB
$env:ConnectionStrings__Piranha = $ConnectionString
$log = Join-Path $WorkDir 'app.log'
$proc = Start-Process dotnet -ArgumentList @((Join-Path $publish 'ConsumerApp.dll'), '--urls', $base) `
    -WorkingDirectory $publish -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err"
$failures = @()
try {
    $ready = $false
    for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
        Start-Sleep 1
        if ($proc.HasExited) { throw "App exited early. $(Get-Content $log, "$log.err" -ErrorAction SilentlyContinue | Select-Object -Last 15 | Out-String)" }
        try { $ready = (Invoke-WebRequest "$base/admin/login" -SkipHttpErrorCheck -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { }
    }
    if (-not $ready) { throw 'App did not become ready in 60s.' }

    function Check([string]$name, [scriptblock]$test) {
        try { if (& $test) { Write-Host "  PASS  $name" -ForegroundColor Green } else { Write-Host "  FAIL  $name" -ForegroundColor Red; $script:failures += $name } }
        catch { Write-Host "  FAIL  $name ($($_.Exception.Message))" -ForegroundColor Red; $script:failures += $name }
    }
    Step 'Smoke tests'
    Check 'admin login page renders' { (Invoke-WebRequest "$base/admin/login" -UseBasicParsing).Content -match 'Sign in' }
    Check 'admin.css served from the NuGet package (static asset)' {
        $r = Invoke-WebRequest "$base/_content/PiranhaCommerce.Admin/admin.css" -UseBasicParsing
        $r.StatusCode -eq 200 -and $r.Headers['Content-Type'] -match 'text/css' -and $r.Content -match '--brand'
    }
    $logText = { (Get-Content $log, "$log.err" -ErrorAction SilentlyContinue) -join "`n" }
    $scriptStatus = (Invoke-WebRequest "$base/_framework/blazor.web.js" -SkipHttpErrorCheck -UseBasicParsing).StatusCode
    $warned = (& $logText) -match 'blazor\.web\.js was not found'
    if ($OmitWebAssetsProperty) {
        Check 'NEGATIVE: script missing (404) and startup warning logged' { $scriptStatus -eq 404 -and $warned }
    } else {
        Check 'blazor.web.js served from published host' { $scriptStatus -eq 200 }
        Check 'no missing-script warning in log' { -not $warned }
    }
    Check 'anonymous API call is 401' { (Invoke-WebRequest "$base/api/v1/products" -SkipHttpErrorCheck -UseBasicParsing).StatusCode -eq 401 }
    $token = $null
    Check 'token issued for seeded admin' {
        $r = Invoke-RestMethod "$base/api/v1/auth/token" -Method Post -ContentType 'application/json' `
            -Body (@{ email = 'admin@piranha.local'; password = 'Dev-Passw0rd!' } | ConvertTo-Json)
        $script:token = $r.accessToken; [bool]$r.accessToken
    }
    $h = @{ Authorization = "Bearer $script:token" }
    $productId = $null
    Check 'create product (catalog package via API)' {
        $r = Invoke-RestMethod "$base/api/v1/products" -Method Post -Headers $h -ContentType 'application/json' -Body '{"name":"Pkg test","sku":"PKG-1","price":2.5}'
        $script:productId = $r.id; [bool]$r.id
    }
    Check 'ecommerce listing endpoint works (ecommerce package + cross-package FK)' {
        (Invoke-RestMethod "$base/api/v1/products/$script:productId/listing" -Method Put -Headers $h -ContentType 'application/json' -Body '{"published":true}').published
    }
    Check 'customers package works (own schema, address book)' {
        $c = Invoke-RestMethod "$base/api/v1/customers" -Method Post -Headers $h -ContentType 'application/json' -Body '{"email":"pkg@example.com","lastName":"Pkg"}'
        $a = Invoke-RestMethod "$base/api/v1/customers/$($c.id)/addresses" -Method Post -Headers $h -ContentType 'application/json' -Body '{"line1":"1 High St","city":"Leeds","countryCode":"gb"}'
        $a.countryCode -eq 'GB'
    }
    Check 'basket, checkout and stock work together (ecommerce orders, shared stock ledger, order tables from host migration)' {
        Invoke-RestMethod "$base/api/v1/products/$script:productId/stock/adjustments" -Method Post -Headers $h -ContentType 'application/json' -Body '{"delta":5,"reason":"Delivery"}' | Out-Null
        $b = Invoke-RestMethod "$base/api/v1/baskets" -Method Post -Headers $h -ContentType 'application/json' -Body '{}'
        Invoke-RestMethod "$base/api/v1/baskets/$($b.id)/lines/$script:productId" -Method Put -Headers $h -ContentType 'application/json' -Body '{"quantity":2}' | Out-Null
        $o = Invoke-RestMethod "$base/api/v1/baskets/$($b.id)/checkout" -Method Post -Headers $h -ContentType 'application/json' `
            -Body '{"email":"buyer@example.com","shippingAddress":{"line1":"1 High St","city":"Leeds","countryCode":"gb"}}'
        $stock = Invoke-RestMethod "$base/api/v1/products/$script:productId/stock" -Headers $h
        ($o.stage -eq 'Placed') -and ($o.total -eq 5) -and ($o.number -ge 1001) -and ($stock.quantity -eq 3)
    }
    Check 'shipping, tax and payments work (order totals, payment ledger, tables from host migration)' {
        $m = Invoke-RestMethod "$base/api/v1/shipping-methods" -Method Post -Headers $h -ContentType 'application/json' -Body '{"name":"Standard","price":4}'
        Invoke-RestMethod "$base/api/v1/tax-rules" -Method Post -Headers $h -ContentType 'application/json' -Body '{"countryCode":"GB","name":"VAT","rate":20}' | Out-Null
        Invoke-RestMethod "$base/api/v1/products/$script:productId/stock/adjustments" -Method Post -Headers $h -ContentType 'application/json' -Body '{"delta":5,"reason":"Delivery"}' | Out-Null
        $b = Invoke-RestMethod "$base/api/v1/baskets" -Method Post -Headers $h -ContentType 'application/json' -Body '{}'
        Invoke-RestMethod "$base/api/v1/baskets/$($b.id)/lines/$script:productId" -Method Put -Headers $h -ContentType 'application/json' -Body '{"quantity":2}' | Out-Null
        $o = Invoke-RestMethod "$base/api/v1/baskets/$($b.id)/checkout" -Method Post -Headers $h -ContentType 'application/json' `
            -Body "{`"email`":`"buyer2@example.com`",`"shippingMethodId`":`"$($m.id)`",`"shippingAddress`":{`"line1`":`"1 High St`",`"city`":`"Leeds`",`"countryCode`":`"gb`"}}"
        Invoke-RestMethod "$base/api/v1/orders/$($o.id)/payments" -Method Post -Headers $h -ContentType 'application/json' -Body "{`"amount`":$($o.total)}" | Out-Null
        $paid = Invoke-RestMethod "$base/api/v1/orders/$($o.id)" -Headers $h
        # 2 x 2.50 = 5.00 goods + 4.00 shipping = 9.00; 20% tax = 1.80; total 10.80
        ($o.total -eq 10.8) -and ($o.taxTotal -eq 1.8) -and ($paid.paymentStatus -eq 'Paid')
    }
    Check 'third-party plugin compiled against the packages works (own schema, host migration)' {
        (Invoke-RestMethod "$base/api/v1/products/$script:productId/warranty" -Method Put -Headers $h -ContentType 'application/json' -Body '{"months":24,"terms":"x"}').months -eq 24
    }
    Check 'admin sign-in works and shows both plugin packages (incl. cross-package panel)' {
        $handler = [Net.Http.HttpClientHandler]::new(); $handler.UseCookies = $true; $handler.AllowAutoRedirect = $false
        $http = [Net.Http.HttpClient]::new($handler)
        try {
            $page = $http.GetStringAsync("$base/admin/login").GetAwaiter().GetResult()
            $tok = [regex]::Match($page, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
            $fields = [Collections.Generic.Dictionary[string,string]]::new()
            $fields['__RequestVerificationToken'] = $tok; $fields['_handler'] = 'login'
            $fields['Input.Email'] = 'admin@piranha.local'; $fields['Input.Password'] = 'Dev-Passw0rd!'
            $form = [Net.Http.FormUrlEncodedContent]::new($fields)
            $login = $http.PostAsync("$base/admin/login", $form).GetAwaiter().GetResult()
            if ([int]$login.StatusCode -ne 302) { throw "login returned $([int]$login.StatusCode)" }
            $dash = $http.GetStringAsync("$base/admin").GetAwaiter().GetResult()
            $edit = $http.GetStringAsync("$base/admin/catalog/products/$script:productId").GetAwaiter().GetResult()
            ($dash -match 'Catalog Admin') -and ($dash -match 'Ecommerce Admin') -and ($edit -match 'Online store')
        } finally { $http.Dispose() }
    }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep 1
    if ($ownsDb) {
        try { Run sqlcmd @('-S', '(localdb)\MSSQLLocalDB', '-Q', "ALTER DATABASE [$dbName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$dbName];") | Out-Null } catch { Write-Warning "Could not drop $dbName" }
    }
    Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    Remove-Item Env:ConnectionStrings__Piranha -ErrorAction SilentlyContinue
    Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    if (-not $KeepWorkDir) { Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue } else { Write-Host "Kept: $WorkDir" }
}
if ($failures.Count) { Write-Host "`n$($failures.Count) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "`nAll package checks passed." -ForegroundColor Green
