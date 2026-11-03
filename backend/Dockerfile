FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/OnkelBummel.Api/OnkelBummel.Api.csproj src/OnkelBummel.Api/
RUN dotnet restore src/OnkelBummel.Api/OnkelBummel.Api.csproj

COPY src/ src/
RUN dotnet publish src/OnkelBummel.Api/OnkelBummel.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
# Volume mount point for the database and the attachments, writable by the non-root app user.
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
ENV Storage__DataPath=/data
# 8080 for the app, 9464 only for /metrics (see Monitoring:MetricsPort)
ENV ASPNETCORE_HTTP_PORTS="8080;9464"
EXPOSE 8080 9464
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s \
  CMD ["dotnet", "OnkelBummel.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "OnkelBummel.Api.dll"]
