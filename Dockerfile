# Multi-stage build for the ApiForge API (.NET 10).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first for layer caching.
COPY ApiForge.Api/ApiForge.Api.csproj ApiForge.Api/
RUN dotnet restore ApiForge.Api/ApiForge.Api.csproj

COPY ApiForge.Api/ ApiForge.Api/
RUN dotnet publish ApiForge.Api/ApiForge.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# Run as the built-in non-root user provided by the base image.
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ApiForge.Api.dll"]
