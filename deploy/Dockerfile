# One build stage publishes all four deployables; four final stages pick one each.
# Build context is the repository root (the WebApp links src/web/ds, so it must be reachable).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/CryptoSmithX.Database/CryptoSmithX.Database.csproj                     src/CryptoSmithX.Database/
COPY src/CryptoSmithX.MarketData.Connectors/CryptoSmithX.MarketData.Connectors.csproj src/CryptoSmithX.MarketData.Connectors/
COPY src/CryptoSmithX.MarketData.Hub/CryptoSmithX.MarketData.Hub.csproj         src/CryptoSmithX.MarketData.Hub/
COPY src/CryptoSmithX.MarketData.Api/CryptoSmithX.MarketData.Api.csproj         src/CryptoSmithX.MarketData.Api/
COPY src/CryptoSmithX.WebApp/CryptoSmithX.WebApp.csproj                         src/CryptoSmithX.WebApp/
RUN dotnet restore src/CryptoSmithX.Database/CryptoSmithX.Database.csproj \
 && dotnet restore src/CryptoSmithX.MarketData.Hub/CryptoSmithX.MarketData.Hub.csproj \
 && dotnet restore src/CryptoSmithX.MarketData.Api/CryptoSmithX.MarketData.Api.csproj \
 && dotnet restore src/CryptoSmithX.WebApp/CryptoSmithX.WebApp.csproj

COPY src/ src/
RUN dotnet publish src/CryptoSmithX.Database/CryptoSmithX.Database.csproj         -c Release -o /app/migrator --no-restore \
 && dotnet publish src/CryptoSmithX.MarketData.Hub/CryptoSmithX.MarketData.Hub.csproj -c Release -o /app/hub --no-restore \
 && dotnet publish src/CryptoSmithX.MarketData.Api/CryptoSmithX.MarketData.Api.csproj -c Release -o /app/api --no-restore \
 && dotnet publish src/CryptoSmithX.WebApp/CryptoSmithX.WebApp.csproj             -c Release -o /app/webapp --no-restore

# The migrator applies the schema and exits — the runtime image is enough, no ASP.NET.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS migrator
WORKDIR /app
COPY --from=build /app/migrator ./
ENTRYPOINT ["dotnet", "CryptoSmithX.Database.dll"]

# The hub is a Generic Host worker — no Kestrel, so the runtime image is enough.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS hub
WORKDIR /app
COPY --from=build /app/hub ./
ENTRYPOINT ["dotnet", "CryptoSmithX.MarketData.Hub.dll"]

# The api serves HTTP, so it needs the ASP.NET runtime.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /app/api ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CryptoSmithX.MarketData.Api.dll"]

# The webapp (platform) is an MVC app on ASP.NET; its linked wwwroot/ds comes along in publish.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS webapp
WORKDIR /app
COPY --from=build /app/webapp ./
ENV ASPNETCORE_HTTP_PORTS=8080
# The commit is baked into the image, not passed by the host: the version is a
# property of what was built, and a host-side variable would drift from it the
# first time someone edited compose without redeploying.
ARG SOURCE_COMMIT=""
ENV CSX_COMMIT=$SOURCE_COMMIT
EXPOSE 8080
ENTRYPOINT ["dotnet", "CryptoSmithX.WebApp.dll"]
