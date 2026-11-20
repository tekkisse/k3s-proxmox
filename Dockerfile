FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/K3sProxmox.Web/K3sProxmox.Web.csproj src/K3sProxmox.Web/
RUN dotnet restore src/K3sProxmox.Web/K3sProxmox.Web.csproj
COPY src/ src/
RUN dotnet publish src/K3sProxmox.Web/K3sProxmox.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "K3sProxmox.Web.dll"]
