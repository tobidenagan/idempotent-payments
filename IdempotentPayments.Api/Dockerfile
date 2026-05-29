FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY NuGet.Config ./
COPY IdempotentPayments.Api/IdempotentPayments.Api.csproj IdempotentPayments.Api/
RUN dotnet restore IdempotentPayments.Api/IdempotentPayments.Api.csproj --configfile NuGet.Config

COPY IdempotentPayments.Api/ IdempotentPayments.Api/
RUN dotnet publish IdempotentPayments.Api/IdempotentPayments.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "IdempotentPayments.Api.dll"]
