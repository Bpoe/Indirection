FROM mcr.microsoft.com/dotnet/aspnet:10.0-azurelinux3.0-distroless
USER $APP_UID
WORKDIR /data
WORKDIR /app
COPY artifacts/portable/ ./
COPY docker/appsettings.json ./appsettings.json
EXPOSE 8080
ENTRYPOINT ["dotnet", "Indirection.dll"]
