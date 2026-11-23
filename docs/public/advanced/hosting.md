---
title: Hosting requirements and sign-in limits
order: 30
description: What a host must set, rate limits, and running behind a proxy.
---

## Host requirements

A host application that uses `PiranhaCommerce.Admin` must set this in its `.csproj`:

```xml
<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
```

The admin uses Blazor Interactive Server. Without the property the host does not receive `_framework/blazor.web.js`,
so pages render but never become interactive. A package cannot set it for you (it is decided at restore time, before
package build files exist). The admin logs a warning at startup if the script is missing.

## Sign-in limits and proxies

Accounts lock for a while after 5 wrong passwords. On top of that, `POST /api/v1/auth/token` and the admin sign-in form allow
10 attempts a minute per client address (answer `429` with `Retry-After`). Change it under `Piranha:RateLimit`
(`SignInAttempts`, `WindowSeconds`, or `Enabled: false` if a gateway already limits). It is on automatically once
`AddPiranhaApi()` is called; the host does not need to add middleware.

The limit counts the address the app sees. **Behind a load balancer or reverse proxy** (Azure App Service, Front Door, nginx),
configure forwarded headers so the app sees the real client. Otherwise every visitor shares the proxy's address and ten sign-ins
a minute is the limit for everybody:

```csharp
builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
// ...and app.UseForwardedHeaders(); first in the pipeline, with KnownProxies/KnownNetworks set to your proxy.
```
