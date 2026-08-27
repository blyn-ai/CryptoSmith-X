# One build stage publishes all three deployables; three final stages pick one each.
# Build context is the repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/CryptoSmithX.Database/CryptoSmithX.Database.csproj                     src/CryptoSmithX.Database/
COPY src/CryptoSmithX.MarketData.Connectors/CryptoSmithX.MarketData.Connectors.csproj src/CryptoSmithX.MarketData.Connectors/
COPY src/CryptoSmithX.MarketData.Hub/CryptoSmithX.MarketData.Hub.csproj         src/CryptoSmithX.MarketData.Hub/
COPY src/CryptoSmithX.MarketData.Api/CryptoSmithX.MarketData.Api.csproj         src/CryptoSmithX.MarketData.Api/
RUN dotnet restore src/CryptoSmithX.Database/CryptoSmithX.Database.csproj \
 && dotnet restore src/CryptoSmithX.MarketData.Hub/CryptoSmithX.MarketData.Hub.csproj \
 && dotnet restore src/CryptoSmithX.MarketData.Api/CryptoSmithX.MarketData.Api.csproj

COPY src/ src/
RUN dotnet publish src/CryptoSmithX.Database/CryptoSmithX.Database.csproj         -c Release -o /app/migrator --no-restore \
 && dotnet publish src/CryptoSmithX.MarketData.Hub/CryptoSmithX.MarketData.Hub.csproj -c Release -o /app/hub --no-restore \
 && dotnet publish src/CryptoSmithX.MarketData.Api/CryptoSmithX.MarketData.Api.csproj -c Release -o /app/api --no-restore

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
