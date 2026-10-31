# syntax=docker/dockerfile:1
# Multi-arch build: docker buildx build --platform linux/amd64,linux/arm64 .
# The SDK stage runs on the build machine's own architecture and cross-publishes for the target.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/MyProject.Api/MyProject.Api.csproj src/MyProject.Api/
#if (UseWorker)
COPY src/MyProject.Worker/MyProject.Worker.csproj src/MyProject.Worker/
# Restoring the worker restores the API it references.
RUN dotnet restore src/MyProject.Worker/MyProject.Worker.csproj -a $TARGETARCH
#else
RUN dotnet restore src/MyProject.Api/MyProject.Api.csproj -a $TARGETARCH
#endif
COPY .editorconfig ./
COPY src/ src/
RUN dotnet publish src/MyProject.Api/MyProject.Api.csproj -c Release -a $TARGETARCH --no-restore -o /app/publish -p:UseAppHost=false

#if (UseDatabase)
# Migration bundle: a single executable that applies pending migrations, run before the API starts.
FROM build AS bundle
ARG TARGETARCH
COPY .config/dotnet-tools.json .config/
RUN dotnet tool restore
#if (UseNotifications || UseWebhooks)
# One bundle per context: the app's tables, then each feature's (see Data/ModuleDatabase.cs).
#endif
RUN RID="linux-$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH")" \
    && dotnet ef migrations bundle --project src/MyProject.Api --configuration Release --context AppDbContext \
       --target-runtime "$RID" --self-contained --output /app/efbundle
#if (UseNotifications)
RUN RID="linux-$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH")" \
    && dotnet ef migrations bundle --project src/MyProject.Api --configuration Release --context NotificationsDbContext \
       --target-runtime "$RID" --self-contained --output /app/efbundle-notifications
#endif
#if (UseWebhooks)
RUN RID="linux-$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH")" \
    && dotnet ef migrations bundle --project src/MyProject.Api --configuration Release --context WebhooksDbContext \
       --target-runtime "$RID" --self-contained --output /app/efbundle-webhooks
#endif

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrator
#if (UseNotifications || UseWebhooks)
COPY --from=bundle /app/efbundle* /app/
#else
COPY --from=bundle /app/efbundle /app/efbundle
#endif
#if (UseSqlite)
# The database file lives on the app-data volume; the volume takes this folder's owner.
RUN mkdir /data && chown $APP_UID /data
#endif
USER $APP_UID
#if (UseNotifications && UseWebhooks)
ENTRYPOINT ["/bin/sh", "-c", "/app/efbundle && /app/efbundle-notifications && /app/efbundle-webhooks"]
#endif
#if (UseNotifications && !UseWebhooks)
ENTRYPOINT ["/bin/sh", "-c", "/app/efbundle && /app/efbundle-notifications"]
#endif
#if (!UseNotifications && UseWebhooks)
ENTRYPOINT ["/bin/sh", "-c", "/app/efbundle && /app/efbundle-webhooks"]
#endif
#if (!UseNotifications && !UseWebhooks)
ENTRYPOINT ["/app/efbundle"]
#endif

#endif
#if (UseWorker)
FROM build AS worker-build
ARG TARGETARCH
RUN dotnet publish src/MyProject.Worker/MyProject.Worker.csproj -c Release -a $TARGETARCH --no-restore -o /app/worker -p:UseAppHost=false

# The worker image: runs the jobs the API queues. Build it with --target worker.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS worker
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
COPY --from=worker-build /app/worker .
#if (UseSqlite)
RUN mkdir /data && chown $APP_UID /data
#endif
USER $APP_UID
ENTRYPOINT ["dotnet", "MyProject.Worker.dll"]

#endif
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
#if (UseSqlite)
RUN mkdir /data && chown $APP_UID /data
#endif
# Runs as the image's non-root "app" user.
USER $APP_UID
ENTRYPOINT ["dotnet", "MyProject.Api.dll"]
