# One build stage publishes all six deployables; six final stages pick one each.
# Build context is the repository root (the WebApp.Admin links src/web/ds, so it must be reachable).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/CryptoSmithX.Database/CryptoSmithX.Database.csproj                     src/CryptoSmithX.Database/
COPY src/CryptoSmithX.MarketData.Connectors/CryptoSmithX.MarketData.Connectors.csproj src/CryptoSmithX.MarketData.Connectors/
COPY src/CryptoSmithX.MarketData.Hub/CryptoSmithX.MarketData.Hub.csproj         src/CryptoSmithX.MarketData.Hub/
COPY src/CryptoSmithX.MarketData.Api/CryptoSmithX.MarketData.Api.csproj         src/CryptoSmithX.MarketData.Api/
COPY src/CryptoSmithX.WebApp.Admin/CryptoSmithX.WebApp.Admin.csproj                         src/CryptoSmithX.WebApp.Admin/
COPY src/CryptoSmithX.WebApp.Studio/CryptoSmithX.WebApp.Studio.csproj                           src/CryptoSmithX.WebApp.Studio/
COPY src/CryptoSmithX.WebApp.Agent/CryptoSmithX.WebApp.Agent.csproj                             src/CryptoSmithX.WebApp.Agent/
RUN dotnet restore src/CryptoSmithX.Database/CryptoSmithX.Database.csproj \
 && dotnet restore src/CryptoSmithX.MarketData.Hub/CryptoSmithX.MarketData.Hub.csproj \
 && dotnet restore src/CryptoSmithX.MarketData.Api/CryptoSmithX.MarketData.Api.csproj \
 && dotnet restore src/CryptoSmithX.WebApp.Admin/CryptoSmithX.WebApp.Admin.csproj \
 && dotnet restore src/CryptoSmithX.WebApp.Studio/CryptoSmithX.WebApp.Studio.csproj \
 && dotnet restore src/CryptoSmithX.WebApp.Agent/CryptoSmithX.WebApp.Agent.csproj

COPY src/ src/
RUN dotnet publish src/CryptoSmithX.Database/CryptoSmithX.Database.csproj         -c Release -o /app/migrator --no-restore \
 && dotnet publish src/CryptoSmithX.MarketData.Hub/CryptoSmithX.MarketData.Hub.csproj -c Release -o /app/hub --no-restore \
 && dotnet publish src/CryptoSmithX.MarketData.Api/CryptoSmithX.MarketData.Api.csproj -c Release -o /app/api --no-restore \
 && dotnet publish src/CryptoSmithX.WebApp.Admin/CryptoSmithX.WebApp.Admin.csproj             -c Release -o /app/webapp-admin --no-restore \
 && dotnet publish src/CryptoSmithX.WebApp.Studio/CryptoSmithX.WebApp.Studio.csproj               -c Release -o /app/studio --no-restore \
 && dotnet publish src/CryptoSmithX.WebApp.Agent/CryptoSmithX.WebApp.Agent.csproj                 -c Release -o /app/agent --no-restore

# The migrator applies the schema and exits — the runtime image is enough, no ASP.NET.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS migrator
WORKDIR /app
COPY --from=build /app/migrator ./
ENTRYPOINT ["dotnet", "CryptoSmithX.Database.dll"]

# The hub is still mostly collector loops, but it now serves one endpoint — /live, the venues' own
# sockets relayed to the studio — so it needs Kestrel and the ASP.NET runtime. The port is published
# to the compose network only: no ports: in docker-compose and no Traefik route, so /live is
# reachable from a sibling container and from nowhere else.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS hub
WORKDIR /app
COPY --from=build /app/hub ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CryptoSmithX.MarketData.Hub.dll"]

# The api serves HTTP, so it needs the ASP.NET runtime.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /app/api ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CryptoSmithX.MarketData.Api.dll"]

# The webapp-admin (platform) is an MVC app on ASP.NET; its linked wwwroot/ds comes along in publish.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS webapp-admin
WORKDIR /app
COPY --from=build /app/webapp-admin ./
ENV ASPNETCORE_HTTP_PORTS=8080
# The commit is baked into the image, not passed by the host: the version is a
# property of what was built, and a host-side variable would drift from it the
# first time someone edited compose without redeploying.
ARG SOURCE_COMMIT=""
ENV CSX_COMMIT=$SOURCE_COMMIT
EXPOSE 8080
ENTRYPOINT ["dotnet", "CryptoSmithX.WebApp.Admin.dll"]

# Studio is the public showcase — MVC on ASP.NET, its own container because a public
# surface and the admin console have different availability needs and different blast
# radii. It takes no SOURCE_COMMIT: the webapp-admin bakes the commit in to print a version
# badge for the people who operate it, and Studio has no such badge — a build argument
# whose value nothing reads is a thing that drifts unnoticed.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS webapp-studio
WORKDIR /app
COPY --from=build /app/studio ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CryptoSmithX.WebApp.Studio.dll"]

# The agent — the bot owner's own surface at /agent, behind a sign-in. Its own container for the
# same reason Studio has one: it is reached by different people, for a different purpose, and an
# afternoon of trouble on one should not be an afternoon of trouble on the other. No SOURCE_COMMIT,
# because it prints no version badge and a build argument nothing reads drifts unnoticed.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS webapp-agent
WORKDIR /app
COPY --from=build /app/agent ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CryptoSmithX.WebApp.Agent.dll"]
