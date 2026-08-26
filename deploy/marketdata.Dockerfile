# Build context is the repository root: the MarketData project links src/web/ds into wwwroot,
# so the design system has to be inside the context.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/CryptoSmithX.Exchanges/CryptoSmithX.Exchanges.csproj  src/CryptoSmithX.Exchanges/
COPY src/CryptoSmithX.MarketData/CryptoSmithX.MarketData.csproj src/CryptoSmithX.MarketData/
RUN dotnet restore src/CryptoSmithX.MarketData/CryptoSmithX.MarketData.csproj

COPY src/ src/
RUN dotnet publish src/CryptoSmithX.MarketData/CryptoSmithX.MarketData.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CryptoSmithX.MarketData.dll"]
