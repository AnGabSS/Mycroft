# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props Mycroft.sln ./
COPY src/Mycroft.Core/Mycroft.Core.csproj src/Mycroft.Core/
COPY src/Mycroft.Infrastructure/Mycroft.Infrastructure.csproj src/Mycroft.Infrastructure/
COPY src/Mycroft.Api/Mycroft.Api.csproj src/Mycroft.Api/
RUN dotnet restore src/Mycroft.Api/Mycroft.Api.csproj
COPY src/ src/
RUN dotnet publish src/Mycroft.Api/Mycroft.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
COPY --from=build /app/publish .
# Non-root user shipped with the official .NET images
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mycroft.Api.dll"]
