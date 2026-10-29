# syntax=docker/dockerfile:1
# Multi-arch build: docker buildx build --platform linux/amd64,linux/arm64 .
# The SDK stage runs on the build machine's own architecture and cross-publishes for the target.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/MyProject.Api/MyProject.Api.csproj src/MyProject.Api/
RUN dotnet restore src/MyProject.Api/MyProject.Api.csproj -a $TARGETARCH
COPY .editorconfig ./
COPY src/ src/
RUN dotnet publish src/MyProject.Api/MyProject.Api.csproj -c Release -a $TARGETARCH --no-restore -o /app/publish -p:UseAppHost=false

#if (UsePostgres)
# Migration bundle: a single executable that applies pending migrations, run before the API starts.
FROM build AS bundle
ARG TARGETARCH
COPY .config/dotnet-tools.json .config/
RUN dotnet tool restore
RUN RID="linux-$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH")" \
    && dotnet ef migrations bundle --project src/MyProject.Api --configuration Release \
       --target-runtime "$RID" --self-contained --output /app/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrator
COPY --from=bundle /app/efbundle /app/efbundle
USER $APP_UID
ENTRYPOINT ["/app/efbundle"]

#endif
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
# Runs as the image's non-root "app" user.
USER $APP_UID
ENTRYPOINT ["dotnet", "MyProject.Api.dll"]
