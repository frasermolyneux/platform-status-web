FROM node:22-bookworm-slim AS frontend
WORKDIR /web
COPY src/MX.Platform.Status.Web/package*.json ./
RUN npm ci --ignore-scripts
COPY src/MX.Platform.Status.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/ ./src/
RUN dotnet publish src/MX.Platform.Status.Service/MX.Platform.Status.Service.csproj \
    --configuration Release --output /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
COPY --from=build /publish/ ./
COPY --from=frontend /web/dist/ ./wwwroot/
USER 10001:10001
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "MX.Platform.Status.Service.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "MX.Platform.Status.Service.dll"]
