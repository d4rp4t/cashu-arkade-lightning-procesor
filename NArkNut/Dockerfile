FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
ENV NUGET_HTTP_TIMEOUT_SECONDS=30

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
ENV DOTNET_ENVIRONMENT=Production

WORKDIR /src
COPY ["NArkNut/NArkNut.csproj", "NArkNut/"]
COPY ["submodules/NArk/NArk.Swaps/NArk.Swaps.csproj", "submodules/NArk/NArk.Swaps/"]
COPY ["submodules/NArk/NArk.Core/NArk.Core.csproj", "submodules/NArk/NArk.Core/"]
COPY ["submodules/NArk/NArk.Abstractions/NArk.Abstractions.csproj", "submodules/NArk/NArk.Abstractions/"]
COPY ["submodules/NArk/NArk.Storage.EfCore/NArk.Storage.EfCore.csproj", "submodules/NArk/NArk.Storage.EfCore/"]

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore "NArkNut/NArkNut.csproj" \
    --disable-parallel \
    --use-current-runtime \
    /p:NuGetAudit=false
COPY . .
WORKDIR "/src/NArkNut"
RUN dotnet build "./NArkNut.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./NArkNut.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "NArkNut.dll"]
